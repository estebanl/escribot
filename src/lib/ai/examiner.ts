import {
  callLLM,
  callLLMWithMeta,
  PROVIDER_DEFAULTS,
  resolveUserAndAIConfig,
  type ResolvedAIConfig,
} from "./providers";
import {
  countEssayWords,
  extractJsonObject,
  getMinimumWords,
  L1_LANGUAGE_PROFILES,
  roundIeltsBand,
} from "@/lib/ielts-utils";
import { normalizeCartesianChartSpec, normalizeChartSpec } from "@/lib/chart-utils";
import type {
  AnnotationCategory,
  ChartSpec,
  DrillLevel,
  DrillType,
  EvaluationPayload,
  InlineAnnotation,
  MicroDrillChallenge,
  MicroDrillScore,
  MicroDrillStep,
  NativeLanguageCode,
  OutlineFeedback,
  OutlineInput,
  PromptCategory,
  TaskType,
  ToolkitRequest,
  ToolkitResponse,
} from "@/lib/types";

/* ============================================================================
 * 1. TASK 1 SCREENSHOT MULTIMODAL VISION EXTRACTION (Cached per Prompt)
 * ============================================================================ */

export async function extractVisualSpecFromImage(
  imageDataUrl: string,
  promptText: string,
  userId: string,
  config?: ResolvedAIConfig
): Promise<string> {
  const cfg = config || (await resolveUserAndAIConfig(userId));
  if (!cfg.apiKey) {
    return "Uploaded visual chart/diagram screenshot (configure API key in Settings for live multimodal chart figure extraction).";
  }

  const visionModel = cfg.visionModel || PROVIDER_DEFAULTS[cfg.provider]?.visionModel || cfg.fastModel;

  try {
    const raw = await callLLM({
      userId: cfg.userId,
      keySource: cfg.keySource,
      provider: cfg.provider,
      apiKey: cfg.apiKey,
      model: visionModel,
      fallbackModels: PROVIDER_DEFAULTS[cfg.provider]?.fastFallbacks,
      feature: "vision_extract",
      imageDataUrl,
      jsonMode: true,
      thinkingMode: "disabled",
      maxTokens: 900,
      timeoutMs: 20_000,
      systemPrompt:
        "You are an Academic IELTS Task 1 visual data analyst. Extract every objective detail from the uploaded chart, table, process diagram, or comparison map so a text-based IELTS examiner can verify a candidate's Task Achievement with 100% numeric and factual precision.",
      userPrompt: `Task prompt accompanying this image:\n${promptText}\n\nReturn a JSON object with keys:\n- "visualType": string (e.g., line graph, bar chart, pie charts, table, process diagram, map comparison)\n- "unitsAndAxes": string\n- "macroOverviewTrends": string[] (2-4 overarching trends, extremes, or transformations without drowning in minutiae)\n- "exactFiguresAndStages": string[] (comprehensive list of numbers, years, intersections, rankings, or process/map changes visible in the image)`,
    });
    return raw;
  } catch {
    return "Uploaded visual chart/diagram screenshot (verify internal factual consistency against prompt instructions).";
  }
}

/* ============================================================================
 * 2. DEEP 4-CRITERION RUBRIC EVALUATION + INLINE ANNOTATIONS + BAND 8+ REWRITE
 * ============================================================================ */

export interface EvaluateEssayInput {
  userId: string;
  taskType: TaskType;
  promptTitle: string;
  promptText: string;
  visualContext?: string | null;
  essayText: string;
  draftVersion: number;
  previousBand?: number | null;
  outline?: OutlineInput | null;
  outlineFeedback?: OutlineFeedback | null;
  coachingHistory?: ToolkitResponse[] | null;
  onProgress?: (stage: string) => void;
}

interface RubricScoreOutput {
  taTrBand?: number;
  ccBand?: number;
  lrBand?: number;
  graBand?: number;
  examinerSummary?: string;
  criteria?: Partial<EvaluationPayload["criteria"]>;
  topPriorities?: string[];
}

interface AnnotationRewriteOutput {
  inlineAnnotations?: InlineAnnotation[];
  targetBandRewrite?: string;
  rewriteKeyChanges?: string[];
  vaultItems?: EvaluationPayload["vaultItems"];
}

function getSharedPedagogyRules(taskType: TaskType): string {
  const taskSpecificRule =
    taskType === "task1"
      ? `- TASK 1 OVERVIEW RULE (CRITICAL — DO NOT CONTRADICT): The Task 1 Overview paragraph MUST be 100% free of raw numbers, percentages, and specific years. It must synthesise 2–3 overarching macro trends, dominant categories, or crossovers ONLY. Exact figures (e.g., "coal fell from 65% to 0%") belong strictly in Body Paragraphs 1 & 2, NEVER in the Overview. Never advise adding numbers or percentages to the Overview.
- TASK 1 REGISTER CONSISTENCY: Across all tools (Outline Check, Paragraph Check, Collocations, and Final Grader), avoid dramatic/journalistic metaphors ("collapsed", "skyrocketed", "crashed", "exploded"). Consistently recommend objective statistical phrasing ("declined precipitously", "fell sharply to zero", "experienced a steep contraction", "surged", "expanded rapidly").`
      : `- TASK 2 THESIS & EXTENSION RULE: The introduction must state a clear, nuanced position that is maintained throughout. Body paragraphs must develop one central claim each with specific causal mechanisms and concrete examples rather than sweeping generalisations.`;

  return `SHARED HOUSE PEDAGOGY & CONSISTENCY RULES:
${taskSpecificRule}
- FALSE COGNATES & WHOLE-PHRASE REPLACEMENTS: When "actually" is used to mean "currently / at present / in recent years" or inside a trend/growth verb phrase (e.g., "Actually, ..." or "it has actually grown very fast"), it is a Romance L1 false cognate (Spanish/Portuguese/French "actualmente/atualmente/actuellement"). NEVER keep "actually" in a suggested revision (do NOT suggest "it has actually grown very fast") and NEVER suggest swapping "actually" with an intensifier like "extremely" (which creates ungrammatical double intensifiers like "grown extremely very fast"). Instead, replace the entire verb/adverb phrase with natural academic English (e.g., "it has expanded rapidly in recent years" or "Currently, ..."). Every suggested replacement MUST be grammatically valid in its exact surrounding sentence context.
- STRICT EVIDENCE & ANTI-HALLUCINATION RULE: Every error or limiting factor mentioned in feedback MUST exist verbatim in the candidate's text. NEVER invent or assume errors from the L1 profile (such as claiming a "missing subject" when every sentence has a valid subject). Note that compound predicates sharing a single subject (e.g., "Coal stood at 65% in 1990 and fell to 0% by 2025") are grammatically complete and are NOT missing a subject.
- STRICT CATEGORY BOUNDARIES:
  * GRA (Grammatical Range & Accuracy): Comma splices (joining two independent clauses with a comma or ", however,"), run-on sentences, fragments, punctuation errors, verb tense errors, subject-verb agreement, and article/preposition grammar errors MUST be classified under GRA (with severity: "error"), NEVER under CC.
  * CC (Coherence & Cohesion): Paragraphing, topic sentences, logical progression, and replacing mechanical sentence-initial linkers ("Firstly", "Moreover", "On the other hand") with demonstrative referencing ("This shift...", "Such measures...") — never punctuation or comma splices.
  * LR (Lexical Resource): Upgrading genuinely informal vocabulary ("very fast", "a lot of", "big drop") or imprecise collocations to C1/C2 academic register.
  * L1 (Native-Language Interference): False cognates ("actually" for "actualmente", "assist to", "sensible") and direct L1 calques.
- NO LATERAL OR TRIVIAL SYNONYM SWAPS: Never suggest swapping words that are already accurate Academic English of the same register — specifically, NEVER suggest swapping "whereas" ↔ "while", "although" ↔ "even though", or "graph" ↔ "chart". Prefer fewer, genuinely high-leverage annotations over padding.`;
}

function cleanDoubleIntensifiers(text: string): string {
  return text
    .replace(/\b(extremely|very|really|highly)\s+(very|extremely|really)\b/gi, "rapidly")
    .replace(/\bgrown\s+rapidly\s+fast\b/gi, "expanded rapidly");
}

function isTrivialLateralSwap(originalSpan: string, replacement: string, explanation: string): boolean {
  const normOrig = originalSpan.trim().toLowerCase().replace(/[.,;!?]+$/g, "");
  const normRepl = replacement.trim().toLowerCase().replace(/[.,;!?]+$/g, "");
  if (normOrig === normRepl) return true;

  // Check if the only difference between originalSpan and replacement is swapping whereas <-> while or although <-> even though
  const normalizeLateral = (s: string) =>
    s
      .replace(/\b(whereas|while)\b/gi, "__CONTRAST_CONJ__")
      .replace(/\b(although|even though|though)\b/gi, "__CONCESSIVE_CONJ__")
      .replace(/\s+/g, " ")
      .trim();

  if (normalizeLateral(normOrig) === normalizeLateral(normRepl)) {
    return true;
  }

  if (
    /\bswap(?:ping)?\s+['"“”]?whereas['"“”]?\s+(?:for|with|to)\s+['"“”]?while['"“”]?/i.test(
      explanation
    ) ||
    /\breplac(?:e|ing)\s+['"“”]?whereas['"“”]?\s+with\s+['"“”]?while['"“”]?/i.test(
      explanation
    ) ||
    /\breplac(?:e|ing)\s+['"“”]?while['"“”]?\s+with\s+['"“”]?whereas['"“”]?/i.test(
      explanation
    )
  ) {
    return true;
  }

  return false;
}

