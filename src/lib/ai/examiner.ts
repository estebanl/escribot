import {
  callLLM,
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
import type {
  AnnotationCategory,
  ChartSpec,
  DrillType,
  EvaluationPayload,
  InlineAnnotation,
  MicroDrillChallenge,
  MicroDrillScore,
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
  config?: ResolvedAIConfig
): Promise<string> {
  const cfg = config || (await resolveUserAndAIConfig());
  if (!cfg.apiKey) {
    return "Uploaded visual chart/diagram screenshot (configure DEEPSEEK_API_KEY in Settings for live multimodal chart figure extraction).";
  }

  const visionModel = PROVIDER_DEFAULTS[cfg.provider]?.visionModel || cfg.fastModel;

  try {
    const raw = await callLLM({
      provider: cfg.provider,
      apiKey: cfg.apiKey,
      model: visionModel,
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
  taskType: TaskType;
  promptTitle: string;
  promptText: string;
  visualContext?: string | null;
  essayText: string;
  draftVersion: number;
  previousBand?: number | null;
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

export async function evaluateEssayWithAI(
  input: EvaluateEssayInput
): Promise<{
  payload: EvaluationPayload;
  providerUsed: string;
  modelUsed: string;
}> {
  const cfg = await resolveUserAndAIConfig();
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
      ? "Task Achievement (TA): accurate data selection, clear macro Overview paragraph (mandatory for Band 7+), key comparisons, no personal opinion/speculation, 150+ words."
      : "Task Response (TR): addresses all parts of the prompt fully, maintains a clear & consistent position throughout, extends main ideas with specific reasoning/examples rather than over-generalising, 250+ words.";

  const targetRewriteBand = Math.max(cfg.targetBand, 8.0).toFixed(1);

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
    ? `UNDERLYING VISUAL DATA / DIAGRAM GROUND TRUTH (verify candidate's figures & overview against this):\n${input.visualContext}\n`
    : ""
}CANDIDATE ESSAY:
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
2. CC — Coherence and Cohesion: logical paragraphing, clear central topic per body paragraph, natural cohesive devices WITHOUT mechanical sentence-initial linkers ("Firstly, Secondly, Moreover, In a nutshell").
3. LR — Lexical Resource: natural C1/C2 academic collocations, precise register, effective hedging, accurate word formation & spelling.
4. GRA — Grammatical Range and Accuracy: flexible mix of complex subordinate, relative, participial, and conditional structures with high punctuation & clause accuracy.

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
      "howToGainHalfBand": "1 concrete revision step to reach +0.5/+1.0."
    },
    "CC": { "band": 6.5, "descriptorSummary": "...", "strengths": ["..."], "limitingFactors": ["..."], "howToGainHalfBand": "..." },
    "LR": { "band": 7.0, "descriptorSummary": "...", "strengths": ["..."], "limitingFactors": ["..."], "howToGainHalfBand": "..." },
    "GRA": { "band": 6.5, "descriptorSummary": "...", "strengths": ["..."], "limitingFactors": ["..."], "howToGainHalfBand": "..." }
  },
  "topPriorities": ["Top 1 highest-leverage fix for v${input.draftVersion + 1}", "Top 2...", "Top 3..."]
}`;

  const coachSystemPrompt = `You are a Band ${targetRewriteBand} Academic IELTS Prose Coach & Marginalia Editor.
Linguistic Lens (${l1Profile.label}): ${l1Profile.coachingFocus}

CRITICAL RULES FOR JSON OUTPUT:
- Each "inlineAnnotations" item MUST set "originalSpan" to an EXACT verbatim substring copied character-for-character from the candidate's essay (15 to 140 characters) so it highlights cleanly in the UI. Include 5 to 8 high-impact annotations spanning TA_TR, CC, LR, GRA${
    cfg.l1CoachingEnabled && cfg.nativeLanguage !== "neutral" ? ", and L1" : ""
  }.
- "targetBandRewrite" MUST rewrite the candidate's OWN arguments/data groupings elevated to natural Band ${targetRewriteBand} academic prose (multi-paragraph essay separated by "\\n\\n"), NOT a generic unrelated sample.
- "vaultItems" MUST extract 3 to 4 reusable items (recurring error fixes and high-utility academic collocations) for spaced review.`;

  const coachUserPrompt = `${sharedEssayContext}

Return ONLY a valid JSON object matching this exact structure:
{
  "inlineAnnotations": [
    {
      "id": "ann-1",
      "category": "CC",
      "severity": "upgrade",
      "originalSpan": "exact substring from candidate essay",
      "replacement": "natural Band 8+ revision of that span",
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

  const scorePromise = (async (): Promise<RubricScoreOutput> => {
    try {
      const rawRubric = await callLLM({
        provider: cfg.provider,
        apiKey,
        model: cfg.graderModel,
        systemPrompt: rubricSystemPrompt,
        userPrompt: rubricUserPrompt,
        jsonMode: true,
        thinkingMode: "disabled",
        maxTokens: 1200,
        timeoutMs: 26_000,
      });
      const parsedRubric = extractJsonObject<RubricScoreOutput>(rawRubric);
      input.onProgress?.("4-criterion band scores ready — finalising inline upgrades...");
      return parsedRubric;
    } catch (primaryErr) {
      if (cfg.fastModel && cfg.fastModel !== cfg.graderModel) {
        input.onProgress?.("Switching to fast examiner model to complete band scoring...");
        modelUsed = cfg.fastModel;
        const rawFallback = await callLLM({
          provider: cfg.provider,
          apiKey,
          model: cfg.fastModel,
          systemPrompt: rubricSystemPrompt,
          userPrompt: rubricUserPrompt,
          jsonMode: true,
          thinkingMode: "disabled",
          maxTokens: 1200,
          timeoutMs: 20_000,
        });
        input.onProgress?.("4-criterion band scores ready — finalising inline upgrades...");
        return extractJsonObject<RubricScoreOutput>(rawFallback);
      }
      throw primaryErr;
    }
  })();

  const annotationsPromise = (async (): Promise<AnnotationRewriteOutput> => {
    try {
      const rawCoach = await callLLM({
        provider: cfg.provider,
        apiKey,
        model: cfg.fastModel,
        systemPrompt: coachSystemPrompt,
        userPrompt: coachUserPrompt,
        jsonMode: true,
        thinkingMode: "disabled",
        maxTokens: 2100,
        timeoutMs: 34_000,
      });
      const parsedCoach = extractJsonObject<AnnotationRewriteOutput>(rawCoach);
      input.onProgress?.("Inline annotations & Band 8+ rewrite ready...");
      return parsedCoach;
    } catch {
      const localPreview = buildHeuristicDiagnosticPreview(input, cfg, wordCount, minWords);
      return {
        inlineAnnotations: localPreview.inlineAnnotations,
        targetBandRewrite:
          "Band 8.0+ full rewrite timed out on this run, while your official 4-criterion band scores succeeded above. You can redraft or re-run anytime.",
        rewriteKeyChanges: [],
        vaultItems: localPreview.vaultItems,
      };
    }
  })();

  const [rubricData, coachData] = await Promise.all([scorePromise, annotationsPromise]);

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

  const normalizedAnnotations: InlineAnnotation[] = (coachData.inlineAnnotations || [])
    .filter((a) => a && typeof a.originalSpan === "string" && a.originalSpan.trim().length > 0)
    .map((a, idx) => ({
      id: a.id || `ann-${idx + 1}`,
      category: validCategories.has(a.category) ? a.category : "LR",
      severity: a.severity === "error" ? "error" : "upgrade",
      originalSpan: a.originalSpan.trim(),
      replacement: a.replacement || a.originalSpan,
      explanation: a.explanation || "Academic refinement suggested.",
    }));

  const payload: EvaluationPayload = {
    overallBand,
    taTrBand,
    ccBand,
    lrBand,
    graBand,
    examinerSummary:
      rubricData.examinerSummary ||
      `Scored Band ${overallBand.toFixed(1)} overall across the four official Academic IELTS criteria.`,
    criteria: {
      TA_TR: {
        band: taTrBand,
        descriptorSummary:
          rubricData.criteria?.TA_TR?.descriptorSummary || "Assessed for task fulfilment.",
        strengths: rubricData.criteria?.TA_TR?.strengths || [],
        limitingFactors: rubricData.criteria?.TA_TR?.limitingFactors || [],
        howToGainHalfBand:
          rubricData.criteria?.TA_TR?.howToGainHalfBand ||
          "Extend core points with sharper precision.",
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
    },
    inlineAnnotations: normalizedAnnotations,
    targetBandRewrite:
      coachData.targetBandRewrite ||
      "Target-band rewrite unavailable for this response.",
    rewriteKeyChanges: coachData.rewriteKeyChanges || [],
    topPriorities: rubricData.topPriorities || [
      "Strengthen macro overview / thesis precision",
      "Replace mechanical sequence connectors with cohesive referencing",
      "Upgrade high-frequency verbs into formal academic collocations",
    ],
    vaultItems: (coachData.vaultItems || []).map((v) => ({
      itemType: v.itemType === "collocation" ? "collocation" : "error",
      criterion: validCategories.has(v.criterion) ? v.criterion : "LR",
      originalText: v.originalText || "",
      upgradedText: v.upgradedText || "",
      explanation: v.explanation || "",
      exampleSentence: v.exampleSentence || "",
    })),
  };

  return {
    payload,
    providerUsed: cfg.provider,
    modelUsed,
  };
}

/* ============================================================================
 * 3. FAST <2s COACH: 5-MIN OUTLINE CHECK & ON-DEMAND ACADEMIC TOOLKIT
 * ============================================================================ */

export async function checkOutlineWithAI(params: {
  taskType: TaskType;
  promptText: string;
  visualContext?: string | null;
  outline: OutlineInput;
}): Promise<OutlineFeedback> {
  const cfg = await resolveUserAndAIConfig();
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
                phrase: "accounted for the lion's share / largest proportion",
                usageNote: "Ideal for highlighting dominant pie/bar segments.",
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
    provider: cfg.provider,
    apiKey: cfg.apiKey,
    model: cfg.fastModel,
    jsonMode: true,
    thinkingMode: "disabled",
    timeoutMs: 35_000,
    systemPrompt: `You are a fast, surgical Academic IELTS Band ${cfg.targetBand.toFixed(
      1
    )}+ Writing Coach. Validate the student's 5-minute pre-writing outline BEFORE they write the essay. Do NOT write the essay for them—spot Task Achievement/Response traps, weak grouping, or missing prompt parts, and suggest 4 high-register academic collocations.`,
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
  "overviewOrThesisCheck": "Specific critique of their Overview (Task 1: no raw numbers? captures main trend + extreme?) or Thesis (Task 2: clear position?).",
  "bodyGroupingCheck": "Critique of how logically Body 1 & Body 2 divide the data or arguments.",
  "actionableTweaks": ["2-3 quick bullet adjustments before typing"],
  "suggestedAcademicCollocations": [
    { "phrase": "...", "usageNote": "..." }
  ]
}`,
  });

  return extractJsonObject<OutlineFeedback>(raw);
}

export async function runToolkitWithAI(req: ToolkitRequest): Promise<ToolkitResponse> {
  const cfg = await resolveUserAndAIConfig();
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
        )}+ academic phrasings (one with C1/C2 collocations, one with nuanced academic hedging, one with compact nominalisation). Do NOT write new paragraphs—only refine the selected idea.`
      : req.tool === "l1_check"
      ? `Inspect the selected text through the lens of ${l1Profile.label} (${l1Profile.coachingFocus}). Flag any false cognates, comma splices, article/preposition calques, or unnatural clause length, and provide 2-3 clean native academic alternatives.`
      : `Evaluate this single paragraph for IELTS Coherence & Cohesion and Task ${
          req.taskType === "task1" ? "Achievement" : "Response"
        }: Does it have a clear topic sentence? Are ideas extended logically without mechanical linkers? Give concise diagnosis + 2 surgical improvements (do not rewrite the entire essay).`;

  const raw = await callLLM({
    provider: cfg.provider,
    apiKey: cfg.apiKey,
    model: cfg.fastModel,
    jsonMode: true,
    thinkingMode: "disabled",
    timeoutMs: 35_000,
    systemPrompt:
      "You are an on-demand Academic IELTS writing coach. Be crisp, encouraging, and surgical.",
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
  taskType: TaskType;
  category: PromptCategory;
  topic?: string;
}): Promise<{
  title: string;
  topic: string;
  promptText: string;
  chartSpec?: ChartSpec;
}> {
  const cfg = await resolveUserAndAIConfig();
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
    provider: cfg.provider,
    apiKey: cfg.apiKey,
    model: cfg.fastModel,
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
    chartSpec: parsed.chartSpec || undefined,
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

export async function generateMicroDrillWithAI(params: {
  drillType: DrillType;
  vaultHighlights?: string[];
}): Promise<MicroDrillChallenge> {
  const cfg = await resolveUserAndAIConfig();
  const fallback = BUILTIN_DRILL_TEMPLATES[params.drillType] || BUILTIN_DRILL_TEMPLATES.cohesion;

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
    provider: cfg.provider,
    apiKey: cfg.apiKey!,
    model: cfg.fastModel,
    jsonMode: true,
    thinkingMode: "disabled",
    timeoutMs: 40_000,
    systemPrompt: `You design crisp 5-minute Academic IELTS writing micro-drills calibrated for Band ${cfg.targetBand.toFixed(
      1
    )}+ (${l1Profile.label}).`,
    userPrompt: `Create a fresh 5-minute drill of type "${params.drillType}".
${vaultNote}
${
  params.drillType === "overview_sprint"
    ? `Include a "chartSpec" (kind: "line" or "bar" with 3 series and 5 data points) and set "flawedText" to null. Ask the student to write ONLY a 2-sentence Task 1 Overview without raw statistics.`
    : `Set "chartSpec" to null and provide a 2-3 sentence "flawedText" paragraph exhibiting realistic Band 6.0–6.5 weaknesses for "${params.drillType}" (${
        params.drillType === "l1_surgeon" ? l1Profile.coachingFocus : "mechanical linkers or informal unhedged claims"
      }).`
}

Return JSON:
{
  "drillType": "${params.drillType}",
  "targetCriterion": "${fallback.targetCriterion}",
  "title": "...",
  "instructions": "...",
  "timeLimitSeconds": ${params.drillType === "overview_sprint" ? 240 : 300},
  "sourceContext": "...",
  "chartSpec": null,
  "flawedText": "...",
  "hints": ["...", "..."],
  "referenceModelAnswer": "Band 8.5 reference rewrite/overview"
}`,
  });

  const parsed = extractJsonObject<Partial<MicroDrillChallenge>>(raw);
  return {
    drillType: params.drillType,
    targetCriterion: parsed.targetCriterion || fallback.targetCriterion,
    title: parsed.title || fallback.title,
    instructions: parsed.instructions || fallback.instructions,
    timeLimitSeconds: parsed.timeLimitSeconds || fallback.timeLimitSeconds,
    sourceContext: parsed.sourceContext || fallback.sourceContext,
    chartSpec: parsed.chartSpec || (params.drillType === "overview_sprint" ? fallback.chartSpec : undefined),
    flawedText: parsed.flawedText || fallback.flawedText,
    hints: parsed.hints?.length ? parsed.hints : fallback.hints,
    referenceModelAnswer: parsed.referenceModelAnswer || fallback.referenceModelAnswer,
  };
}

export async function scoreMicroDrillWithAI(params: {
  challenge: MicroDrillChallenge;
  userResponse: string;
}): Promise<MicroDrillScore> {
  const cfg = await resolveUserAndAIConfig();
  const { challenge, userResponse } = params;

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
    provider: cfg.provider,
    apiKey: cfg.apiKey,
    model: cfg.fastModel,
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
  const actuallyMatch = text.match(/\bActually,\s+[^.!?]{5,60}/i);
  if (actuallyMatch) {
    annotations.push({
      id: `prev-l1-${annotations.length + 1}`,
      category: "L1",
      severity: "error",
      originalSpan: actuallyMatch[0].trim(),
      replacement: actuallyMatch[0].trim().replace(/^Actually/i, "Currently"),
      explanation:
        "Spanish → English false friend: 'Actually' means 'in fact', whereas 'actualmente' translates to 'Currently / At present'.",
    });
  }

  const commaSpliceMatch = text.match(/[^.!?\n]{10,45},\s*(however|therefore|nevertheless),\s*[^.!?\n]{10,45}/i);
  if (commaSpliceMatch) {
    annotations.push({
      id: `prev-gra-${annotations.length + 1}`,
      category: "L1",
      severity: "error",
      originalSpan: commaSpliceMatch[0].trim(),
      replacement: commaSpliceMatch[0].trim().replace(/,\s*(however|therefore|nevertheless),/i, "; $1,"),
      explanation:
        "Comma splice: conjunctive adverbs ('however', 'therefore') between two independent clauses require a semicolon or period.",
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