function normalizeAndFilterAnnotations(
  rawAnnotations: InlineAnnotation[],
  validCategories: Set<AnnotationCategory>
): InlineAnnotation[] {
  const result: InlineAnnotation[] = [];

  for (let idx = 0; idx < rawAnnotations.length; idx++) {
    const a = rawAnnotations[idx];
    if (!a || typeof a.originalSpan !== "string" || a.originalSpan.trim().length === 0) {
      continue;
    }

    const originalSpan = a.originalSpan.trim();
    let replacement = cleanDoubleIntensifiers((a.replacement || originalSpan).trim());
    let explanation = (a.explanation || "Academic refinement suggested.").trim();
    let category: AnnotationCategory = validCategories.has(a.category) ? a.category : "LR";
    let severity: "error" | "upgrade" = a.severity === "error" ? "error" : "upgrade";

    // Filter out trivial lateral swaps (e.g. "whereas" <-> "while")
    if (isTrivialLateralSwap(originalSpan, replacement, explanation)) {
      continue;
    }

    // Fix broken "actually" handling where the model kept "actually" or produced "actually grown very fast"
    if (/\bactually\b/i.test(originalSpan)) {
      if (/\bactually\b/i.test(replacement)) {
        replacement = replacement
          .replace(/\bhas actually grown very fast\b/gi, "has expanded rapidly in recent years")
          .replace(/\bactually grown very fast\b/gi, "expanded rapidly in recent years")
          .replace(/\bactually grew very fast\b/gi, "expanded rapidly")
          .replace(/\bactually,\s*/gi, "Currently, ")
          .replace(/\bactually\s+/gi, "");
      }
      if (/\bvery fast\b/i.test(replacement)) {
        replacement = replacement
          .replace(/\bgrown very fast\b/gi, "expanded rapidly")
          .replace(/\bgrew very fast\b/gi, "expanded rapidly")
          .replace(/\bvery fast\b/gi, "rapidly");
      }
      if (/['"“”]?actually['"“”]?\s+with\s+['"“”]?extremely['"“”]?/i.test(explanation)) {
        explanation =
          "'Actually' is a Romance false cognate for 'actualmente' ('currently / in recent years'); replace the whole phrase with formal academic phrasing such as 'expanded rapidly'.";
      }
      if (validCategories.has("L1") && /false cognate|actualmente|in fact|currently/i.test(explanation)) {
        category = "L1";
        severity = "error";
      }
    }

    // Enforce GRA category for comma splices and clause/punctuation errors
    if (
      /\b(comma splice|run-on|independent clauses?|semicolon|clause boundary|subject-verb|verb tense|missing verb)\b/i.test(
        explanation
      )
    ) {
      category = "GRA";
      severity = "error";
    } else if (
      /\b(false cognate|false friend|actualmente|calque)\b/i.test(explanation) &&
      validCategories.has("L1")
    ) {
      category = "L1";
      severity = "error";
    }

    result.push({
      id: a.id || `ann-${idx + 1}`,
      category,
      severity,
      originalSpan,
      replacement,
      explanation,
    });
  }

  return result;
}

function hasLikelyMissingSubjectInEssay(essayText: string): boolean {
  // Check if any sentence starts directly with a finite auxiliary/copula without a subject ("Is important...", "Has grown...")
  return /(?:^|[.!?;]\s+)(?:is|are|was|were|has|have|had|will|would|can|could)\s+(?!there\b|it\b)[a-z]/im.test(
    essayText
  );
}

function sanitizeCriteriaFeedback(
  criteria: EvaluationPayload["criteria"],
  taskType: TaskType,
  essayText: string
): EvaluationPayload["criteria"] {
  const sanitized = structuredClone(criteria);

  // 1. Task 1 Overview must NEVER be told to include raw numbers/figures
  if (taskType === "task1") {
    const overviewHasNumbersAdvice = (text: string) =>
      /\boverview\b/i.test(text) &&
      /\b(\d+%|figures|statistics|numbers|percentages|data|from \d+)\b/i.test(text) &&
      !/\b(without|no|avoid|free of|never|rather than|instead of)\b[^.!?]{0,35}\b(numbers|figures|statistics|percentages|\d+%)/i.test(
        text
      );

    if (overviewHasNumbersAdvice(sanitized.TA_TR.howToGainHalfBand)) {
      sanitized.TA_TR.howToGainHalfBand =
        "Keep your Overview paragraph strictly number-free (2–3 macro trends and key crossovers only) and place exact start/end figures and percentage comparisons in Body Paragraphs 1 and 2.";
    }

    sanitized.TA_TR.limitingFactors = sanitized.TA_TR.limitingFactors.map((lf) =>
      overviewHasNumbersAdvice(lf)
        ? "Body paragraphs need sharper comparative figures (keep raw percentages in Body 1 & Body 2 while keeping the Overview number-free)"
        : lf
    );
  }

  // 2. Fix LR tips that wrongly suggest swapping "actually" -> "extremely"
  const badActuallyTip = (text: string) =>
    /actually/i.test(text) && /extremely/i.test(text);

  if (badActuallyTip(sanitized.LR.howToGainHalfBand)) {
    sanitized.LR.howToGainHalfBand =
      "Replace L1 false cognates like 'actually' (actualmente → 'currently / in recent years') and conversational intensifiers ('very fast') with C1 academic verb-adverb collocations ('expanded rapidly', 'rose sharply').";
  } else {
    sanitized.LR.howToGainHalfBand = cleanDoubleIntensifiers(sanitized.LR.howToGainHalfBand);
  }

  sanitized.LR.limitingFactors = sanitized.LR.limitingFactors.map((lf) =>
    badActuallyTip(lf)
      ? "Uses L1 false cognate 'actually' (for 'currently') alongside informal intensifiers ('very fast')"
      : cleanDoubleIntensifiers(lf)
  );

  // 3. Remove hallucinated "missing subject" claims in GRA if no sentence is missing a subject
  if (!hasLikelyMissingSubjectInEssay(essayText)) {
    if (/\bmissing subject(s|\s+pronouns?)?\b/i.test(sanitized.GRA.descriptorSummary)) {
      sanitized.GRA.descriptorSummary = sanitized.GRA.descriptorSummary
        .replace(
          /[,;]?\s*(?:and\s+|with\s+|including\s+)?(?:occasional\s+|some\s+|a\s+)?missing subject(?:s|\s+pronouns?)?/gi,
          ""
        )
        .replace(/missing subject(?:s|\s+pronouns?)?\s*(?:and|,)\s*/gi, "")
        .trim();
      if (!sanitized.GRA.descriptorSummary || sanitized.GRA.descriptorSummary.length < 15) {
        sanitized.GRA.descriptorSummary =
          "Uses a mix of simple and complex sentence structures; punctuation across clause boundaries and subordinate clauses can be tightened.";
      }
    }

    sanitized.GRA.limitingFactors = sanitized.GRA.limitingFactors.filter(
      (lf) => !/\bmissing subject(s|\s+pronouns?)?\b/i.test(lf)
    );

    if (/\bmissing subject(s|\s+pronouns?)?\b/i.test(sanitized.GRA.howToGainHalfBand)) {
      sanitized.GRA.howToGainHalfBand =
        "Eliminate comma splices between independent clauses (use a semicolon, period, or subordinating conjunction) and vary participial and relative clauses.";
    }
  }

  return sanitized;
}

export async function evaluateEssayWithAI(
  input: EvaluateEssayInput
): Promise<{
  payload: EvaluationPayload;
  providerUsed: string;
  modelUsed: string;
  costUsd?: number;
}> {
  const cfg = await resolveUserAndAIConfig(input.userId);
  const wordCount = countEssayWords(input.essayText);
  const minWords = getMinimumWords(input.taskType);

  if (!cfg.apiKey) {
    input.onProgress?.("Running local diagnostic analysis...");
    return {
      payload: buildHeuristicDiagnosticPreview(input, cfg, wordCount, minWords),
      providerUsed: "local-diagnostic-preview",
      modelUsed: "heuristic-analyzer-v1",
    };
  }

  const apiKey = cfg.apiKey;
  const l1Profile = cfg.l1CoachingEnabled
    ? L1_LANGUAGE_PROFILES[cfg.nativeLanguage]
    : L1_LANGUAGE_PROFILES.neutral;

  const criterion1Name =
    input.taskType === "task1"
      ? "Task Achievement (TA): clear NUMBER-FREE macro Overview paragraph (mandatory for Band 7+; 2–3 overarching trends/crossovers WITHOUT raw percentages or dates), accurate data selection & exact comparative figures inside Body Paragraphs 1 & 2, no personal opinion/speculation, 150+ words."
      : "Task Response (TR): addresses all parts of the prompt fully, maintains a clear & consistent position throughout, extends main ideas with specific reasoning/examples rather than over-generalising, 250+ words.";

  const targetRewriteBand = Math.max(cfg.targetBand, 8.0).toFixed(1);
  const sharedPedagogyRules = getSharedPedagogyRules(input.taskType);

  const sessionCoachingContextParts: string[] = [];
  if (input.outlineFeedback) {
    sessionCoachingContextParts.push(
      `Prior Outline Check given to student:\n- Overview/Thesis guidance: ${input.outlineFeedback.overviewOrThesisCheck}\n- Grouping guidance: ${input.outlineFeedback.bodyGroupingCheck}`
    );
  }
  if (input.coachingHistory && input.coachingHistory.length > 0) {
    const recentTools = input.coachingHistory
      .slice(-3)
      .map(
        (t) =>
          `- [${t.tool}] ${t.headline}: ${t.suggestions
            .map((s) => `"${s.rewrittenOrPhrase}"`)
            .join(" | ")}`
      )
      .join("\n");
    sessionCoachingContextParts.push(
      `Prior On-Demand Toolkit suggestions shown to student during drafting (DO NOT contradict or penalise phrasing recommended here):\n${recentTools}`
    );
  }
  const sessionCoachingBlock =
    sessionCoachingContextParts.length > 0
      ? `\nSESSION COACHING CONTEXT (maintain 100% consistency with this prior guidance):\n${sessionCoachingContextParts.join(
          "\n\n"
        )}\n`
      : "";

  const sharedEssayContext = `TASK TYPE: Academic IELTS ${
    input.taskType === "task1" ? "Writing Task 1 (Report)" : "Writing Task 2 (Essay)"
  }
DRAFT VERSION: v${input.draftVersion}${
    input.previousBand ? ` (Previous draft scored Band ${input.previousBand})` : ""
  }
WORD COUNT: ${wordCount} words (Minimum required: ${minWords})

PROMPT TITLE: ${input.promptTitle}
PROMPT INSTRUCTIONS:
${input.promptText}

${
  input.visualContext
    ? `UNDERLYING VISUAL DATA / DIAGRAM GROUND TRUTH (verify candidate's body-paragraph figures & macro overview trends against this):\n${input.visualContext}\n`
    : ""
}${sessionCoachingBlock}CANDIDATE ESSAY:
"""
${input.essayText}
"""`;

  const rubricSystemPrompt = `You are a Principal Academic IELTS Writing Examiner calibrated for Band ${cfg.targetBand.toFixed(
    1
  )}+.
Grade strictly according to official IELTS Academic Writing Band Descriptors (0.0 to 9.0 in 0.5 increments; do NOT inflate scores—Band 6.5 plateaus are common when cohesion is mechanical, Task 1 lacks a synthesised overview, or Task 2 ideas remain over-generalised).

Candidate Calibration:
- Current Baseline Band: ${cfg.currentBand.toFixed(1)} | Target Band: ${cfg.targetBand.toFixed(1)}
- Linguistic Lens (${l1Profile.label}): ${l1Profile.coachingFocus}

Official 4 Criteria:
1. TA_TR — ${criterion1Name}
2. CC — Coherence and Cohesion: logical paragraphing, clear central topic per body paragraph, natural cohesive devices WITHOUT mechanical sentence-initial linkers ("Firstly, Secondly, Moreover, In a nutshell"). Note: comma splices belong under GRA, not CC.
3. LR — Lexical Resource: natural C1/C2 academic collocations, precise statistical/academic register, effective hedging, accurate word formation & spelling.
4. GRA — Grammatical Range and Accuracy: flexible mix of complex subordinate, relative, participial, and conditional structures with high punctuation & clause-boundary accuracy (comma splices are scored here under GRA).

${sharedPedagogyRules}

Keep each string concise and high-signal so JSON generation is rapid.`;

  const rubricUserPrompt = `${sharedEssayContext}

Return ONLY a valid JSON object matching this exact structure:
{
  "taTrBand": 6.5,
  "ccBand": 6.5,
  "lrBand": 7.0,
  "graBand": 6.5,
  "examinerSummary": "2-3 sentence candid synthesis of what held this draft back and its strongest asset.",
  "criteria": {
    "TA_TR": {
      "band": 6.5,
      "descriptorSummary": "1-2 sentences on why this band applies under official descriptors.",
      "strengths": ["1-2 concise strengths"],
      "limitingFactors": ["1-2 concise limiting factors"],
      "howToGainHalfBand": "1 concrete revision step to reach +0.5/+1.0 (for Task 1: keep Overview number-free; put exact figures in Body 1 & 2)."
    },
    "CC": { "band": 6.5, "descriptorSummary": "...", "strengths": ["..."], "limitingFactors": ["..."], "howToGainHalfBand": "..." },
    "LR": { "band": 7.0, "descriptorSummary": "...", "strengths": ["..."], "limitingFactors": ["..."], "howToGainHalfBand": "..." },
    "GRA": { "band": 6.5, "descriptorSummary": "...", "strengths": ["..."], "limitingFactors": ["..."], "howToGainHalfBand": "..." }
  },
  "topPriorities": ["Top 1 highest-leverage fix for v${input.draftVersion + 1}", "Top 2...", "Top 3..."]
}`;

  const coachSystemPrompt = `You are a Band ${targetRewriteBand} Academic IELTS Prose Coach & Marginalia Editor.
Linguistic Lens (${l1Profile.label}): ${l1Profile.coachingFocus}

${sharedPedagogyRules}

CRITICAL RULES FOR JSON OUTPUT:
- Each "inlineAnnotations" item MUST set "originalSpan" to an EXACT verbatim substring copied character-for-character from the candidate's essay (15 to 140 characters) so it highlights cleanly in the UI. Include 3 to 7 genuinely high-impact annotations spanning TA_TR, CC, LR, GRA${
    cfg.l1CoachingEnabled && cfg.nativeLanguage !== "neutral" ? ", and L1" : ""
  } (do NOT pad with lateral synonym swaps like "whereas" -> "while").
- Ensure every "replacement" is a complete, grammatically sound drop-in replacement for "originalSpan" that reads naturally in the surrounding sentence (never leave false cognates like "actually" in place or create double intensifiers).
- "targetBandRewrite" MUST rewrite the candidate's OWN arguments/data groupings elevated to natural Band ${targetRewriteBand} academic prose (multi-paragraph essay separated by "\\n\\n"; for Task 1, ensure the Overview paragraph has NO raw numbers and Body 1 & 2 contain the exact figures).
- "vaultItems" MUST extract 3 to 4 reusable items (recurring error fixes and high-utility academic collocations) for spaced review.`;

  const coachUserPrompt = `${sharedEssayContext}

Return ONLY a valid JSON object matching this exact structure:
{
  "inlineAnnotations": [
    {
      "id": "ann-1",
      "category": "GRA",
      "severity": "error",
      "originalSpan": "exact substring from candidate essay",
      "replacement": "natural Band 8+ revision of that exact span",
      "explanation": "Concise 1-sentence examiner note explaining why."
    }
  ],
  "targetBandRewrite": "Full multi-paragraph rewrite of the candidate's own essay at Band ${targetRewriteBand}...",
  "rewriteKeyChanges": ["3-4 concise bullet takeaways comparing the rewrite to the candidate's draft"],
  "vaultItems": [
    {
      "itemType": "error",
      "criterion": "L1",
      "originalText": "...",
      "upgradedText": "...",
      "explanation": "...",
      "exampleSentence": "..."
    }
  ]
}`;

  input.onProgress?.("Scoring 4 IELTS criteria & generating Band 8+ rewrite...");

  let modelUsed = cfg.graderModel;

  const scorePromise = (async (): Promise<{
    data: RubricScoreOutput;
    cost?: number;
    model: string;
  }> => {
    try {
      const resRubric = await callLLMWithMeta({
        userId: cfg.userId,
        keySource: cfg.keySource,
        provider: cfg.provider,
        apiKey,
        model: cfg.graderModel,
        fallbackModels: PROVIDER_DEFAULTS[cfg.provider]?.graderFallbacks,
        feature: "rubric_score",
        systemPrompt: rubricSystemPrompt,
        userPrompt: rubricUserPrompt,
        jsonMode: true,
        thinkingMode: "disabled",
        maxTokens: 1200,
        timeoutMs: 35_000,
      });
      const parsedRubric = extractJsonObject<RubricScoreOutput>(resRubric.content);
      input.onProgress?.("4-criterion band scores ready — finalising inline upgrades...");
      const actualModel = resRubric.modelUsed || cfg.graderModel;
      modelUsed = actualModel;
      return { data: parsedRubric, cost: resRubric.costUsd, model: actualModel };
    } catch (primaryErr) {
      if (cfg.fastModel && cfg.fastModel !== cfg.graderModel) {
        input.onProgress?.("Switching to fast examiner model to complete band scoring...");
        const resFallback = await callLLMWithMeta({
          userId: cfg.userId,
          keySource: cfg.keySource,
          provider: cfg.provider,
          apiKey,
          model: cfg.fastModel,
          fallbackModels: PROVIDER_DEFAULTS[cfg.provider]?.fastFallbacks,
          feature: "rubric_score_fallback",
          systemPrompt: rubricSystemPrompt,
          userPrompt: rubricUserPrompt,
          jsonMode: true,
          thinkingMode: "disabled",
          maxTokens: 1200,
          timeoutMs: 25_000,
        });
        const actualModel = resFallback.modelUsed || cfg.fastModel;
        modelUsed = actualModel;
        input.onProgress?.("4-criterion band scores ready — finalising inline upgrades...");
        return {
          data: extractJsonObject<RubricScoreOutput>(resFallback.content),
          cost: resFallback.costUsd,
          model: actualModel,
        };
      }
      throw primaryErr;
    }
  })();

  const annotationsPromise = (async (): Promise<{
    data: AnnotationRewriteOutput;
    cost?: number;
  }> => {
    try {
      const resCoach = await callLLMWithMeta({
        userId: cfg.userId,
        keySource: cfg.keySource,
        provider: cfg.provider,
        apiKey,
        model: cfg.fastModel,
        fallbackModels: PROVIDER_DEFAULTS[cfg.provider]?.fastFallbacks,
        feature: "annotations_rewrite",
        systemPrompt: coachSystemPrompt,
        userPrompt: coachUserPrompt,
        jsonMode: true,
        thinkingMode: "disabled",
        maxTokens: 2100,
        timeoutMs: 40_000,
      });
      const parsedCoach = extractJsonObject<AnnotationRewriteOutput>(resCoach.content);
      input.onProgress?.("Inline annotations & Band 8+ rewrite ready...");
      return { data: parsedCoach, cost: resCoach.costUsd };
    } catch {
      const localPreview = buildHeuristicDiagnosticPreview(input, cfg, wordCount, minWords);
      return {
        data: {
          inlineAnnotations: localPreview.inlineAnnotations,
          targetBandRewrite:
            "Band 8.0+ full rewrite timed out on this run, while your official 4-criterion band scores succeeded above. You can redraft or re-run anytime.",
          rewriteKeyChanges: [],
          vaultItems: localPreview.vaultItems,
        },
        cost: undefined,
      };
    }
  })();

  const [rubricResult, coachResult] = await Promise.all([scorePromise, annotationsPromise]);
  const rubricData = rubricResult.data;
  const coachData = coachResult.data;
  const totalCost =
    rubricResult.cost !== undefined || coachResult.cost !== undefined
      ? Number(((rubricResult.cost || 0) + (coachResult.cost || 0)).toFixed(6))
      : undefined;

  const taTrBand = roundIeltsBand(
    Number(rubricData.taTrBand ?? rubricData.criteria?.TA_TR?.band ?? 6.5)
  );
  const ccBand = roundIeltsBand(
    Number(rubricData.ccBand ?? rubricData.criteria?.CC?.band ?? 6.5)
  );
  const lrBand = roundIeltsBand(
    Number(rubricData.lrBand ?? rubricData.criteria?.LR?.band ?? 6.5)
  );
  const graBand = roundIeltsBand(
    Number(rubricData.graBand ?? rubricData.criteria?.GRA?.band ?? 6.5)
  );
  const overallBand = roundIeltsBand((taTrBand + ccBand + lrBand + graBand) / 4);

  const validCategories = new Set<AnnotationCategory>(["TA_TR", "CC", "LR", "GRA", "L1"]);

  const normalizedAnnotations = normalizeAndFilterAnnotations(
    coachData.inlineAnnotations || [],
    validCategories
  );

  const rawCriteria: EvaluationPayload["criteria"] = {
    TA_TR: {
      band: taTrBand,
      descriptorSummary:
        rubricData.criteria?.TA_TR?.descriptorSummary || "Assessed for task fulfilment.",
      strengths: rubricData.criteria?.TA_TR?.strengths || [],
      limitingFactors: rubricData.criteria?.TA_TR?.limitingFactors || [],
      howToGainHalfBand:
        rubricData.criteria?.TA_TR?.howToGainHalfBand ||
        (input.taskType === "task1"
          ? "Keep your Overview number-free and support both body paragraphs with exact comparative figures."
          : "Extend core points with sharper precision and concrete examples."),
    },
    CC: {
      band: ccBand,
      descriptorSummary:
        rubricData.criteria?.CC?.descriptorSummary || "Assessed for logical flow and cohesion.",
      strengths: rubricData.criteria?.CC?.strengths || [],
      limitingFactors: rubricData.criteria?.CC?.limitingFactors || [],
      howToGainHalfBand:
        rubricData.criteria?.CC?.howToGainHalfBand ||
        "Replace mechanical linkers with demonstrative referencing.",
    },
    LR: {
      band: lrBand,
      descriptorSummary:
        rubricData.criteria?.LR?.descriptorSummary || "Assessed for academic lexical precision.",
      strengths: rubricData.criteria?.LR?.strengths || [],
      limitingFactors: rubricData.criteria?.LR?.limitingFactors || [],
      howToGainHalfBand:
        rubricData.criteria?.LR?.howToGainHalfBand ||
        "Use precise domain collocations and nuanced hedging.",
    },
    GRA: {
      band: graBand,
      descriptorSummary:
        rubricData.criteria?.GRA?.descriptorSummary ||
        "Assessed for syntactic range and accuracy.",
      strengths: rubricData.criteria?.GRA?.strengths || [],
      limitingFactors: rubricData.criteria?.GRA?.limitingFactors || [],
      howToGainHalfBand:
        rubricData.criteria?.GRA?.howToGainHalfBand ||
        "Vary subordinate clauses and eliminate punctuation splices.",
    },
  };

  const sanitizedCriteria = sanitizeCriteriaFeedback(
    rawCriteria,
    input.taskType,
    input.essayText
  );

  const sanitizedTopPriorities = (
    rubricData.topPriorities || [
      "Strengthen macro overview / thesis precision",
      "Replace mechanical sequence connectors with cohesive referencing",
      "Upgrade high-frequency verbs into formal academic collocations",
    ]
  ).map((p) => {
    if (
      input.taskType === "task1" &&
      /\boverview\b/i.test(p) &&
      /\b(\d+%|figures|statistics|numbers|percentages|data)\b/i.test(p) &&
      !/\b(without|no|avoid|free of|never)\b/i.test(p)
    ) {
      return "Keep the Overview strictly number-free and place exact comparative figures in Body Paragraphs 1 & 2";
    }
    if (/actually/i.test(p) && /extremely/i.test(p)) {
      return "Replace L1 false cognates ('actually' → 'currently / in recent years') and informal intensifiers ('very fast' → 'rapidly')";
    }
    if (!hasLikelyMissingSubjectInEssay(input.essayText) && /\bmissing subject/i.test(p)) {
      return "Eliminate comma splices between independent clauses and vary subordinate clause structures";
    }
    return cleanDoubleIntensifiers(p);
  });

  const payload: EvaluationPayload = {
    overallBand,
    taTrBand,
    ccBand,
    lrBand,
    graBand,
    examinerSummary:
      rubricData.examinerSummary ||
      `Scored Band ${overallBand.toFixed(1)} overall across the four official Academic IELTS criteria.`,
    criteria: sanitizedCriteria,
    inlineAnnotations: normalizedAnnotations,
    targetBandRewrite: cleanDoubleIntensifiers(
      coachData.targetBandRewrite ||
        "Target-band rewrite unavailable for this response."
    ),
    rewriteKeyChanges: (coachData.rewriteKeyChanges || []).map(cleanDoubleIntensifiers),
    topPriorities: sanitizedTopPriorities,
    vaultItems: (coachData.vaultItems || [])
      .filter(
        (v) =>
          v &&
          !isTrivialLateralSwap(
            v.originalText || "",
            v.upgradedText || "",
            v.explanation || ""
          )
      )
      .map((v) => {
        const explanation = v.explanation || "";
        let criterion: AnnotationCategory = validCategories.has(v.criterion)
          ? v.criterion
          : "LR";
        if (/\b(comma splice|run-on|independent clauses?|semicolon)\b/i.test(explanation)) {
          criterion = "GRA";
        } else if (
          /\b(false cognate|false friend|actualmente|calque)\b/i.test(explanation) &&
          validCategories.has("L1")
        ) {
          criterion = "L1";
        }
        return {
          itemType: v.itemType === "collocation" ? "collocation" : "error",
          criterion,
          originalText: v.originalText || "",
          upgradedText: cleanDoubleIntensifiers(v.upgradedText || ""),
          explanation,
          exampleSentence: cleanDoubleIntensifiers(v.exampleSentence || ""),
        };
      }),
  };

  return {
    payload,
    providerUsed: cfg.provider,
    modelUsed,
    costUsd: totalCost,
  };
}

/* ============================================================================
 * 3. FAST <2s COACH: 5-MIN OUTLINE CHECK & ON-DEMAND ACADEMIC TOOLKIT
 * ============================================================================ */

export async function checkOutlineWithAI(params: {
  userId: string;
  taskType: TaskType;
  promptText: string;
  visualContext?: string | null;
  outline: OutlineInput;
}): Promise<OutlineFeedback> {
  const cfg = await resolveUserAndAIConfig(params.userId);
  const { taskType, promptText, visualContext, outline } = params;

  if (!cfg.apiKey) {
    const hasOverviewOrThesis = outline.thesisOrOverview.trim().split(/\s+/).length >= 8;
    const hasBodies =
      outline.body1Plan.trim().length > 12 && outline.body2Plan.trim().length > 12;
    return {
      readiness: hasOverviewOrThesis && hasBodies ? "strong" : "needs_refinement",
      estimatedTaskBandCeiling: hasOverviewOrThesis && hasBodies ? 7.5 : 6.0,
      verdictSummary:
        hasOverviewOrThesis && hasBodies
          ? "Solid structural skeleton (Local Preview — add your DeepSeek API key in Settings for live prompt-specific validation)."
          : "Your plan is still too brief—spell out your exact macro overview/thesis and concrete evidence for both body paragraphs before starting the timer.",
      overviewOrThesisCheck:
        taskType === "task1"
          ? "Ensure your Overview identifies 2–3 overarching trends or extremes WITHOUT quoting raw statistics (save exact numbers for Body 1 & Body 2)."
          : "Ensure your thesis states a nuanced, unambiguous position that directly answers every part of the prompt question.",
      bodyGroupingCheck:
        taskType === "task1"
          ? "Group items by shared trajectories or contrast (e.g., rising vs. declining categories) rather than describing one line/row per paragraph mechanically."
          : "Give each body paragraph ONE central claim backed by a causal mechanism and a specific real-world example.",
      actionableTweaks: [
        hasOverviewOrThesis
          ? "Avoid repeating exact prompt wording in your opening paraphrase—use structural conversion (active ↔ passive or clause nominalisation)."
          : "Expand your Thesis/Overview box to at least 1–2 complete thoughts.",
        "Pre-select 3–4 domain collocations now so your cognitive load during drafting goes into syntax accuracy.",
      ],
      suggestedAcademicCollocations:
        taskType === "task1"
          ? [
              {
                phrase: "witnessed a pronounced upward trajectory",
                usageNote: "Stronger than 'went up a lot' for sustained multi-year growth.",
              },
              {
                phrase: "declined precipitously / fell sharply",
                usageNote: "Objective statistical phrasing for steep drops (prefer over journalistic metaphors like 'collapsed').",
              },
              {
                phrase: "converged before overtaking",
                usageNote: "Precise phrasing for intersecting line-graph series.",
              },
            ]
          : [
              {
                phrase: "yields tangible long-term dividends",
                usageNote: "High-register alternative to 'brings good results in the future'.",
              },
              {
                phrase: "disproportionately burdens lower-income households",
                usageNote: "Nuanced economic equity phrasing for policy debates.",
              },
              {
                phrase: "mitigate unintended externalities",
                usageNote: "Precise C1/C2 academic collocation for Problem/Solution & Policy essays.",
              },
            ],
    };
  }

  const raw = await callLLM({
    userId: cfg.userId,
    keySource: cfg.keySource,
    provider: cfg.provider,
    apiKey: cfg.apiKey,
    model: cfg.fastModel,
    fallbackModels: PROVIDER_DEFAULTS[cfg.provider]?.fastFallbacks,
    feature: "outline_check",
    jsonMode: true,
    thinkingMode: "disabled",
    timeoutMs: 35_000,
    systemPrompt: `You are a fast, surgical Academic IELTS Band ${cfg.targetBand.toFixed(
      1
    )}+ Writing Coach. Validate the student's 5-minute pre-writing outline BEFORE they write the essay. Do NOT write the essay for them—spot Task Achievement/Response traps, weak grouping, or missing prompt parts, and suggest 4 high-register academic collocations.

${getSharedPedagogyRules(taskType)}`,
    userPrompt: `TASK: Academic IELTS ${taskType.toUpperCase()}
PROMPT:
${promptText}
${visualContext ? `\nVISUAL DATA SUMMARY:\n${visualContext}\n` : ""}
STUDENT OUTLINE:
- ${taskType === "task1" ? "Macro Overview" : "Thesis / Position"}: ${outline.thesisOrOverview}
- Body Paragraph 1 Plan: ${outline.body1Plan}
- Body Paragraph 2 Plan: ${outline.body2Plan}
${outline.targetKeyTerms ? `- Planned Vocabulary: ${outline.targetKeyTerms}` : ""}

Return JSON:
{
  "readiness": "strong" | "needs_refinement" | "off_track",
  "estimatedTaskBandCeiling": 7.5,
  "verdictSummary": "1-2 crisp sentences on whether they are ready to draft.",
  "overviewOrThesisCheck": "Specific critique of their Overview (Task 1: strictly NO raw numbers/percentages in Overview; captures main trend + extreme; save exact figures for Body 1 & 2) or Thesis (Task 2: clear position?).",
  "bodyGroupingCheck": "Critique of how logically Body 1 & Body 2 divide the data (with exact figures reserved for Body 1 & 2) or arguments.",
  "actionableTweaks": ["2-3 quick bullet adjustments before typing"],
  "suggestedAcademicCollocations": [
    { "phrase": "...", "usageNote": "..." }
  ]
}`,
  });

  return extractJsonObject<OutlineFeedback>(raw);
}

export async function runToolkitWithAI(
  userId: string,
  req: ToolkitRequest
): Promise<ToolkitResponse> {
  const cfg = await resolveUserAndAIConfig(userId);
  const l1Profile = cfg.l1CoachingEnabled
    ? L1_LANGUAGE_PROFILES[cfg.nativeLanguage]
    : L1_LANGUAGE_PROFILES.neutral;

  if (!cfg.apiKey) {
    return {
      tool: req.tool,
      headline:
        req.tool === "collocations"
          ? "Academic Register & Hedging Upgrades (Preview)"
          : req.tool === "l1_check"
          ? `${l1Profile.label} Transfer Scan (Preview)`
          : "Paragraph Coherence & Topic-Sentence Check (Preview)",
      analysis:
        "Add your DeepSeek API key in Settings to unlock live context-aware rewrites of your exact selection. Meanwhile, verify that your sentence avoids sweeping absolutes ('always', 'everyone') and links back to the prompt's core claim without mechanical connectors.",
      suggestions: [
        {
          label: "Nominalised Academic Opening",
          rewrittenOrPhrase:
            "This structural shift underscores the need for targeted regulatory intervention...",
          rationale: "Turns a conversational clause into compact Band 8.0 academic prose.",
        },
        {
          label: "Nuanced C1 Hedging",
          rewrittenOrPhrase:
            "tends to exacerbate... / is widely associated with... / risks undermining...",
          rationale: "Replaces over-confident Band 6 claims ('will cause / destroys').",
        },
      ],
    };
  }

  const toolInstructions =
    req.tool === "collocations"
      ? `Upgrade the selected phrase/sentence into 3 distinct, natural Band ${cfg.targetBand.toFixed(
          1
        )}+ academic phrasings (one with C1/C2 collocations, one with nuanced academic hedging, one with compact nominalisation). Do NOT write new paragraphs—only refine the selected idea. Never use journalistic metaphors like "collapsed" or "skyrocketed" (use objective statistical verbs like "declined precipitously", "fell sharply", "expanded rapidly"), and never suggest lateral swaps like "whereas" ↔ "while".`
      : req.tool === "l1_check"
      ? `Inspect the selected text through the lens of ${l1Profile.label} (${l1Profile.coachingFocus}). Flag any false cognates (such as "actually" used for "actualmente" = "currently / in recent years" — replace the whole phrase naturally, never keep "actually" or swap it with "extremely"), comma splices (note that comma splices are GRA punctuation errors), article/preposition calques, or unnatural clause length, and provide 2-3 clean native academic alternatives.`
      : `Evaluate this single paragraph for IELTS Coherence & Cohesion, Task ${
          req.taskType === "task1" ? "Achievement" : "Response"
        }, and Academic Register: Does it have a clear topic sentence or overview signpost? (If it is a Task 1 Overview paragraph, ensure it contains NO raw numbers/percentages; if it is a Body paragraph, ensure it groups and compares specific figures.) Are ideas extended logically without mechanical linkers or journalistic verbs like "collapsed"? Give concise diagnosis + 2 surgical improvements that align strictly with our examiner grading standards (do not rewrite the entire essay).`;

  const raw = await callLLM({
    userId: cfg.userId,
    keySource: cfg.keySource,
    provider: cfg.provider,
    apiKey: cfg.apiKey,
    model: cfg.fastModel,
    fallbackModels: PROVIDER_DEFAULTS[cfg.provider]?.fastFallbacks,
    feature: "toolkit",
    jsonMode: true,
    thinkingMode: "disabled",
    timeoutMs: 35_000,
    systemPrompt: `You are an on-demand Academic IELTS writing coach. Be crisp, encouraging, and surgical. Ensure 100% alignment with the final IELTS Examiner rubric so you never suggest phrasing that the grader would penalise.

${getSharedPedagogyRules(req.taskType)}`,
    userPrompt: `TASK: ${req.taskType.toUpperCase()}
PROMPT: ${req.promptText}
SELECTED TEXT TO COACH:
"""
${req.selectedText}
"""

INSTRUCTION: ${toolInstructions}

Return JSON:
{
  "tool": "${req.tool}",
  "headline": "Short diagnostic title",
  "analysis": "2-3 sentence explanation",
  "suggestions": [
    { "label": "Option 1 label", "rewrittenOrPhrase": "...", "rationale": "Why this scores higher" }
  ]
}`,
  });

  return extractJsonObject<ToolkitResponse>(raw);
}

/* ============================================================================
 * 4. ON-DEMAND AI PROMPT & STRUCTURED VISUAL GENERATOR
 * ============================================================================ */

export async function generatePromptWithAI(params: {
  userId: string;
  taskType: TaskType;
  category: PromptCategory;
  topic?: string;
}): Promise<{
  title: string;
  topic: string;
  promptText: string;
  chartSpec?: ChartSpec;
}> {
  const cfg = await resolveUserAndAIConfig(params.userId);
  const chosenTopic = params.topic?.trim() || "Public Policy, Technology & Sustainability";

  if (!cfg.apiKey) {
    if (params.taskType === "task2") {
      return {
        title: `Public Investment in ${chosenTopic}`,
        topic: chosenTopic,
        promptText: `In many countries, governments face growing pressure to prioritise immediate economic growth over long-term investments in ${chosenTopic.toLowerCase()}.\n\nTo what extent do you agree or disagree that long-term sustainability should take precedence over short-term economic gains?\n\nGive reasons for your answer and include any relevant examples from your own knowledge or experience. Write at least 250 words.`,
      };
    }
    return {
      title: `Regional Adoption Trends in ${chosenTopic} (2005–2025)`,
      topic: chosenTopic,
      promptText: `The line graph below compares adoption rates across three regions in ${chosenTopic.toLowerCase()} between 2005 and 2025.\n\nSummarise the information by selecting and reporting the main features, and make comparisons where relevant.\n\nWrite at least 150 words.`,
      chartSpec: {
        kind: "line",
        title: `Adoption Index Across Three Regions (2005–2025)`,
        subtitle: "Percentage of surveyed institutions (%)",
        xAxisKey: "year",
        yAxisUnit: "%",
        series: [
          { key: "nordic", label: "Northern Europe", color: "#16656e" },
          { key: "eastAsia", label: "East Asia", color: "#c55e1b" },
          { key: "latam", label: "Latin America", color: "#2b7a52" },
        ],
        data: [
          { year: "2005", nordic: 28, eastAsia: 19, latam: 11 },
          { year: "2010", nordic: 39, eastAsia: 34, latam: 16 },
          { year: "2015", nordic: 51, eastAsia: 56, latam: 24 },
          { year: "2020", nordic: 64, eastAsia: 72, latam: 37 },
          { year: "2025", nordic: 74, eastAsia: 83, latam: 49 },
        ],
        sourceNote: "Generated Practice Dataset (Connect DeepSeek API key for infinite custom charts)",
      },
    };
  }

  const chartSchemaHint =
    params.taskType === "task1"
      ? getChartSchemaPromptForCategory(params.category)
      : "Omit chartSpec (set to null) because Task 2 is a discursive essay.";

  const raw = await callLLM({
    userId: cfg.userId,
    keySource: cfg.keySource,
    provider: cfg.provider,
    apiKey: cfg.apiKey,
    model: cfg.fastModel,
    fallbackModels: PROVIDER_DEFAULTS[cfg.provider]?.fastFallbacks,
    feature: "prompt_generation",
    jsonMode: true,
    thinkingMode: "disabled",
    maxTokens: 1600,
    timeoutMs: 38_000,
    systemPrompt:
      "You are an official Cambridge Academic IELTS test-paper designer. Create realistic, nuanced Academic IELTS Writing tasks with rich contrasts, crossovers, or thought-provoking academic tensions.",
    userPrompt: `Create a brand-new Academic IELTS ${
      params.taskType === "task1" ? "Task 1 visual report" : "Task 2 discursive essay"
    }.
Archetype / Category: "${params.category}"
Subject Domain: "${chosenTopic}"

${chartSchemaHint}

Return ONLY valid JSON:
{
  "title": "Concise descriptive title",
  "topic": "${chosenTopic}",
  "promptText": "Official-style IELTS prompt instructions ending with 'Write at least ${
    params.taskType === "task1" ? "150" : "250"
  } words.'",
  "chartSpec": ${params.taskType === "task1" ? "{ ... }" : "null"}
}`,
  });

  const parsed = extractJsonObject<{
    title: string;
    topic: string;
    promptText: string;
    chartSpec?: ChartSpec | null;
  }>(raw);

  return {
    title: parsed.title || `Academic ${params.taskType.toUpperCase()} Practice`,
    topic: parsed.topic || chosenTopic,
    promptText: parsed.promptText,
    chartSpec: normalizeChartSpec(parsed.chartSpec),
  };
}

function getChartSchemaPromptForCategory(category: PromptCategory): string {
  switch (category) {
    case "line":
    case "bar":
      return `Include "chartSpec" with exact shape:
{ "kind": "${category}", "title": "...", "subtitle": "...", "xAxisKey": "xLabel", "yAxisUnit": "%", "series": [{ "key": "s1", "label": "Series 1", "color": "#16656e" }, { "key": "s2", "label": "Series 2", "color": "#c55e1b" }, { "key": "s3", "label": "Series 3", "color": "#2b7a52" }], "data": [{ "xLabel": "2005", "s1": 20, "s2": 45, "s3": 12 }, ... (5-7 points with interesting crossovers/contrasts)], "sourceNote": "Source: ..." }`;
    case "mixed":
      return `Include "chartSpec" with exact shape:
{ "kind": "mixed", "title": "...", "subtitle": "...", "xAxisKey": "year", "yAxisUnit": "units", "yAxisRightUnit": "%", "series": [{ "key": "vol", "label": "... (Bar)", "type": "bar", "yAxisId": "left", "color": "#16656e" }, { "key": "share", "label": "... (%)", "type": "line", "yAxisId": "right", "color": "#c55e1b" }], "data": [{ "year": "2005", "vol": 120, "share": 18 }, ... (6 points)], "sourceNote": "Source: ..." }`;
    case "pie":
      return `Include "chartSpec" with two comparative periods:
{ "kind": "pie", "title": "...", "subtitle": "...", "unit": "%", "charts": [{ "label": "2000", "slices": [{ "name": "...", "value": 40 }, ... (slices summing to 100)] }, { "label": "2025", "slices": [{ "name": "...", "value": 22 }, ... (summing to 100)] }], "sourceNote": "Source: ..." }`;
    case "table":
      return `Include "chartSpec" with 5 rows and 5 columns:
{ "kind": "table", "title": "...", "unitNote": "...", "columns": ["Country/Sector", "Metric 1", "Metric 2", "Metric 3", "Metric 4"], "rows": [["...", 120, "34%", ...], ...], "sourceNote": "Source: ..." }`;
    case "process":
      return `Include "chartSpec" with 6-8 stages:
{ "kind": "process", "title": "...", "subtitle": "...", "isCyclic": false, "steps": [{ "stepNumber": 1, "stageName": "...", "detail": "...", "badge": "..." }, ...] }`;
    case "map":
      return `Include "chartSpec" comparing two time periods across 9 compass zones ("NW","N","NE","W","C","E","SW","S","SE") and categories ("residential"|"nature"|"commercial"|"industrial"|"transport"|"leisure"|"civic"):
{ "kind": "map", "title": "...", "subtitle": "...", "periodA": "1995", "periodB": "Present Day", "waterway": "coastal_south" | "river_vertical" | "none", "mainRoad": "horizontal" | "vertical", "featuresA": [{ "zone": "NW", "label": "...", "category": "nature" }, ...all 9 zones], "featuresB": [{ "zone": "NW", "label": "...", "category": "leisure", "changed": true }, ...all 9 zones] }`;
    default:
      return "";
  }
}

/* ============================================================================
 * 5. 5-MINUTE TARGETED MICRO-DRILLS (Generation & Instant Scoring)
 * ============================================================================ */

const BUILTIN_DRILL_TEMPLATES: Record<DrillType, MicroDrillChallenge> = {
  overview_sprint: {
    drillType: "overview_sprint",
    targetCriterion: "TA_TR",
    title: "4-Minute Task 1 Overview Sprint",
    instructions:
      "Inspect the chart below and write ONLY a 2-sentence Academic Task 1 Overview paragraph (35–55 words). Capture the main overall trend and the most striking contrast/dominance—WITHOUT quoting specific percentages or years!",
    timeLimitSeconds: 240,
    sourceContext: "Global Cargo Shipping Propulsion Mix (2010–2030 Projections)",
    chartSpec: {
      kind: "line",
      title: "Share of New Commercial Vessels by Fuel Type (2010–2030)",
      xAxisKey: "year",
      yAxisUnit: "%",
      series: [
        { key: "diesel", label: "Conventional Heavy Fuel Oil", color: "#475569" },
        { key: "lng", label: "Liquefied Natural Gas (LNG)", color: "#16656e" },
        { key: "methanol", label: "Green Methanol & Ammonia", color: "#228b5b" },
      ],
      data: [
        { year: "2010", diesel: 96, lng: 3, methanol: 0 },
        { year: "2015", diesel: 89, lng: 9, methanol: 1 },
        { year: "2020", diesel: 74, lng: 22, methanol: 3 },
        { year: "2025", diesel: 51, lng: 33, methanol: 14 },
        { year: "2030", diesel: 28, lng: 37, methanol: 35 },
      ],
    },
    hints: [
      "Start with a clear signpost such as 'Overall,' or 'In general,'",
      "Contrast the steep decline in conventional oil against the surge in cleaner alternatives, noting that LNG and green fuels are projected to overtake heavy oil by the end of the period.",
    ],
    referenceModelAnswer:
      "Overall, reliance on conventional heavy fuel oil for new commercial vessels declines precipitously across the timeframe, whereas cleaner alternative fuels experience substantial growth. Notably, although heavy oil initially dominated orders, both liquefied natural gas and green methanol-ammonia are projected to surpass it by the end of the period.",
  },
  cohesion: {
    drillType: "cohesion",
    targetCriterion: "CC",
    title: "Cohesion De-Mechanizer",
    instructions:
      "Rewrite this Band 6.0 paragraph to achieve Band 8.0+ Coherence & Cohesion. Eliminate ALL four mechanical sentence-initial linkers ('Firstly', 'Moreover', 'On the other hand', 'In conclusion') using demonstrative referencing ('This influx...', 'Such constraints...'), subordination, and thematic flow.",
    timeLimitSeconds: 300,
    sourceContext: "Task 2 Body Paragraph on Remote Work & Regional Towns",
    flawedText:
      "Firstly, remote work allows salaried professionals to relocate away from overcrowded capitals into smaller towns. Moreover, these new residents spend their salaries in local cafés, clinics, and schools, which revives regional economies. On the other hand, sudden population inflows can push up local rents rapidly. Because of this, long-standing town residents may struggle to afford housing.",
    hints: [
      "Link Sentence 1 → 2 via nominalisation ('This geographic dispersal injects disposable income...')",
      "Turn 'On the other hand... Because of this...' into a tightly integrated concessive or participial complex sentence.",
    ],
    referenceModelAnswer:
      "Widespread telecommuting enables salaried professionals to relocate from congested capitals to smaller regional municipalities, injecting vital consumer spending into local cafés, clinics, and schools. Yet this demographic influx carries an acute housing trade-off: rapid demand growth can inflate rental prices, potentially pricing long-standing residents out of their own communities.",
  },
  l1_surgeon: {
    drillType: "l1_surgeon",
    targetCriterion: "L1",
    title: "Spanish → English Clause & False-Cognate Surgeon",
    instructions:
      "Locate and fix 4 classic L1 interference slips in the passage below (false cognates, comma splices with 'however', definite article generalisation, and preposition calque) while elevating the prose to crisp Band 8.0 academic register.",
    timeLimitSeconds: 300,
    sourceContext: "Task 2 Paragraph on University Curriculum Reform",
    flawedText:
      "Actually, the society demands graduates who possess practical digital literacy, however, many public universities still assist to outdated lecture models that depend of rote memorisation rather than collaborative problem-solving.",
    hints: [
      "'Actually' ≠ 'actualmente' (use 'Currently / At present')",
      "'the society' → abstract plural/uncountable generalisation drops 'the'",
      "', however,' between two full clauses is a comma splice",
      "'assist to' (asistir a) & 'depend of' (depender de) are Romance calques",
    ],
    referenceModelAnswer:
      "Currently, modern economies demand graduates equipped with practical digital literacy; however, many public universities still adhere to outdated lecture formats that rely on rote memorisation rather than collaborative problem-solving.",
  },
  lexical_hedging: {
    drillType: "lexical_hedging",
    targetCriterion: "LR",
    title: "Band 6.5 → 8.0 Lexical & Hedging Upgrade",
    instructions:
      "Transform these sweeping, informal Band 6.0 claims into nuanced, academically hedged Band 8.0+ arguments without changing the underlying stance.",
    timeLimitSeconds: 300,
    sourceContext: "Task 2 Claims on Congestion Taxes & Public Health",
    flawedText:
      "Putting big taxes on sugary drinks will definitely stop obesity in every country. People will stop buying sodas right away, and governments will get lots of money to fix all hospital problems.",
    hints: [
      "Replace absolutes ('will definitely stop', 'every', 'all') with calibrated modal/lexical hedging ('can significantly curb', 'tends to discourage', 'earmarked revenue')",
      "Upgrade informal lexis ('putting big taxes', 'lots of money', 'fix hospital problems') to fiscal/public-health collocations.",
    ],
    referenceModelAnswer:
      "Levying tiered excise duties on sugar-sweetened beverages can measurably curb caloric intake, particularly among price-sensitive consumers. While fiscal disincentives alone rarely eradicate obesity, the resulting tax receipts can be earmarked to subsidise preventative healthcare and nutritional education.",
  },
};


/**
 * Beginner templates: short texts, max 2 errors, guided steps, sentence starters,
 * a word bank, and reachable (~Band 6.0) reference answers in plain English.
 */
const BEGINNER_DRILL_TEMPLATES: Record<DrillType, MicroDrillChallenge> = {
  overview_sprint: {
    drillType: "overview_sprint",
    level: "beginner",
    targetCriterion: "TA_TR",
    title: "Write a Chart Summary (2 Sentences)",
    instructions:
      "Look at the chart. Write 2 short sentences that say what changed in general. Do not write any numbers.",
    timeLimitSeconds: 300,
    sourceContext: "Cinema vs. Streaming: How People Watch Films (2015–2035)",
    chartSpec: {
      kind: "bar",
      title: "How People Watch New Films (% of viewers)",
      xAxisKey: "year",
      yAxisUnit: "%",
      series: [
        { key: "cinema", label: "Cinema", color: "#475569" },
        { key: "streaming", label: "Streaming at home", color: "#16656e" },
      ],
      data: [
        { year: "2015", cinema: 70, streaming: 30 },
        { year: "2020", cinema: 55, streaming: 45 },
        { year: "2025", cinema: 40, streaming: 60 },
        { year: "2030", cinema: 30, streaming: 70 },
        { year: "2035", cinema: 25, streaming: 75 },
      ],
    },
    steps: [
      { prompt: "Look at the bars. Which one goes up? Which one goes down?" },
      {
        prompt: "Write one sentence about the general change. Start with “Overall,”.",
        starter: "Overall, the share of people who ___ increased, while ___ decreased.",
      },
      {
        prompt: "Write one more sentence about which one is bigger at the end.",
        starter: "By the end of the period, ___ was the most popular way to watch films.",
      },
    ],
    wordBank: [
      "Overall,",
      "increased",
      "decreased",
      "rose steadily",
      "fell",
      "while",
      "became more popular than",
      "by the end of the period",
    ],
    hints: [
      "Start your first sentence with “Overall,”.",
      "Use “while” to compare two things in one sentence.",
      "No numbers! Say “increased” or “fell”, not “70%”.",
    ],
    referenceModelAnswer:
      "Overall, the share of people who watch new films by streaming at home increased, while the share who go to the cinema decreased. By the end of the period, streaming had become much more popular than the cinema.",
  },
  cohesion: {
    drillType: "cohesion",
    level: "beginner",
    targetCriterion: "CC",
    title: "Make Your Sentences Flow",
    instructions:
      "This paragraph has 2 linking words that sound like a list (“Firstly”, “Also”). Rewrite it so the sentences connect more naturally.",
    timeLimitSeconds: 300,
    sourceContext: "Short paragraph about public parks",
    flawedText:
      "Firstly, parks give people a place to exercise. Also, parks are good for children. Children can play outside and make friends.",
    steps: [
      { prompt: "Find the 2 list words: “Firstly” and “Also”." },
      {
        prompt: "Remove “Firstly”. Start the paragraph with the main idea instead.",
        starter: "Parks give people a free place to exercise.",
      },
      {
        prompt: "Connect the next ideas. Use “This…”, “In addition,” or “where”.",
        starter: "They are also important for children, who can ___",
      },
    ],
    wordBank: ["This", "These spaces", "In addition,", "where", "who", "because", "As a result,"],
    hints: [
      "You do not need “Firstly” in a short paragraph. Just start with your idea.",
      "“This” or “These spaces” can point back to the last sentence.",
      "You can join two short sentences with “where” or “who”.",
    ],
    referenceModelAnswer:
      "Parks give people a free place to exercise. They are also important for children, who can play outside and make new friends there.",
  },
  l1_surgeon: {
    drillType: "l1_surgeon",
    level: "beginner",
    targetCriterion: "L1",
    title: "Fix 2 Spanish-to-English Mistakes",
    instructions:
      "This sentence has 2 common mistakes that Spanish speakers make. Find them and write the correct sentence.",
    timeLimitSeconds: 300,
    sourceContext: "One sentence about technology",
    flawedText: "Actually, the technology is very important in our lives.",
    steps: [
      { prompt: "Look at the first word. Does “Actually” mean “actualmente”?" },
      { prompt: "Look at “the technology”. Do we need “the” when we talk about technology in general?" },
      {
        prompt: "Write the correct sentence.",
        starter: "Nowadays, ___ is very important in our lives.",
      },
    ],
    wordBank: ["Nowadays,", "Currently,", "At present,", "technology", "plays an important role in"],
    hints: [
      "“Actually” means “en realidad”. For “actualmente”, use “Nowadays” or “Currently”.",
      "In English, general ideas have no “the”: “Technology is…”, not “The technology is…”.",
    ],
    referenceModelAnswer: "Nowadays, technology plays a very important role in our lives.",
  },
  lexical_hedging: {
    drillType: "lexical_hedging",
    level: "beginner",
    targetCriterion: "LR",
    title: "Make Strong Claims Softer",
    instructions:
      "These sentences say “always” and “all”, which sounds too strong. Rewrite them so they are more careful and a little more formal.",
    timeLimitSeconds: 300,
    sourceContext: "Two sentences about video games",
    flawedText: "Video games are always bad for children. All kids who play them get lots of bad habits.",
    steps: [
      { prompt: "Find the very strong words: “always”, “All”, “lots of”." },
      {
        prompt: "Change them to softer words like “can”, “some”, “many”.",
        starter: "Video games can be ___ for children.",
      },
      {
        prompt: "Change one informal word (“kids” or “lots of”) to a more formal word.",
        starter: "Some children who play them for many hours may ___",
      },
    ],
    wordBank: ["can", "may", "some", "many", "often", "children", "a number of", "harmful", "develop"],
    hints: [
      "“always” → “can” or “often”. “All” → “Some” or “Many”.",
      "“kids” → “children”. “lots of” → “a number of” or “several”.",
    ],
    referenceModelAnswer:
      "Video games can be harmful for children. Some children who play them for many hours may develop bad habits.",
  },
};

function beginnerL1(
  title: string,
  instructions: string,
  flawedText: string,
  steps: MicroDrillStep[],
  wordBank: string[],
  hints: string[],
  referenceModelAnswer: string
): MicroDrillChallenge {
  return {
    drillType: "l1_surgeon",
    level: "beginner",
    targetCriterion: "L1",
    title,
    instructions,
    timeLimitSeconds: 300,
    sourceContext: "One short sentence to correct",
    flawedText,
    steps,
    wordBank,
    hints,
    referenceModelAnswer,
  };
}

/** Beginner L1 drills matched to each native-language profile (used without an AI key). */
const BEGINNER_L1_TEMPLATES: Record<NativeLanguageCode, MicroDrillChallenge> = {
  es: BEGINNER_DRILL_TEMPLATES.l1_surgeon,
  pt: beginnerL1(
    "Fix 2 Portuguese-to-English Mistakes",
    "This sentence has 2 common mistakes that Portuguese speakers make. Find them and write the correct sentence.",
    "Actually, the technology is very important in our lives.",
    [
      { prompt: "Look at the first word. Does “Actually” mean “atualmente”?" },
      { prompt: "Look at “the technology”. Do we need “the” when we talk about technology in general?" },
      { prompt: "Write the correct sentence.", starter: "Nowadays, ___ is very important in our lives." },
    ],
    ["Nowadays,", "Currently,", "At present,", "technology", "plays an important role in"],
    [
      "“Actually” means “na verdade”. For “atualmente”, use “Nowadays” or “Currently”.",
      "In English, general ideas have no “the”: “Technology is…”, not “The technology is…”.",
    ],
    "Nowadays, technology plays a very important role in our lives."
  ),
  fr: beginnerL1(
    "Fix 2 French-to-English Mistakes",
    "This sentence has 2 common mistakes that French speakers make. Find them and write the correct sentence.",
    "Actually, the technology is very important in our lives.",
    [
      { prompt: "Look at the first word. Does “Actually” mean “actuellement”?" },
      { prompt: "Look at “the technology”. Do we need “the” when we talk about technology in general?" },
      { prompt: "Write the correct sentence.", starter: "Nowadays, ___ is very important in our lives." },
    ],
    ["Nowadays,", "Currently,", "At present,", "technology", "plays an important role in"],
    [
      "“Actually” means “en fait”. For “actuellement”, use “Nowadays” or “Currently”.",
      "In English, general ideas have no “the”: “Technology is…”, not “The technology is…”.",
    ],
    "Nowadays, technology plays a very important role in our lives."
  ),
  zh: beginnerL1(
    "Fix 2 Plural & Verb Mistakes",
    "This sentence has 2 common mistakes that Mandarin speakers make with plurals and verbs. Find them and write the correct sentence.",
    "Many student needs a computer for study.",
    [
      { prompt: "Look at “Many student”. After “many”, should the noun be singular or plural?" },
      { prompt: "Now look at the verb “needs”. Does it match a plural subject?" },
      { prompt: "Write the correct sentence.", starter: "Many ___ need a computer for ___." },
    ],
    ["students", "need", "studying", "their studies", "many", "a computer"],
    [
      "After “many”, use a plural noun: “many students”.",
      "Plural subjects take the verb without “-s”: “students need”, not “students needs”.",
    ],
    "Many students need a computer for their studies."
  ),
  ar: beginnerL1(
    "Fix 2 Sentence-Pattern Mistakes",
    "This sentence has 2 common mistakes that Arabic speakers make. Find them and write the correct sentence.",
    "This is the policy which the government made it, and the people like it.",
    [
      { prompt: "Look at “which the government made it”. Do we need “it” after “which”?" },
      { prompt: "Look at “the people”. Are we talking about specific people or people in general?" },
      { prompt: "Write the correct sentence.", starter: "This is the policy which the government made, and ___ like it." },
    ],
    ["which", "that", "many people", "people", "most people", "support"],
    [
      "“which” already points to “the policy”, so do not add “it” at the end.",
      "For people in general, do not use “the”: “people like it” or “many people like it”.",
    ],
    "This is the policy which the government made, and many people like it."
  ),
  hi: beginnerL1(
    "Fix 2 Preposition Mistakes",
    "This sentence has 2 common mistakes that Hindi speakers make with small words. Find them and write the correct sentence.",
    "Students should discuss about the problem and cope up with stress.",
    [
      { prompt: "Look at “discuss about”. Does “discuss” need “about” after it?" },
      { prompt: "Look at “cope up with”. Is “up” needed?" },
      { prompt: "Write the correct sentence.", starter: "Students should discuss ___ and cope with ___." },
    ],
    ["discuss", "cope with", "talk about", "deal with", "the problem", "stress"],
    [
      "Say “discuss the problem” (no “about”), or “talk about the problem”.",
      "The correct phrase is “cope with”, not “cope up with”.",
    ],
    "Students should discuss the problem and cope with stress."
  ),
  neutral: beginnerL1(
    "Fix 2 Small Grammar Mistakes",
    "This sentence has 2 common small mistakes with “the”. Find them and write the correct sentence.",
    "The technology is very important in the our lives.",
    [
      { prompt: "Look at “The technology”. Do we need “the” when we talk about technology in general?" },
      { prompt: "Look at “the our lives”. Can “the” and “our” go together?" },
      { prompt: "Write the correct sentence.", starter: "___ is very important in ___ lives." },
    ],
    ["Technology", "our", "plays an important role in", "very important", "daily lives"],
    [
      "In English, general ideas have no “the”: “Technology is…”.",
      "Use “the” or “our”, never both: “our lives”.",
    ],
    "Technology plays a very important role in our lives."
  ),
};

/** Extra guiding steps so intermediate drills are scaffolded even without an AI key. */
const INTERMEDIATE_DRILL_STEPS: Record<DrillType, MicroDrillStep[]> = {
  overview_sprint: [
    { prompt: "Find the biggest overall trend: which category rises most, and which falls most?" },
    { prompt: "Write sentence 1 about the general change, starting with “Overall,” (no numbers)." },
    { prompt: "Write sentence 2 about the most striking contrast or the ranking at the end of the period." },
  ],
  cohesion: [
    { prompt: "Find the 4 list-style linkers (“Firstly”, “Moreover”, “On the other hand”, “Because of this”)." },
    { prompt: "Join sentences 1 and 2, using “This…” or “These…” to point back to the previous idea." },
    { prompt: "Merge the last two sentences into one contrast sentence starting with “Yet” or “However,”." },
  ],
  l1_surgeon: [
    { prompt: "Read word by word and underline anything that looks like a direct translation from your first language." },
    { prompt: "Check the joins between clauses: is “however” between two full sentences with only commas around it?" },
    { prompt: "Rewrite the sentence with each mistake fixed, keeping the same meaning." },
  ],
  lexical_hedging: [
    { prompt: "Underline every absolute word (“definitely”, “every”, “all”, “right away”)." },
    { prompt: "Replace them with careful language: “can”, “may”, “tends to”, “many”." },
    { prompt: "Swap informal phrases (“big taxes”, “lots of money”) for more formal ones." },
  ],
};

function levelGuidance(level: DrillLevel, currentBand: number, targetBand: number) {
  if (level === "beginner") {
    const band = Math.max(currentBand + 1, 5.5);
    return {
      band,
      generation: `The student is a BEGINNER writer (current band about ${currentBand.toFixed(
        1
      )}). Use simple everyday topics and simple English (CEFR B1) in the title, instructions, steps and hints. Do NOT use grammar jargon (no "nominalisation", "subordination", "calque", "cohesive device"). Keep any "flawedText" to 1-3 short sentences with at most 2 errors. Give 3 small "steps", each with a short "prompt" and, where useful, a fill-in "starter" sentence frame using "___" for the gaps. Give a "wordBank" of 6-9 useful words or short phrases. The "referenceModelAnswer" must be a simple, correct answer at about Band ${band.toFixed(
        1
      )} — NOT an advanced Band 8 answer.`,
      stepsSchema: `,
  "steps": [{ "prompt": "...", "starter": "... ___ ..." }],
  "wordBank": ["...", "..."]`,
    };
  }
  if (level === "intermediate") {
    const band = Math.max(currentBand + 1, 6.5);
    return {
      band,
      generation: `The student is an INTERMEDIATE writer (current band about ${currentBand.toFixed(
        1
      )}). Keep explanations clear and explain any technical term briefly. Give 2-3 short "steps" that break the task into parts. The "referenceModelAnswer" should be at about Band ${band.toFixed(1)}.`,
      stepsSchema: `,
  "steps": [{ "prompt": "..." }]`,
    };
  }
  return { band: Math.max(targetBand, 8.0), generation: "", stepsSchema: "" };
}

export async function generateMicroDrillWithAI(params: {
  userId: string;
  drillType: DrillType;
  level?: DrillLevel;
  vaultHighlights?: string[];
}): Promise<MicroDrillChallenge> {
  const cfg = await resolveUserAndAIConfig(params.userId);
  const level: DrillLevel = params.level || "advanced";
  const drillType: DrillType = BUILTIN_DRILL_TEMPLATES[params.drillType] ? params.drillType : "cohesion";
  const l1Code: NativeLanguageCode = cfg.l1CoachingEnabled ? cfg.nativeLanguage : "neutral";
  const base =
    level === "beginner"
      ? drillType === "l1_surgeon"
        ? BEGINNER_L1_TEMPLATES[l1Code] || BEGINNER_L1_TEMPLATES.neutral
        : BEGINNER_DRILL_TEMPLATES[drillType]
      : BUILTIN_DRILL_TEMPLATES[drillType];
  const fallback: MicroDrillChallenge = {
    ...base,
    level,
    steps: base.steps ?? (level === "intermediate" ? INTERMEDIATE_DRILL_STEPS[drillType] : undefined),
  };
  const guidance = levelGuidance(level, cfg.currentBand, cfg.targetBand);

  if (!cfg.apiKey || params.drillType === "overview_sprint") {
    // For overview_sprint without key (or fast reliable chart rendering), if key exists we can also generate fresh charts!
    if (!cfg.apiKey) return fallback;
  }

  const l1Profile = cfg.l1CoachingEnabled
    ? L1_LANGUAGE_PROFILES[cfg.nativeLanguage]
    : L1_LANGUAGE_PROFILES.neutral;

  const vaultNote =
    params.vaultHighlights && params.vaultHighlights.length > 0
      ? `Incorporate patterns similar to the student's recent Vault weaknesses: ${params.vaultHighlights.join(
          " | "
        )}`
      : "";

  const raw = await callLLM({
    userId: cfg.userId,
    keySource: cfg.keySource,
    provider: cfg.provider,
    apiKey: cfg.apiKey!,
    model: cfg.fastModel,
    fallbackModels: PROVIDER_DEFAULTS[cfg.provider]?.fastFallbacks,
    feature: "drill_generate",
    jsonMode: true,
    thinkingMode: "disabled",
    timeoutMs: 40_000,
    systemPrompt:
      level === "advanced"
        ? `You design crisp 5-minute Academic IELTS writing micro-drills calibrated for Band ${cfg.targetBand.toFixed(
            1
          )}+ (${l1Profile.label}).`
        : `You design friendly, scaffolded 5-minute IELTS writing micro-drills for ${level} writers, aiming at about Band ${guidance.band.toFixed(
            1
          )} (${l1Profile.label}).`,
    userPrompt: `Create a fresh 5-minute drill of type "${params.drillType}".
${guidance.generation}
${vaultNote}
${
  params.drillType === "overview_sprint"
    ? `Include a "chartSpec" with ${level === "beginner" ? 2 : 3} series and 5 data points using this EXACT shape (all data values must be plain numbers, and "yAxisUnit" must be a short suffix like "%", "k", "M", or "" — put longer unit notes in "subtitle"):
{ "kind": "line" | "bar", "title": "...", "subtitle": "...", "xAxisKey": "year", "yAxisUnit": "%", "series": [{ "key": "s1", "label": "Series 1", "color": "#16656e" }, { "key": "s2", "label": "Series 2", "color": "#c55e1b" }], "data": [{ "year": "2015", "s1": 20, "s2": 45 }, ... (5 points)] }
Set "flawedText" to null. Ask the student to write ONLY a 2-sentence Task 1 Overview without raw statistics.`
    : `Set "chartSpec" to null and provide a ${level === "beginner" ? "1-3 short sentence" : "2-3 sentence"} "flawedText" paragraph exhibiting realistic Band ${
        level === "beginner" ? "4.5–5.5" : "6.0–6.5"
      } weaknesses for "${params.drillType}" (${
        params.drillType === "l1_surgeon" ? l1Profile.coachingFocus : "mechanical linkers or informal unhedged claims"
      }).`
}

Return JSON:
{
  "drillType": "${params.drillType}",
  "targetCriterion": "${fallback.targetCriterion}",
  "title": "...",
  "instructions": "...",
  "timeLimitSeconds": ${params.drillType === "overview_sprint" && level !== "beginner" ? 240 : 300},
  "sourceContext": "...",
  "chartSpec": ${params.drillType === "overview_sprint" ? "{ ... }" : "null"},
  "flawedText": ${params.drillType === "overview_sprint" ? "null" : '"..."'},
  "hints": ["...", "..."],
  "referenceModelAnswer": "${
    level === "advanced" ? "Band 8.5" : `Band ${guidance.band.toFixed(1)}`
  } reference rewrite/overview"${guidance.stepsSchema}
}`,
  });

  const parsed = extractJsonObject<Partial<MicroDrillChallenge>>(raw);
  const chartSpec =
    params.drillType === "overview_sprint"
      ? normalizeCartesianChartSpec(parsed.chartSpec) ?? fallback.chartSpec
      : undefined;

  return {
    drillType: params.drillType,
    targetCriterion: parsed.targetCriterion || fallback.targetCriterion,
    title: parsed.title || fallback.title,
    instructions: parsed.instructions || fallback.instructions,
    timeLimitSeconds: parsed.timeLimitSeconds || fallback.timeLimitSeconds,
    sourceContext: parsed.sourceContext || fallback.sourceContext,
    chartSpec,
    flawedText: parsed.flawedText || fallback.flawedText,
    hints: parsed.hints?.length ? parsed.hints : fallback.hints,
    referenceModelAnswer: parsed.referenceModelAnswer || fallback.referenceModelAnswer,
    level,
    steps: sanitizeSteps(parsed.steps) || fallback.steps,
    wordBank: sanitizeWordBank(parsed.wordBank) || fallback.wordBank,
  };
}

function sanitizeSteps(steps: unknown): MicroDrillChallenge["steps"] | undefined {
  if (!Array.isArray(steps)) return undefined;
  const clean = steps
    .filter((st): st is { prompt: string; starter?: unknown } =>
      Boolean(st && typeof st === "object" && typeof (st as { prompt?: unknown }).prompt === "string")
    )
    .slice(0, 4)
    .map((st) => ({
      prompt: st.prompt,
      starter: typeof st.starter === "string" && st.starter.trim() ? st.starter : undefined,
    }));
  return clean.length ? clean : undefined;
}

function sanitizeWordBank(words: unknown): string[] | undefined {
  if (!Array.isArray(words)) return undefined;
  const clean = words.filter((w): w is string => typeof w === "string" && w.trim().length > 0).slice(0, 12);
  return clean.length ? clean : undefined;
}

/** Offline (no API key) encouraging feedback for beginner / intermediate drills. */
function scoreScaffoldedDrillLocally(
  challenge: MicroDrillChallenge,
  userResponse: string,
  level: DrillLevel
): MicroDrillScore {
  const words = countEssayWords(userResponse);
  const lower = userResponse.toLowerCase();
  const minWords = level === "beginner" ? 8 : 15;
  const usedWords = (challenge.wordBank || []).filter((w) =>
    lower.includes(w.toLowerCase().replace(/[,.]$/, ""))
  );
  const unfilledGaps = /_{2,}/.test(userResponse);
  const mechanical = /\b(firstly|secondly|moreover|also,)\b/i.test(userResponse);
  const hasNumbers = challenge.drillType === "overview_sprint" && /\d/.test(userResponse);
  const copiedOriginal =
    Boolean(challenge.flawedText) &&
    userResponse.trim().toLowerCase() === challenge.flawedText!.trim().toLowerCase();

  let score = 5;
  if (words >= minWords) score += 2;
  if (usedWords.length >= 2) score += 1;
  if (!unfilledGaps && !mechanical && !hasNumbers && !copiedOriginal) score += 1;
  if (words < minWords / 2) score -= 2;
  // An answer that still has blank "___" gaps or is just the original text isn't finished yet.
  if (unfilledGaps || copiedOriginal) score = Math.min(score, 5);
  score = Math.min(9, Math.max(3, score));

  const oneThingToFix = unfilledGaps
    ? "Fill in the “___” gaps in the sentence starter with your own words."
    : copiedOriginal
    ? "Your answer is the same as the original. Try changing the words the steps point to."
    : hasNumbers
    ? "Remove the numbers. A summary talks about the general change, not exact figures."
    : mechanical
    ? "Try removing list words like “Firstly” or “Also”, and start with your idea."
    : words < minWords
    ? `Write a little more — aim for at least ${minWords} words.`
    : "Compare your answer with the example and pick one phrase to borrow next time.";

  return {
    scoreOutOf10: score,
    estimatedBandImpact: score >= 7 ? "Great progress — keep going!" : "Good start — you are getting there",
    feedback:
      (score >= 7 ? "Nice work! Your answer is clear and complete. " : "Good start! ") +
      (usedWords.length
        ? `You used useful words like “${usedWords.slice(0, 2).join("”, “")}”. `
        : "Try using 2 or 3 words from the word bank next time. ") +
      "Compare your answer with the example below. (Add an AI key in Settings for personal feedback.)",
    improvedVersion: challenge.referenceModelAnswer,
    keyTakeaway: challenge.hints[0] ?? "Short, clear sentences are better than long, confusing ones.",
    oneThingToFix,
  };
}

export async function scoreMicroDrillWithAI(params: {
  userId: string;
  challenge: MicroDrillChallenge;
  userResponse: string;
  level?: DrillLevel;
}): Promise<MicroDrillScore> {
  const cfg = await resolveUserAndAIConfig(params.userId);
  const { challenge, userResponse } = params;
  const level: DrillLevel = params.level || challenge.level || "advanced";

  if (!cfg.apiKey && level !== "advanced") {
    return scoreScaffoldedDrillLocally(challenge, userResponse, level);
  }

  if (level !== "advanced") {
    return scoreScaffoldedDrillWithAI(cfg, challenge, userResponse, level);
  }

  if (!cfg.apiKey) {
    const words = countEssayWords(userResponse);
    const hasMechanical = /\b(firstly|secondly|moreover|in conclusion|on the other hand)\b/i.test(
      userResponse
    );
    const score = words < 15 ? 4 : hasMechanical ? 6 : 8;
    return {
      scoreOutOf10: score,
      estimatedBandImpact: score >= 8 ? "Band 7.5–8.0 execution" : "Band 6.0–6.5 execution",
      feedback:
        score >= 8
          ? "Clean, compact academic phrasing! Compare your version against the Band 8.5 reference below (add your DeepSeek API key in Settings for live line-by-line drill scoring)."
          : hasMechanical
          ? "Watch out—a mechanical connector slipped in. Try linking clauses with demonstrative noun phrases ('This shift...') instead."
          : "Expand your response into complete academic sentences addressing every flaw in the prompt.",
      improvedVersion: challenge.referenceModelAnswer,
      keyTakeaway:
        "High-band Academic IELTS prose relies on clause integration, hedging, and precision rather than memorised transition phrases.",
    };
  }

  const raw = await callLLM({
    userId: cfg.userId,
    keySource: cfg.keySource,
    provider: cfg.provider,
    apiKey: cfg.apiKey,
    model: cfg.fastModel,
    fallbackModels: PROVIDER_DEFAULTS[cfg.provider]?.fastFallbacks,
    feature: "drill_score",
    jsonMode: true,
    thinkingMode: "disabled",
    timeoutMs: 35_000,
    systemPrompt:
      "You are a sharp, encouraging Academic IELTS Writing Coach grading a 5-minute targeted micro-drill.",
    userPrompt: `DRILL: ${challenge.title} (${challenge.drillType})
INSTRUCTIONS: ${challenge.instructions}
${challenge.flawedText ? `ORIGINAL FLAWED PASSAGE:\n${challenge.flawedText}\n` : ""}
${challenge.chartSpec ? `CHART DATA:\n${JSON.stringify(challenge.chartSpec)}\n` : ""}
REFERENCE BAND 8.5 ANSWER:
${challenge.referenceModelAnswer}

STUDENT SUBMISSION:
"""
${userResponse}
"""

Return JSON:
{
  "scoreOutOf10": 8,
  "estimatedBandImpact": "e.g. Band 8.0 Coherence execution",
  "feedback": "2-3 crisp sentences praising what worked and pinpointing any remaining slip.",
  "improvedVersion": "Polished version of the student's own phrasing at Band 8.5",
  "keyTakeaway": "1 memorable rule to carry into their next timed essay"
}`,
  });

  return extractJsonObject<MicroDrillScore>(raw);
}

async function scoreScaffoldedDrillWithAI(
  cfg: Awaited<ReturnType<typeof resolveUserAndAIConfig>>,
  challenge: MicroDrillChallenge,
  userResponse: string,
  level: DrillLevel
): Promise<MicroDrillScore> {
  const { band } = levelGuidance(level, cfg.currentBand, cfg.targetBand);
  const steps = challenge.steps?.length
    ? `GUIDED STEPS:\n${challenge.steps.map((st, i) => `${i + 1}. ${st.prompt}`).join("\n")}\n`
    : "";

  const raw = await callLLM({
    userId: cfg.userId,
    keySource: cfg.keySource,
    provider: cfg.provider,
    apiKey: cfg.apiKey!,
    model: cfg.fastModel,
    fallbackModels: PROVIDER_DEFAULTS[cfg.provider]?.fastFallbacks,
    feature: "drill_score",
    jsonMode: true,
    thinkingMode: "disabled",
    timeoutMs: 35_000,
    systemPrompt:
      level === "beginner"
        ? "You are a warm, patient writing coach for beginner English writers. Use simple English (CEFR B1) and short sentences. Never use grammar jargon. Always praise one real strength first, then give ONE clear fix."
        : "You are a clear, encouraging IELTS writing coach for intermediate students. Explain any technical term in plain words. Praise one strength, then give the most important fix.",
    userPrompt: `DRILL: ${challenge.title} (${challenge.drillType}, level: ${level})
INSTRUCTIONS: ${challenge.instructions}
${steps}${challenge.flawedText ? `ORIGINAL PASSAGE:\n${challenge.flawedText}\n` : ""}
${challenge.chartSpec ? `CHART DATA:\n${JSON.stringify(challenge.chartSpec)}\n` : ""}
EXAMPLE ANSWER (about Band ${band.toFixed(1)}):
${challenge.referenceModelAnswer}

STUDENT SUBMISSION:
"""
${userResponse}
"""

Score kindly but honestly for a ${level} writer (do not compare with Band 8 writing; reward effort and correct basics).

Return JSON:
{
  "scoreOutOf10": 7,
  "estimatedBandImpact": "Short encouraging progress label, e.g. 'Moving toward Band ${band.toFixed(1)}'",
  "feedback": "2 short, simple sentences: one real strength, then what to improve.",
  "oneThingToFix": "The single most important change, in one simple sentence.",
  "stepFeedback": ["One short line per guided step: done well / try this"],
  "improvedVersion": "The student's OWN answer with only the necessary fixes (keep their words and ideas; about Band ${band.toFixed(
    1
  )}, not advanced)",
  "keyTakeaway": "1 simple rule to remember, in under 15 words"
}`,
  });

  const parsed = extractJsonObject<Partial<MicroDrillScore>>(raw);
  return {
    scoreOutOf10: typeof parsed.scoreOutOf10 === "number" ? parsed.scoreOutOf10 : 6,
    estimatedBandImpact: parsed.estimatedBandImpact || "Good progress",
    feedback: parsed.feedback || "Good effort! Compare your answer with the example below.",
    improvedVersion: parsed.improvedVersion || challenge.referenceModelAnswer,
    keyTakeaway: parsed.keyTakeaway || challenge.hints[0] || "",
    oneThingToFix: parsed.oneThingToFix,
    stepFeedback: Array.isArray(parsed.stepFeedback)
      ? parsed.stepFeedback.filter((x): x is string => typeof x === "string")
      : undefined,
  };
}

/* ============================================================================
 * 6. LOCAL DIAGNOSTIC PREVIEW ENGINE (Zero-Key Graceful Fallback)
 * ============================================================================ */

function buildHeuristicDiagnosticPreview(
  input: EvaluateEssayInput,
  cfg: ResolvedAIConfig,
  wordCount: number,
  minWords: number
): EvaluationPayload {
  const text = input.essayText;
  const paragraphs = text
    .split(/\n\s*\n/)
    .map((p) => p.trim())
    .filter(Boolean);

  const annotations: InlineAnnotation[] = [];

  // Scan mechanical linkers
  const linkerMatches = text.match(
    /\b(Firstly|Secondly|Thirdly|Moreover|Furthermore|In a nutshell|Last but not least|On the other hand)[^.!?]{0,55}/gi
  );
  if (linkerMatches) {
    for (const span of linkerMatches.slice(0, 2)) {
      annotations.push({
        id: `prev-cc-${annotations.length + 1}`,
        category: "CC",
        severity: "upgrade",
        originalSpan: span.trim(),
        replacement: span
          .trim()
          .replace(
            /^(Firstly|Secondly|Thirdly|Moreover|Furthermore|In a nutshell|Last but not least),?\s*/i,
            "Notably, "
          ),
        explanation:
          "Mechanical sequence linkers often cap Coherence & Cohesion at Band 6.5. Prefer demonstrative referencing ('This trend...', 'Such measures...').",
      });
    }
  }

  // Scan Spanish -> English L1 false cognates & comma splices
  const actuallyMatch = text.match(
    /\b(?:Actually,\s+[^.!?]{5,60}|(?:has|have|had)?\s*actually\s+(?:grown|grew|risen|rose|fallen|fell|increased)[^.!?]{0,35})/i
  );
  if (actuallyMatch) {
    const rawSpan = actuallyMatch[0].trim();
    const cleanedSpan = /^actually,/i.test(rawSpan)
      ? rawSpan.replace(/^Actually/i, "Currently")
      : rawSpan
          .replace(/\bactually\s+/i, "")
          .replace(/\b(grown|grew)\s+very\s+fast\b/i, "expanded rapidly in recent years")
          .replace(/\bvery\s+fast\b/i, "rapidly");
    annotations.push({
      id: `prev-l1-${annotations.length + 1}`,
      category: "L1",
      severity: "error",
      originalSpan: rawSpan,
      replacement: cleanedSpan,
      explanation:
        "Spanish → English false friend: 'actually' means 'in fact', whereas 'actualmente' translates to 'currently / in recent years'.",
    });
  }

  const commaSpliceMatch = text.match(/[^.!?\n]{10,45},\s*(however|therefore|nevertheless),\s*[^.!?\n]{10,45}/i);
  if (commaSpliceMatch) {
    annotations.push({
      id: `prev-gra-${annotations.length + 1}`,
      category: "GRA",
      severity: "error",
      originalSpan: commaSpliceMatch[0].trim(),
      replacement: commaSpliceMatch[0].trim().replace(/,\s*(however|therefore|nevertheless),/i, "; $1,"),
      explanation:
        "Comma splice (Grammar error): conjunctive adverbs ('however', 'therefore') between two independent clauses require a semicolon or period.",
    });
  }

  // Scan informal vocabulary
  const informalMatch = text.match(/\b(a lot of|lots of|very big|really important|get better|nowadays people)\b[^.!?]{0,35}/i);
  if (informalMatch) {
    annotations.push({
      id: `prev-lr-${annotations.length + 1}`,
      category: "LR",
      severity: "upgrade",
      originalSpan: informalMatch[0].trim(),
      replacement: informalMatch[0]
        .trim()
        .replace(/a lot of|lots of/i, "a substantial proportion of")
        .replace(/very big/i, "pronounced")
        .replace(/really important/i, "pivotal"),
      explanation: "Upgrade conversational high-frequency phrasing to formal C1 academic register.",
    });
  }

  const underLengthPenalty = wordCount < minWords ? 1.0 : 0;
  const hasOverviewSignal =
    input.taskType === "task1"
      ? /\b(overall|in general|broadly|main trend)\b/i.test(text)
      : paragraphs.length >= 4;

  const taTrBand = roundIeltsBand((hasOverviewSignal ? 7.0 : 6.0) - underLengthPenalty);
  const ccBand = roundIeltsBand(
    paragraphs.length >= 3 && (!linkerMatches || linkerMatches.length <= 1) ? 7.0 : 6.5
  );
  const lrBand = roundIeltsBand(informalMatch ? 6.5 : 7.0);
  const graBand = roundIeltsBand(commaSpliceMatch ? 6.0 : 6.5);
  const overallBand = roundIeltsBand((taTrBand + ccBand + lrBand + graBand) / 4);

  return {
    overallBand,
    taTrBand,
    ccBand,
    lrBand,
    graBand,
    examinerSummary: `[Local Diagnostic Preview — Add your DeepSeek API key in Settings for full DeepSeek-V4-Pro grading & personalised Band ${cfg.targetBand.toFixed(
      1
    )} rewrites.] Structural scan analysed ${wordCount} words across ${
      paragraphs.length
    } paragraph(s).`,
    criteria: {
      TA_TR: {
        band: taTrBand,
        descriptorSummary:
          wordCount < minWords
            ? `Under minimum length (${wordCount}/${minWords} words), which directly penalises Task fulfilment.`
            : hasOverviewSignal
            ? "Clear macro structure detected; verify every prompt facet is fully extended."
            : input.taskType === "task1"
            ? "No explicit Overview signpost ('Overall,...') detected—missing a clear overview caps Task Achievement at Band 5.0–6.0."
            : "Aim for a 4-paragraph structure (Intro with clear thesis, 2 extended body paragraphs, concise conclusion).",
        strengths: wordCount >= minWords ? [`Meets the ${minWords}-word minimum (${wordCount} words)`] : [],
        limitingFactors:
          wordCount < minWords
            ? [`Add at least ${minWords - wordCount} more words of substantive analysis`]
            : [],
        howToGainHalfBand:
          input.taskType === "task1"
            ? "Pair a 2-sentence number-free Overview with crisp comparative figures in both body paragraphs."
            : "Back each body paragraph's topic claim with a specific mechanism and concrete example.",
      },
      CC: {
        band: ccBand,
        descriptorSummary: `Organised into ${paragraphs.length} paragraph(s)${
          linkerMatches ? ` with ${linkerMatches.length} mechanical connector(s) flagged` : ""
        }.`,
        strengths: paragraphs.length >= 3 ? ["Clear multi-paragraph visual separation"] : [],
        limitingFactors: linkerMatches
          ? ["Relies on formulaic sentence-initial linkers"]
          : [],
        howToGainHalfBand:
          "Connect sentences via demonstrative noun phrases ('This disparity...', 'Such policies...') rather than list markers.",
      },
      LR: {
        band: lrBand,
        descriptorSummary: "Heuristic scan of register formality and academic collocations.",
        strengths: ["Readable academic vocabulary base"],
        limitingFactors: informalMatch ? ["Conversational intensifiers/quantifiers detected"] : [],
        howToGainHalfBand:
          "Replace generic verbs ('make', 'get', 'have') with domain-precise verbs ('generate', 'incur', 'exhibit').",
      },
      GRA: {
        band: graBand,
        descriptorSummary: "Clause boundary and punctuation consistency check.",
        strengths: ["Complete sentence framing"],
        limitingFactors: commaSpliceMatch ? ["Comma splice across conjunctive adverb"] : [],
        howToGainHalfBand:
          "Combine participial phrases and concessive clauses ('While X..., Y...') with spotless semicolon/comma control.",
      },
    },
    inlineAnnotations: annotations,
    targetBandRewrite:
      "Connect your DeepSeek API key (or Gemini / OpenAI / Anthropic) in Settings → AI Engine to generate a full side-by-side Band 8.0+ rewrite of your exact essay arguments!",
    rewriteKeyChanges: [
      "Add DEEPSEEK_API_KEY in Settings (or .env) to activate DeepSeek-V4-Pro rewrites and multimodal Task 1 chart verification.",
    ],
    topPriorities: [
      wordCount < minWords
        ? `Expand response past the ${minWords}-word threshold (${wordCount} words written).`
        : "Configure your DeepSeek API key in Settings to unlock full examiner commentary.",
      "Eliminate mechanical sequence linkers in favour of thematic referencing.",
      "Double-check L1 false cognates and comma-spliced conjunctive adverbs.",
    ],
    vaultItems: annotations.slice(0, 3).map((a) => ({
      itemType: a.severity === "error" ? "error" : "collocation",
      criterion: a.category,
      originalText: a.originalSpan,
      upgradedText: a.replacement,
      explanation: a.explanation,
      exampleSentence: a.replacement,
    })),
  };
}
