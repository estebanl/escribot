using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Escribot.Api.Ai.Agents;
using Escribot.Api.Api;
using Escribot.Api.Common;

namespace Escribot.Api.Ai;

/// <summary>
/// IELTS examiner functions: visual extraction, 4-criterion evaluation, outline check,
/// toolkit, prompt generation, and micro-drill generation &amp; scoring.
/// Each workflow runs through a stateless <c>Microsoft.Agents.AI</c> <c>ChatClientAgent</c>.
/// </summary>
public sealed partial class ExaminerService(
    AiConfigResolver configResolver,
    AiCompletionService ai,
    UserHistoryTools? historyTools = null)
{
    private static readonly HashSet<string> ValidCategories = ["TA_TR", "CC", "LR", "GRA", "L1"];

    /* ============================================================================
     * 1. TASK 1 SCREENSHOT MULTIMODAL VISION EXTRACTION (Cached per Prompt)
     * ============================================================================ */

    public async Task<string?> ExtractVisualSpecFromImageAsync(
        string imageDataUrl,
        string promptText,
        string userId,
        ResolvedAiConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        var cfg = config ?? await configResolver.ResolveAsync(userId, cancellationToken);
        var vision = cfg.Vision;
        if (string.IsNullOrEmpty(vision.ApiKey))
        {
            return null;
        }

        var defaults = AiProviders.Defaults.GetValueOrDefault(vision.Provider);

        try
        {
            return await ai.CallLlmAsync(new LlmCompletionRequest
            {
                UserId = cfg.UserId,
                KeySource = vision.KeySource,
                Plan = cfg.Plan,
                Provider = vision.Provider,
                ApiKey = vision.ApiKey,
                Model = vision.Model,
                FallbackModels = defaults?.FastFallbacks,
                Feature = "vision_extract",
                AgentName = "VisionExtractorAgent",
                ImageDataUrl = imageDataUrl,
                JsonMode = true,
                ThinkingMode = "disabled",
                MaxTokens = 900,
                TimeoutMs = 20_000,
                SystemPrompt = "You are an Academic IELTS Task 1 visual data analyst. Extract every objective detail from the uploaded chart, table, process diagram, or comparison map so a text-based IELTS examiner can verify a candidate's Task Achievement with 100% numeric and factual precision.",
                UserPrompt = $"Task prompt accompanying this image:\n{promptText}\n\nReturn a JSON object with keys:\n- \"visualType\": string (e.g., line graph, bar chart, pie charts, table, process diagram, map comparison)\n- \"unitsAndAxes\": string\n- \"macroOverviewTrends\": string[] (2-4 overarching trends, extremes, or transformations without drowning in minutiae)\n- \"exactFiguresAndStages\": string[] (comprehensive list of numbers, years, intersections, rankings, or process/map changes visible in the image)",
            }, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<SvgTransformResult> TransformImageToSvgAsync(
        string imageDataUrl,
        string? category,
        string? promptText,
        string userId,
        ResolvedAiConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        var cfg = config ?? await configResolver.ResolveAsync(userId, cancellationToken);
        var vision = cfg.Vision;
        if (string.IsNullOrEmpty(vision.ApiKey))
        {
            throw new InvalidOperationException("No AI provider API key is configured for vision.");
        }

        var defaults = AiProviders.Defaults.GetValueOrDefault(vision.Provider);

        var hintParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(category))
        {
            hintParts.Add($"Task 1 visual archetype hint: {category.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(promptText))
        {
            hintParts.Add($"Accompanying prompt instructions:\n{promptText.Trim()}");
        }

        var hintsBlock = hintParts.Count > 0 ? string.Join("\n\n", hintParts) + "\n\n" : string.Empty;

        var (parsed, _) = await ai.CallLlmAndParseAsync(
            new LlmCompletionRequest
            {
                UserId = cfg.UserId,
                KeySource = vision.KeySource,
                Plan = cfg.Plan,
                Provider = vision.Provider,
                ApiKey = vision.ApiKey,
                Model = vision.Model,
                FallbackModels = defaults?.FastFallbacks,
                Feature = "svg_transform",
                AgentName = "SvgTransformAgent",
                ImageDataUrl = imageDataUrl,
                JsonMode = true,
                ThinkingMode = "disabled",
                MaxTokens = 8192,
                TimeoutMs = 90_000,
                SystemPrompt =
                    """
                    You are a specialist Academic IELTS Task 1 technical illustrator and data analyst.
                    Your job is to redraw an uploaded photo or low-quality screenshot of an IELTS Academic Task 1 visual (line graph, bar chart, pie chart, data table, mixed chart, process/lifecycle diagram, or comparison map) into a publication-grade, crystal-clear, self-contained SVG graphic.

                    Visual fidelity rules (CRITICAL):
                    - Preserve EVERY title, subtitle, axis label, unit, tick mark, legend item, data value, bar height, line trajectory, pie percentage, table cell, process stage, arrow direction, compass/scale, and map landmark accurately from the source image. Do not invent or drop data.
                    - Correct perspective skew, camera shadows, glare, smudges, and low-resolution blur from phone photos so the result looks like a crisp digital exam paper.

                    SVG design rules:
                    - Root element MUST be `<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 960 600' width='960' height='600'>` (adjust aspect ratio cleanly if the source is wide or tall, keeping viewBox explicit and crisp).
                    - Draw a clean `#ffffff` rounded background `<rect>` covering the full viewBox.
                    - Use Escribot's clean Oxford editorial palette: primary ink `#0f172a`, secondary slate `#475569`, subtle borders/gridlines `#e2e8f0` / `#cbd5e1`, and series/accent fills `#16656e` (Oxford teal), `#c55e1b` (terracotta), `#2b7a52` (emerald), `#9a6b25` (ochre), `#9e3a2b` (crimson), `#334155` (slate).
                    - Use clean `system-ui, -apple-system, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif` typography with generous padding and spacing so NO labels overlap or clip outside the viewBox.
                    - Keep the SVG clean and concise: use simple geometric primitives, rounded coordinates, and shared CSS classes rather than verbose inline styles.
                    - Use single quotes (`'`) for XML attributes inside the SVG string so JSON stays compact and valid.
                    - NEVER include `<script>`, `<foreignObject>`, `<image>`, `<a>`, event handlers (`on*`), or external URLs. Use pure vector shapes, paths, markers, and `<text>`.
                    """,
                UserPrompt =
                    $$"""
                    {{hintsBlock}}Redraw the attached IELTS Task 1 image as a clean, publication-grade SVG and extract its objective visual data for IELTS Task Achievement grading.

                    Return a single JSON object with exactly two keys:
                    - "svg": string containing the complete, self-contained `<svg ...>...</svg>` markup.
                    - "visualSpec": object with:
                      - "visualType": string (e.g., "line graph", "bar chart", "pie charts", "table", "process diagram", "comparison map")
                      - "unitsAndAxes": string
                      - "macroOverviewTrends": string[] (2-4 overarching trends, extremes, or transformations)
                      - "exactFiguresAndStages": string[] (comprehensive list of exact numbers, years, rankings, or process/map stages visible in the source image)
                    """,
            },
            ParseSvgTransformResponse,
            cancellationToken);

        return parsed;
    }

    internal static SvgTransformResult ParseSvgTransformResponse(string rawContent)
    {
        string? rawSvg = null;
        string? visualSpecJson = null;

        try
        {
            var root = JsonExtractor.ExtractJsonObject<JsonElement>(rawContent);
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("svg", out var svgEl) && svgEl.ValueKind == JsonValueKind.String)
                {
                    rawSvg = svgEl.GetString();
                }

                if (root.TryGetProperty("visualSpec", out var specEl)
                    && specEl.ValueKind is JsonValueKind.Object or JsonValueKind.String)
                {
                    visualSpecJson = specEl.ValueKind == JsonValueKind.String
                        ? specEl.GetString()
                        : specEl.GetRawText();
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Fall back to direct <svg>...</svg> extraction if the model emitted raw SVG or unescaped quotes.
        }

        var sanitizedSvg = SvgSanitizer.Sanitize(rawSvg) ?? SvgSanitizer.ExtractAndSanitize(rawContent);
        if (string.IsNullOrEmpty(sanitizedSvg))
        {
            throw new InvalidOperationException("The model did not return a valid SVG.");
        }

        var svgDataUrl = $"data:{ImageStore.SvgContentType};base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(sanitizedSvg))}";
        var trimmedSpec = string.IsNullOrWhiteSpace(visualSpecJson) ? null : visualSpecJson.Trim();
        if (trimmedSpec?.Length > 20_000)
        {
            trimmedSpec = trimmedSpec[..20_000];
        }

        return new SvgTransformResult(svgDataUrl, trimmedSpec);
    }

    /* ============================================================================
     * 2. DEEP 4-CRITERION RUBRIC EVALUATION + INLINE ANNOTATIONS + BAND 8+ REWRITE
     * ============================================================================ */

    public async Task<EvaluateEssayResult> EvaluateEssayWithAiAsync(
        EvaluateEssayInput input,
        CancellationToken cancellationToken = default)
    {
        var cfg = input.Config ?? await configResolver.ResolveAsync(input.UserId, cancellationToken);
        var wordCount = Ielts.CountWords(input.EssayText);
        var minWords = Ielts.MinimumWords(input.TaskType);

        var grader = cfg.Grader;
        var fast = cfg.Fast;
        if (string.IsNullOrEmpty(grader.ApiKey) || string.IsNullOrEmpty(fast.ApiKey))
        {
            input.OnProgress?.Invoke("Running local diagnostic analysis...");
            return new EvaluateEssayResult(
                BuildHeuristicDiagnosticPreview(input, cfg, wordCount, minWords),
                "local-diagnostic-preview",
                "heuristic-analyzer-v1",
                null);
        }

        var graderDefaults = AiProviders.Defaults.GetValueOrDefault(grader.Provider);
        var fastDefaults = AiProviders.Defaults.GetValueOrDefault(fast.Provider);
        var l1Profile = cfg.L1CoachingEnabled ? L1Profiles.Get(cfg.NativeLanguage) : L1Profiles.Get("neutral");

        var criterion1Name = input.TaskType == Ielts.Task1
            ? "Task Achievement (TA): clear NUMBER-FREE macro Overview paragraph (mandatory for Band 7+; 2–3 overarching trends/crossovers WITHOUT raw percentages or dates), accurate data selection & exact comparative figures inside Body Paragraphs 1 & 2, no personal opinion/speculation, 150+ words."
            : "Task Response (TR): addresses all parts of the prompt fully, maintains a clear & consistent position throughout, extends main ideas with specific reasoning/examples rather than over-generalising, 250+ words.";

        var targetRewriteBand = Math.Max(cfg.TargetBand, 8.0).ToString("F1", CultureInfo.InvariantCulture);
        var sharedPedagogyRules = GetSharedPedagogyRules(input.TaskType);

        var sessionCoachingContextParts = new List<string>();
        if (input.OutlineFeedback is not null)
        {
            sessionCoachingContextParts.Add(
                $"Prior Outline Check given to student:\n- Overview/Thesis guidance: {input.OutlineFeedback.OverviewOrThesisCheck}\n- Grouping guidance: {input.OutlineFeedback.BodyGroupingCheck}");
        }

        if (input.CoachingHistory is { Count: > 0 })
        {
            var recentTools = string.Join("\n", input.CoachingHistory.TakeLast(3).Where(t => t is not null).Select(t =>
                $"- [{t.Tool}] {t.Headline}: {string.Join(" | ", (t.Suggestions ?? []).Where(s => s is not null).Select(s => $"\"{s.RewrittenOrPhrase}\""))}"));
            sessionCoachingContextParts.Add(
                $"Prior On-Demand Toolkit suggestions shown to student during drafting (DO NOT contradict or penalise phrasing recommended here):\n{recentTools}");
        }

        var sessionCoachingBlock = sessionCoachingContextParts.Count > 0
            ? $"\nSESSION COACHING CONTEXT (maintain 100% consistency with this prior guidance):\n{string.Join("\n\n", sessionCoachingContextParts)}\n"
            : string.Empty;

        var previousBandText = input.PreviousBand is { } prev
            ? $" (Previous draft scored Band {prev.ToString("G", CultureInfo.InvariantCulture)})"
            : string.Empty;

        var visualBlock = !string.IsNullOrEmpty(input.VisualContext)
            ? $"UNDERLYING VISUAL DATA / DIAGRAM GROUND TRUTH (verify candidate's body-paragraph figures & macro overview trends against this):\n{input.VisualContext}\n"
            : string.Empty;

        var sharedEssayContext = $"TASK TYPE: Academic IELTS {(input.TaskType == Ielts.Task1 ? "Writing Task 1 (Report)" : "Writing Task 2 (Essay)")}\nDRAFT VERSION: v{input.DraftVersion}{previousBandText}\nWORD COUNT: {wordCount} words (Minimum required: {minWords})\n\nPROMPT TITLE: {input.PromptTitle}\nPROMPT INSTRUCTIONS:\n{input.PromptText}\n\n{visualBlock}{sessionCoachingBlock}CANDIDATE ESSAY:\n\"\"\"\n{input.EssayText}\n\"\"\"";

        var rubricSystemPrompt = $"You are a Principal Academic IELTS Writing Examiner calibrated for Band {cfg.TargetBand.ToString("F1", CultureInfo.InvariantCulture)}+.\nGrade strictly according to official IELTS Academic Writing Band Descriptors (0.0 to 9.0 in 0.5 increments; do NOT inflate scores—Band 6.5 plateaus are common when cohesion is mechanical, Task 1 lacks a synthesised overview, or Task 2 ideas remain over-generalised).\n\nCandidate Calibration:\n- Current Baseline Band: {cfg.CurrentBand.ToString("F1", CultureInfo.InvariantCulture)} | Target Band: {cfg.TargetBand.ToString("F1", CultureInfo.InvariantCulture)}\n- Linguistic Lens ({l1Profile.Label}): {l1Profile.CoachingFocus}\n\nOfficial 4 Criteria:\n1. TA_TR — {criterion1Name}\n2. CC — Coherence and Cohesion: logical paragraphing, clear central topic per body paragraph, natural cohesive devices WITHOUT mechanical sentence-initial linkers (\"Firstly, Secondly, Moreover, In a nutshell\"). Note: comma splices belong under GRA, not CC.\n3. LR — Lexical Resource: natural C1/C2 academic collocations, precise statistical/academic register, effective hedging, accurate word formation & spelling.\n4. GRA — Grammatical Range and Accuracy: flexible mix of complex subordinate, relative, participial, and conditional structures with high punctuation & clause-boundary accuracy (comma splices are scored here under GRA).\n\n{sharedPedagogyRules}\n\nKeep each string concise and high-signal so JSON generation is rapid.";

        var rubricUserPrompt = $"{sharedEssayContext}\n\nReturn ONLY a valid JSON object matching this exact structure:\n{{\n  \"taTrBand\": 6.5,\n  \"ccBand\": 6.5,\n  \"lrBand\": 7.0,\n  \"graBand\": 6.5,\n  \"examinerSummary\": \"2-3 sentence candid synthesis of what held this draft back and its strongest asset.\",\n  \"criteria\": {{\n    \"TA_TR\": {{\n      \"band\": 6.5,\n      \"descriptorSummary\": \"1-2 sentences on why this band applies under official descriptors.\",\n      \"strengths\": [\"1-2 concise strengths\"],\n      \"limitingFactors\": [\"1-2 concise limiting factors\"],\n      \"howToGainHalfBand\": \"1 concrete revision step to reach +0.5/+1.0 (for Task 1: keep Overview number-free; put exact figures in Body 1 & 2).\"\n    }},\n    \"CC\": {{ \"band\": 6.5, \"descriptorSummary\": \"...\", \"strengths\": [\"...\"], \"limitingFactors\": [\"...\"], \"howToGainHalfBand\": \"...\" }},\n    \"LR\": {{ \"band\": 7.0, \"descriptorSummary\": \"...\", \"strengths\": [\"...\"], \"limitingFactors\": [\"...\"], \"howToGainHalfBand\": \"...\" }},\n    \"GRA\": {{ \"band\": 6.5, \"descriptorSummary\": \"...\", \"strengths\": [\"...\"], \"limitingFactors\": [\"...\"], \"howToGainHalfBand\": \"...\" }}\n  }},\n  \"topPriorities\": [\"Top 1 highest-leverage fix for v{input.DraftVersion + 1}\", \"Top 2...\", \"Top 3...\"]\n}}";

        var l1Clause = cfg.L1CoachingEnabled && cfg.NativeLanguage != "neutral" ? ", and L1" : string.Empty;
        var coachSystemPrompt = $"You are a Band {targetRewriteBand} Academic IELTS Prose Coach & Marginalia Editor.\nLinguistic Lens ({l1Profile.Label}): {l1Profile.CoachingFocus}\n\n{sharedPedagogyRules}\n\nCRITICAL RULES FOR JSON OUTPUT:\n- Each \"inlineAnnotations\" item MUST set \"originalSpan\" to an EXACT verbatim substring copied character-for-character from the candidate's essay (15 to 140 characters) so it highlights cleanly in the UI. Include 3 to 7 genuinely high-impact annotations spanning TA_TR, CC, LR, GRA{l1Clause} (do NOT pad with lateral synonym swaps like \"whereas\" -> \"while\").\n- Ensure every \"replacement\" is a complete, grammatically sound drop-in replacement for \"originalSpan\" that reads naturally in the surrounding sentence (never leave false cognates like \"actually\" in place or create double intensifiers).\n- \"targetBandRewrite\" MUST rewrite the candidate's OWN arguments/data groupings elevated to natural Band {targetRewriteBand} academic prose (multi-paragraph essay separated by \"\\n\\n\"; for Task 1, ensure the Overview paragraph has NO raw numbers and Body 1 & 2 contain the exact figures).\n- \"vaultItems\" MUST extract 3 to 4 reusable items (recurring error fixes and high-utility academic collocations) for spaced review.";

        var coachUserPrompt = $"{sharedEssayContext}\n\nReturn ONLY a valid JSON object matching this exact structure:\n{{\n  \"inlineAnnotations\": [\n    {{\n      \"id\": \"ann-1\",\n      \"category\": \"GRA\",\n      \"severity\": \"error\",\n      \"originalSpan\": \"exact substring from candidate essay\",\n      \"replacement\": \"natural Band 8+ revision of that exact span\",\n      \"explanation\": \"Concise 1-sentence examiner note explaining why.\"\n    }}\n  ],\n  \"targetBandRewrite\": \"Full multi-paragraph rewrite of the candidate's own essay at Band {targetRewriteBand}...\",\n  \"rewriteKeyChanges\": [\"3-4 concise bullet takeaways comparing the rewrite to the candidate's draft\"],\n  \"vaultItems\": [\n    {{\n      \"itemType\": \"error\",\n      \"criterion\": \"L1\",\n      \"originalText\": \"...\",\n      \"upgradedText\": \"...\",\n      \"explanation\": \"...\",\n      \"exampleSentence\": \"...\"\n    }}\n  ]\n}}";

        input.OnProgress?.Invoke("Scoring 4 IELTS criteria & generating Band 8+ rewrite...");

        async Task<(RubricScoreOutput Data, double? Cost, string Provider, string Model)> RunScoreAsync()
        {
            // A fresh scanner per attempt, so the fallback reports milestones the failed stream already passed.
            JsonMilestoneScanner NewRubricScanner() => new(
                input.OnProgress,
                [
                    ("\"criteria\"", "Scoring 4 IELTS criteria..."),
                    ("\"topPriorities\"", "Prioritising revision steps..."),
                ]);

            try
            {
                var (parsedRubric, resRubric) = await ai.StreamAgentAndParseAsync(
                    new LlmCompletionRequest
                    {
                        UserId = cfg.UserId,
                        KeySource = grader.KeySource,
                        Plan = cfg.Plan,
                        Provider = grader.Provider,
                        ApiKey = grader.ApiKey,
                        Model = grader.Model,
                        FallbackModels = graderDefaults?.GraderFallbacks,
                        Feature = "rubric_score",
                        AgentName = "ExaminerRubricAgent",
                        SystemPrompt = rubricSystemPrompt,
                        UserPrompt = rubricUserPrompt,
                        JsonMode = true,
                        ThinkingMode = "disabled",
                        MaxTokens = 1200,
                        TimeoutMs = 35_000,
                    },
                    JsonExtractor.ExtractJsonObject<RubricScoreOutput>,
                    NewRubricScanner().Append,
                    cancellationToken);

                input.OnProgress?.Invoke("4-criterion band scores ready — finalising inline upgrades...");
                var actualModel = !string.IsNullOrEmpty(resRubric.ModelUsed) ? resRubric.ModelUsed : grader.Model;
                return (parsedRubric, resRubric.CostUsd, grader.Provider, actualModel);
            }
            catch when ((fast.Provider, fast.Model) != (grader.Provider, grader.Model) && !cancellationToken.IsCancellationRequested)
            {
                input.OnProgress?.Invoke("Switching to fast examiner model to complete band scoring...");
                var (parsedFallback, resFallback) = await ai.StreamAgentAndParseAsync(
                    new LlmCompletionRequest
                    {
                        UserId = cfg.UserId,
                        KeySource = fast.KeySource,
                        Plan = cfg.Plan,
                        Provider = fast.Provider,
                        ApiKey = fast.ApiKey,
                        Model = fast.Model,
                        FallbackModels = fastDefaults?.FastFallbacks,
                        Feature = "rubric_score_fallback",
                        AgentName = "ExaminerRubricAgent",
                        SystemPrompt = rubricSystemPrompt,
                        UserPrompt = rubricUserPrompt,
                        JsonMode = true,
                        ThinkingMode = "disabled",
                        MaxTokens = 1200,
                        TimeoutMs = 25_000,
                    },
                    JsonExtractor.ExtractJsonObject<RubricScoreOutput>,
                    NewRubricScanner().Append,
                    cancellationToken);

                var actualModel = !string.IsNullOrEmpty(resFallback.ModelUsed) ? resFallback.ModelUsed : fast.Model;
                input.OnProgress?.Invoke("4-criterion band scores ready — finalising inline upgrades...");
                return (parsedFallback, resFallback.CostUsd, fast.Provider, actualModel);
            }
        }

        async Task<(AnnotationRewriteOutput Data, double? Cost)> RunAnnotationsAsync()
        {
            var coachScanner = new JsonMilestoneScanner(
                input.OnProgress,
                [
                    ("\"inlineAnnotations\"", "Annotating essay spans..."),
                    ("\"targetBandRewrite\"", "Drafting Band 8.0+ rewrite..."),
                    ("\"vaultItems\"", "Extracting Personal Vault items..."),
                ]);

            try
            {
                var (parsedCoach, resCoach) = await ai.StreamAgentAndParseAsync(
                    new LlmCompletionRequest
                    {
                        UserId = cfg.UserId,
                        KeySource = fast.KeySource,
                        Plan = cfg.Plan,
                        Provider = fast.Provider,
                        ApiKey = fast.ApiKey,
                        Model = fast.Model,
                        FallbackModels = fastDefaults?.FastFallbacks,
                        Feature = "annotations_rewrite",
                        AgentName = "ExaminerCoachAgent",
                        SystemPrompt = coachSystemPrompt,
                        UserPrompt = coachUserPrompt,
                        JsonMode = true,
                        ThinkingMode = "disabled",
                        MaxTokens = 2100,
                        TimeoutMs = 40_000,
                    },
                    JsonExtractor.ExtractJsonObject<AnnotationRewriteOutput>,
                    coachScanner.Append,
                    cancellationToken);

                input.OnProgress?.Invoke("Inline annotations & Band 8+ rewrite ready...");
                return (parsedCoach, resCoach.CostUsd);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                var localPreview = BuildHeuristicDiagnosticPreview(input, cfg, wordCount, minWords);
                return (new AnnotationRewriteOutput
                {
                    InlineAnnotations = localPreview.InlineAnnotations,
                    TargetBandRewrite = "Band 8.0+ full rewrite timed out on this run, while your official 4-criterion band scores succeeded above. You can redraft or re-run anytime.",
                    RewriteKeyChanges = [],
                    VaultItems = localPreview.VaultItems,
                }, null);
            }
        }

        var scoreTask = RunScoreAsync();
        var annotationsTask = RunAnnotationsAsync();
        await Task.WhenAll(scoreTask, annotationsTask);

        var (rubricData, rubricCost, providerUsed, modelUsed) = scoreTask.Result;
        var (coachData, coachCost) = annotationsTask.Result;

        double? totalCost = rubricCost is not null || coachCost is not null
            ? Math.Round((rubricCost ?? 0) + (coachCost ?? 0), 6, MidpointRounding.AwayFromZero)
            : null;

        var taTrBand = Ielts.RoundBand(rubricData.TaTrBand ?? rubricData.Criteria?.TaTr?.Band ?? 6.5);
        var ccBand = Ielts.RoundBand(rubricData.CcBand ?? rubricData.Criteria?.Cc?.Band ?? 6.5);
        var lrBand = Ielts.RoundBand(rubricData.LrBand ?? rubricData.Criteria?.Lr?.Band ?? 6.5);
        var graBand = Ielts.RoundBand(rubricData.GraBand ?? rubricData.Criteria?.Gra?.Band ?? 6.5);
        var overallBand = Ielts.RoundBand((taTrBand + ccBand + lrBand + graBand) / 4.0);

        var normalizedAnnotations = NormalizeAndFilterAnnotations(coachData.InlineAnnotations ?? []);

        var rawCriteria = new EvaluationCriteria
        {
            TaTr = new CriterionFeedback
            {
                Band = taTrBand,
                DescriptorSummary = rubricData.Criteria?.TaTr?.DescriptorSummary ?? "Assessed for task fulfilment.",
                Strengths = rubricData.Criteria?.TaTr?.Strengths ?? [],
                LimitingFactors = rubricData.Criteria?.TaTr?.LimitingFactors ?? [],
                HowToGainHalfBand = rubricData.Criteria?.TaTr?.HowToGainHalfBand
                    ?? (input.TaskType == Ielts.Task1
                        ? "Keep your Overview number-free and support both body paragraphs with exact comparative figures."
                        : "Extend core points with sharper precision and concrete examples."),
            },
            Cc = new CriterionFeedback
            {
                Band = ccBand,
                DescriptorSummary = rubricData.Criteria?.Cc?.DescriptorSummary ?? "Assessed for logical flow and cohesion.",
                Strengths = rubricData.Criteria?.Cc?.Strengths ?? [],
                LimitingFactors = rubricData.Criteria?.Cc?.LimitingFactors ?? [],
                HowToGainHalfBand = rubricData.Criteria?.Cc?.HowToGainHalfBand ?? "Replace mechanical linkers with demonstrative referencing.",
            },
            Lr = new CriterionFeedback
            {
                Band = lrBand,
                DescriptorSummary = rubricData.Criteria?.Lr?.DescriptorSummary ?? "Assessed for academic lexical precision.",
                Strengths = rubricData.Criteria?.Lr?.Strengths ?? [],
                LimitingFactors = rubricData.Criteria?.Lr?.LimitingFactors ?? [],
                HowToGainHalfBand = rubricData.Criteria?.Lr?.HowToGainHalfBand ?? "Use precise domain collocations and nuanced hedging.",
            },
            Gra = new CriterionFeedback
            {
                Band = graBand,
                DescriptorSummary = rubricData.Criteria?.Gra?.DescriptorSummary ?? "Assessed for syntactic range and accuracy.",
                Strengths = rubricData.Criteria?.Gra?.Strengths ?? [],
                LimitingFactors = rubricData.Criteria?.Gra?.LimitingFactors ?? [],
                HowToGainHalfBand = rubricData.Criteria?.Gra?.HowToGainHalfBand ?? "Vary subordinate clauses and eliminate punctuation splices.",
            },
        };

        var sanitizedCriteria = SanitizeCriteriaFeedback(rawCriteria, input.TaskType, input.EssayText);

        var defaultPriorities = new List<string>
        {
            "Strengthen macro overview / thesis precision",
            "Replace mechanical sequence connectors with cohesive referencing",
            "Upgrade high-frequency verbs into formal academic collocations",
        };
        var missingSubjectInEssay = HasLikelyMissingSubjectInEssay(input.EssayText);
        var sanitizedTopPriorities = (rubricData.TopPriorities ?? defaultPriorities).Select(p =>
        {
            if (input.TaskType == Ielts.Task1
                && OverviewRegex().IsMatch(p)
                && FigureSignalRegex().IsMatch(p)
                && !NegationSignalRegex().IsMatch(p))
            {
                return "Keep the Overview strictly number-free and place exact comparative figures in Body Paragraphs 1 & 2";
            }

            if (ActuallyRegex().IsMatch(p) && ExtremelyRegex().IsMatch(p))
            {
                return "Replace L1 false cognates ('actually' → 'currently / in recent years') and informal intensifiers ('very fast' → 'rapidly')";
            }

            if (!missingSubjectInEssay && MissingSubjectRegex().IsMatch(p))
            {
                return "Eliminate comma splices between independent clauses and vary subordinate clause structures";
            }

            return CleanDoubleIntensifiers(p);
        }).ToList();

        var vaultItems = (coachData.VaultItems ?? [])
            .Where(v => v is not null && !IsTrivialLateralSwap(v.OriginalText ?? string.Empty, v.UpgradedText ?? string.Empty, v.Explanation ?? string.Empty))
            .Select(v =>
            {
                var explanation = v.Explanation ?? string.Empty;
                var criterion = ValidCategories.Contains(v.Criterion) ? v.Criterion : "LR";
                if (CommaSpliceRegex().IsMatch(explanation))
                {
                    criterion = "GRA";
                }
                else if (FalseCognateRegex().IsMatch(explanation))
                {
                    criterion = "L1";
                }

                return new EvaluationVaultItem
                {
                    ItemType = v.ItemType == "collocation" ? "collocation" : "error",
                    Criterion = criterion,
                    OriginalText = v.OriginalText ?? string.Empty,
                    UpgradedText = CleanDoubleIntensifiers(v.UpgradedText ?? string.Empty),
                    Explanation = explanation,
                    ExampleSentence = CleanDoubleIntensifiers(v.ExampleSentence ?? string.Empty),
                };
            })
            .ToList();

        var payload = new EvaluationPayload
        {
            OverallBand = overallBand,
            TaTrBand = taTrBand,
            CcBand = ccBand,
            LrBand = lrBand,
            GraBand = graBand,
            ExaminerSummary = !string.IsNullOrEmpty(rubricData.ExaminerSummary)
                ? rubricData.ExaminerSummary
                : $"Scored Band {overallBand.ToString("F1", CultureInfo.InvariantCulture)} overall across the four official Academic IELTS criteria.",
            Criteria = sanitizedCriteria,
            InlineAnnotations = normalizedAnnotations,
            TargetBandRewrite = CleanDoubleIntensifiers(coachData.TargetBandRewrite ?? "Target-band rewrite unavailable for this response."),
            RewriteKeyChanges = (coachData.RewriteKeyChanges ?? []).Select(CleanDoubleIntensifiers).ToList(),
            TopPriorities = sanitizedTopPriorities,
            VaultItems = vaultItems,
        };

        return new EvaluateEssayResult(payload, providerUsed, modelUsed, totalCost);
    }

    /* ============================================================================
     * 3. FAST <2s COACH: 5-MIN OUTLINE CHECK & ON-DEMAND ACADEMIC TOOLKIT
     * ============================================================================ */

    public async Task<OutlineFeedback> CheckOutlineWithAiAsync(
        string userId,
        string taskType,
        string promptText,
        string? visualContext,
        OutlineInput outline,
        ResolvedAiConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        var cfg = config ?? await configResolver.ResolveAsync(userId, cancellationToken);
        var thesis = outline.ThesisOrOverview ?? string.Empty;
        var body1 = (outline.Body1Plan ?? string.Empty).Trim();
        var body2 = (outline.Body2Plan ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(cfg.Fast.ApiKey))
        {
            var hasOverviewOrThesis = Ielts.CountWords(thesis) >= 8;
            var hasBodies = body1.Length > 12 && body2.Length > 12;
            return new OutlineFeedback
            {
                Readiness = hasOverviewOrThesis && hasBodies ? "strong" : "needs_refinement",
                EstimatedTaskBandCeiling = hasOverviewOrThesis && hasBodies ? 7.5 : 6.0,
                VerdictSummary = hasOverviewOrThesis && hasBodies
                    ? "Solid structural skeleton (Local Preview — add your DeepSeek API key in Settings for live prompt-specific validation)."
                    : "Your plan is still too brief—spell out your exact macro overview/thesis and concrete evidence for both body paragraphs before starting the timer.",
                OverviewOrThesisCheck = taskType == Ielts.Task1
                    ? "Ensure your Overview identifies 2–3 overarching trends or extremes WITHOUT quoting raw statistics (save exact numbers for Body 1 & Body 2)."
                    : "Ensure your thesis states a nuanced, unambiguous position that directly answers every part of the prompt question.",
                BodyGroupingCheck = taskType == Ielts.Task1
                    ? "Group items by shared trajectories or contrast (e.g., rising vs. declining categories) rather than describing one line/row per paragraph mechanically."
                    : "Give each body paragraph ONE central claim backed by a causal mechanism and a specific real-world example.",
                ActionableTweaks =
                [
                    hasOverviewOrThesis
                        ? "Avoid repeating exact prompt wording in your opening paraphrase—use structural conversion (active ↔ passive or clause nominalisation)."
                        : "Expand your Thesis/Overview box to at least 1–2 complete thoughts.",
                    "Pre-select 3–4 domain collocations now so your cognitive load during drafting goes into syntax accuracy.",
                ],
                SuggestedAcademicCollocations = taskType == Ielts.Task1
                    ?
                    [
                        new() { Phrase = "witnessed a pronounced upward trajectory", UsageNote = "Stronger than 'went up a lot' for sustained multi-year growth." },
                        new() { Phrase = "declined precipitously / fell sharply", UsageNote = "Objective statistical phrasing for steep drops (prefer over journalistic metaphors like 'collapsed')." },
                        new() { Phrase = "converged before overtaking", UsageNote = "Precise phrasing for intersecting line-graph series." },
                    ]
                    :
                    [
                        new() { Phrase = "yields tangible long-term dividends", UsageNote = "High-register alternative to 'brings good results in the future'." },
                        new() { Phrase = "disproportionately burdens lower-income households", UsageNote = "Nuanced economic equity phrasing for policy debates." },
                        new() { Phrase = "mitigate unintended externalities", UsageNote = "Precise C1/C2 academic collocation for Problem/Solution & Policy essays." },
                    ],
            };
        }

        var defaults = AiProviders.Defaults.GetValueOrDefault(cfg.Fast.Provider);
        var visualBlock = !string.IsNullOrEmpty(visualContext) ? $"\nVISUAL DATA SUMMARY:\n{visualContext}\n" : string.Empty;
        var termsLine = !string.IsNullOrEmpty(outline.TargetKeyTerms) ? $"- Planned Vocabulary: {outline.TargetKeyTerms}" : string.Empty;

        var raw = await ai.CallLlmAsync(new LlmCompletionRequest
        {
            UserId = cfg.UserId,
            KeySource = cfg.Fast.KeySource,
            Plan = cfg.Plan,
            Provider = cfg.Fast.Provider,
            ApiKey = cfg.Fast.ApiKey,
            Model = cfg.Fast.Model,
            FallbackModels = defaults?.FastFallbacks,
            Feature = "outline_check",
            AgentName = "OutlineCoachAgent",
            JsonMode = true,
            ThinkingMode = "disabled",
            TimeoutMs = 35_000,
            SystemPrompt = $"You are a fast, surgical Academic IELTS Band {cfg.TargetBand.ToString("F1", CultureInfo.InvariantCulture)}+ Writing Coach. Validate the student's 5-minute pre-writing outline BEFORE they write the essay. Do NOT write the essay for them—spot Task Achievement/Response traps, weak grouping, or missing prompt parts, and suggest 4 high-register academic collocations.\n\n{GetSharedPedagogyRules(taskType)}",
            UserPrompt = $"TASK: Academic IELTS {taskType.ToUpperInvariant()}\nPROMPT:\n{promptText}\n{visualBlock}\nSTUDENT OUTLINE:\n- {(taskType == Ielts.Task1 ? "Macro Overview" : "Thesis / Position")}: {outline.ThesisOrOverview}\n- Body Paragraph 1 Plan: {outline.Body1Plan}\n- Body Paragraph 2 Plan: {outline.Body2Plan}\n{termsLine}\n\nReturn JSON:\n{{\n  \"readiness\": \"strong\" | \"needs_refinement\" | \"off_track\",\n  \"estimatedTaskBandCeiling\": 7.5,\n  \"verdictSummary\": \"1-2 crisp sentences on whether they are ready to draft.\",\n  \"overviewOrThesisCheck\": \"Specific critique of their Overview (Task 1: strictly NO raw numbers/percentages in Overview; captures main trend + extreme; save exact figures for Body 1 & 2) or Thesis (Task 2: clear position?).\",\n  \"bodyGroupingCheck\": \"Critique of how logically Body 1 & Body 2 divide the data (with exact figures reserved for Body 1 & 2) or arguments.\",\n  \"actionableTweaks\": [\"2-3 quick bullet adjustments before typing\"],\n  \"suggestedAcademicCollocations\": [\n    {{ \"phrase\": \"...\", \"usageNote\": \"...\" }}\n  ]\n}}",
        }, cancellationToken);

        return JsonExtractor.ExtractJsonObject<OutlineFeedback>(raw);
    }

    public async Task<ToolkitResponse> RunToolkitWithAiAsync(
        string userId,
        ToolkitRequest req,
        ResolvedAiConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        var cfg = config ?? await configResolver.ResolveAsync(userId, cancellationToken);
        var l1Profile = cfg.L1CoachingEnabled ? L1Profiles.Get(cfg.NativeLanguage) : L1Profiles.Get("neutral");

        if (string.IsNullOrEmpty(cfg.Fast.ApiKey))
        {
            return new ToolkitResponse
            {
                Tool = req.Tool,
                Headline = req.Tool switch
                {
                    "collocations" => "Academic Register & Hedging Upgrades (Preview)",
                    "l1_check" => $"{l1Profile.Label} Transfer Scan (Preview)",
                    _ => "Paragraph Coherence & Topic-Sentence Check (Preview)",
                },
                Analysis = "Add your DeepSeek API key in Settings to unlock live context-aware rewrites of your exact selection. Meanwhile, verify that your sentence avoids sweeping absolutes ('always', 'everyone') and links back to the prompt's core claim without mechanical connectors.",
                Suggestions =
                [
                    new()
                    {
                        Label = "Nominalised Academic Opening",
                        RewrittenOrPhrase = "This structural shift underscores the need for targeted regulatory intervention...",
                        Rationale = "Turns a conversational clause into compact Band 8.0 academic prose.",
                    },
                    new()
                    {
                        Label = "Nuanced C1 Hedging",
                        RewrittenOrPhrase = "tends to exacerbate... / is widely associated with... / risks undermining...",
                        Rationale = "Replaces over-confident Band 6 claims ('will cause / destroys').",
                    },
                ],
            };
        }

        var toolInstructions = req.Tool switch
        {
            "collocations" => $"Upgrade the selected phrase/sentence into 3 distinct, natural Band {cfg.TargetBand.ToString("F1", CultureInfo.InvariantCulture)}+ academic phrasings (one with C1/C2 collocations, one with nuanced academic hedging, one with compact nominalisation). Do NOT write new paragraphs—only refine the selected idea. Never use journalistic metaphors like \"collapsed\" or \"skyrocketed\" (use objective statistical verbs like \"declined precipitously\", \"fell sharply\", \"expanded rapidly\"), and never suggest lateral swaps like \"whereas\" ↔ \"while\".",
            "l1_check" => $"Inspect the selected text through the lens of {l1Profile.Label} ({l1Profile.CoachingFocus}). Flag any false cognates (such as \"actually\" used for \"actualmente\" = \"currently / in recent years\" — replace the whole phrase naturally, never keep \"actually\" or swap it with \"extremely\"), comma splices (note that comma splices are GRA punctuation errors), article/preposition calques, or unnatural clause length, and provide 2-3 clean native academic alternatives.",
            _ => $"Evaluate this single paragraph for IELTS Coherence & Cohesion, Task {(req.TaskType == Ielts.Task1 ? "Achievement" : "Response")}, and Academic Register: Does it have a clear topic sentence or overview signpost? (If it is a Task 1 Overview paragraph, ensure it contains NO raw numbers/percentages; if it is a Body paragraph, ensure it groups and compares specific figures.) Are ideas extended logically without mechanical linkers or journalistic verbs like \"collapsed\"? Give concise diagnosis + 2 surgical improvements that align strictly with our examiner grading standards (do not rewrite the entire essay).",
        };

        var defaults = AiProviders.Defaults.GetValueOrDefault(cfg.Fast.Provider);
        var raw = await ai.CallLlmAsync(new LlmCompletionRequest
        {
            UserId = cfg.UserId,
            KeySource = cfg.Fast.KeySource,
            Plan = cfg.Plan,
            Provider = cfg.Fast.Provider,
            ApiKey = cfg.Fast.ApiKey,
            Model = cfg.Fast.Model,
            FallbackModels = defaults?.FastFallbacks,
            Feature = "toolkit",
            AgentName = "ToolkitAgent",
            JsonMode = true,
            ThinkingMode = "disabled",
            TimeoutMs = 35_000,
            SystemPrompt = $"You are an on-demand Academic IELTS writing coach. Be crisp, encouraging, and surgical. Ensure 100% alignment with the final IELTS Examiner rubric so you never suggest phrasing that the grader would penalise.\n\n{GetSharedPedagogyRules(req.TaskType)}",
            UserPrompt = $"TASK: {req.TaskType.ToUpperInvariant()}\nPROMPT: {req.PromptText}\nSELECTED TEXT TO COACH:\n\"\"\"\n{req.SelectedText}\n\"\"\"\n\nINSTRUCTION: {toolInstructions}\n\nReturn JSON:\n{{\n  \"tool\": \"{req.Tool}\",\n  \"headline\": \"Short diagnostic title\",\n  \"analysis\": \"2-3 sentence explanation\",\n  \"suggestions\": [\n    {{ \"label\": \"Option 1 label\", \"rewrittenOrPhrase\": \"...\", \"rationale\": \"Why this scores higher\" }}\n  ]\n}}",
        }, cancellationToken);

        return JsonExtractor.ExtractJsonObject<ToolkitResponse>(raw);
    }

    /* ============================================================================
     * 4. ON-DEMAND AI PROMPT & STRUCTURED VISUAL GENERATOR
     * ============================================================================ */

    public async Task<GeneratedPromptResult> GeneratePromptWithAiAsync(
        string userId,
        string taskType,
        string category,
        string? topic,
        ResolvedAiConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        var cfg = config ?? await configResolver.ResolveAsync(userId, cancellationToken);
        var chosenTopic = string.IsNullOrWhiteSpace(topic) ? "Public Policy, Technology & Sustainability" : topic.Trim();

        if (string.IsNullOrEmpty(cfg.Fast.ApiKey))
        {
            if (taskType == Ielts.Task2)
            {
                return new GeneratedPromptResult(
                    Title: $"Public Investment in {chosenTopic}",
                    Topic: chosenTopic,
                    PromptText: $"In many countries, governments face growing pressure to prioritise immediate economic growth over long-term investments in {chosenTopic.ToLowerInvariant()}.\n\nTo what extent do you agree or disagree that long-term sustainability should take precedence over short-term economic gains?\n\nGive reasons for your answer and include any relevant examples from your own knowledge or experience. Write at least 250 words.",
                    ChartSpec: null);
            }

            return new GeneratedPromptResult(
                Title: $"Regional Adoption Trends in {chosenTopic} (2005–2025)",
                Topic: chosenTopic,
                PromptText: $"The line graph below compares adoption rates across three regions in {chosenTopic.ToLowerInvariant()} between 2005 and 2025.\n\nSummarise the information by selecting and reporting the main features, and make comparisons where relevant.\n\nWrite at least 150 words.",
                ChartSpec: new JsonObject
                {
                    ["kind"] = "line",
                    ["title"] = "Adoption Index Across Three Regions (2005–2025)",
                    ["subtitle"] = "Percentage of surveyed institutions (%)",
                    ["xAxisKey"] = "year",
                    ["yAxisUnit"] = "%",
                    ["series"] = new JsonArray(
                        new JsonObject { ["key"] = "nordic", ["label"] = "Northern Europe", ["color"] = "#16656e" },
                        new JsonObject { ["key"] = "eastAsia", ["label"] = "East Asia", ["color"] = "#c55e1b" },
                        new JsonObject { ["key"] = "latam", ["label"] = "Latin America", ["color"] = "#2b7a52" }),
                    ["data"] = new JsonArray(
                        new JsonObject { ["year"] = "2005", ["nordic"] = 28, ["eastAsia"] = 19, ["latam"] = 11 },
                        new JsonObject { ["year"] = "2010", ["nordic"] = 39, ["eastAsia"] = 34, ["latam"] = 16 },
                        new JsonObject { ["year"] = "2015", ["nordic"] = 51, ["eastAsia"] = 56, ["latam"] = 24 },
                        new JsonObject { ["year"] = "2020", ["nordic"] = 64, ["eastAsia"] = 72, ["latam"] = 37 },
                        new JsonObject { ["year"] = "2025", ["nordic"] = 74, ["eastAsia"] = 83, ["latam"] = 49 }),
                    ["sourceNote"] = "Generated Practice Dataset (Connect DeepSeek API key for infinite custom charts)",
                });
        }

        var chartSchemaHint = taskType == Ielts.Task1
            ? GetChartSchemaPromptForCategory(category)
            : "Omit chartSpec (set to null) because Task 2 is a discursive essay.";

        var defaults = AiProviders.Defaults.GetValueOrDefault(cfg.Fast.Provider);
        var raw = await ai.CallLlmAsync(new LlmCompletionRequest
        {
            UserId = cfg.UserId,
            KeySource = cfg.Fast.KeySource,
            Plan = cfg.Plan,
            Provider = cfg.Fast.Provider,
            ApiKey = cfg.Fast.ApiKey,
            Model = cfg.Fast.Model,
            FallbackModels = defaults?.FastFallbacks,
            Feature = "prompt_generation",
            AgentName = "PromptGeneratorAgent",
            JsonMode = true,
            ThinkingMode = "disabled",
            MaxTokens = 1600,
            TimeoutMs = 38_000,
            SystemPrompt = "You are an official Cambridge Academic IELTS test-paper designer. Create realistic, nuanced Academic IELTS Writing tasks with rich contrasts, crossovers, or thought-provoking academic tensions.",
            UserPrompt = $"Create a brand-new Academic IELTS {(taskType == Ielts.Task1 ? "Task 1 visual report" : "Task 2 discursive essay")}.\nArchetype / Category: \"{category}\"\nSubject Domain: \"{chosenTopic}\"\n\n{chartSchemaHint}\n\nReturn ONLY valid JSON:\n{{\n  \"title\": \"Concise descriptive title\",\n  \"topic\": \"{chosenTopic}\",\n  \"promptText\": \"Official-style IELTS prompt instructions ending with 'Write at least {(taskType == Ielts.Task1 ? "150" : "250")} words.'\",\n  \"chartSpec\": {(taskType == Ielts.Task1 ? "{ ... }" : "null")}\n}}",
        }, cancellationToken);

        var parsed = JsonExtractor.ExtractJsonNode(raw);
        var title = parsed["title"]?.GetValue<string>() ?? $"Academic {taskType.ToUpperInvariant()} Practice";
        var parsedTopic = parsed["topic"]?.GetValue<string>() ?? chosenTopic;
        var promptText = parsed["promptText"]?.GetValue<string>() ?? string.Empty;
        var chartSpec = ChartSpecNormalizer.Normalize(parsed["chartSpec"]);

        return new GeneratedPromptResult(title, parsedTopic, promptText, chartSpec);
    }

    private static string GetChartSchemaPromptForCategory(string category) => category switch
    {
        "line" or "bar" =>
            $"Include \"chartSpec\" with exact shape:\n{{ \"kind\": \"{category}\", \"title\": \"...\", \"subtitle\": \"...\", \"xAxisKey\": \"xLabel\", \"yAxisUnit\": \"%\", \"series\": [{{ \"key\": \"s1\", \"label\": \"Series 1\", \"color\": \"#16656e\" }}, {{ \"key\": \"s2\", \"label\": \"Series 2\", \"color\": \"#c55e1b\" }}, {{ \"key\": \"s3\", \"label\": \"Series 3\", \"color\": \"#2b7a52\" }}], \"data\": [{{ \"xLabel\": \"2005\", \"s1\": 20, \"s2\": 45, \"s3\": 12 }}, ... (5-7 points with interesting crossovers/contrasts)], \"sourceNote\": \"Source: ...\" }}",
        "mixed" =>
            "Include \"chartSpec\" with exact shape:\n{ \"kind\": \"mixed\", \"title\": \"...\", \"subtitle\": \"...\", \"xAxisKey\": \"year\", \"yAxisUnit\": \"units\", \"yAxisRightUnit\": \"%\", \"series\": [{ \"key\": \"vol\", \"label\": \"... (Bar)\", \"type\": \"bar\", \"yAxisId\": \"left\", \"color\": \"#16656e\" }, { \"key\": \"share\", \"label\": \"... (%)\", \"type\": \"line\", \"yAxisId\": \"right\", \"color\": \"#c55e1b\" }], \"data\": [{ \"year\": \"2005\", \"vol\": 120, \"share\": 18 }, ... (6 points)], \"sourceNote\": \"Source: ...\" }",
        "pie" =>
            "Include \"chartSpec\" with two comparative periods:\n{ \"kind\": \"pie\", \"title\": \"...\", \"subtitle\": \"...\", \"unit\": \"%\", \"charts\": [{ \"label\": \"2000\", \"slices\": [{ \"name\": \"...\", \"value\": 40 }, ... (slices summing to 100)] }, { \"label\": \"2025\", \"slices\": [{ \"name\": \"...\", \"value\": 22 }, ... (summing to 100)] }], \"sourceNote\": \"Source: ...\" }",
        "table" =>
            "Include \"chartSpec\" with 5 rows and 5 columns:\n{ \"kind\": \"table\", \"title\": \"...\", \"unitNote\": \"...\", \"columns\": [\"Country/Sector\", \"Metric 1\", \"Metric 2\", \"Metric 3\", \"Metric 4\"], \"rows\": [[\"...\", 120, \"34%\", ...], ...], \"sourceNote\": \"Source: ...\" }",
        "process" =>
            "Include \"chartSpec\" with 6-8 stages:\n{ \"kind\": \"process\", \"title\": \"...\", \"subtitle\": \"...\", \"isCyclic\": false, \"steps\": [{ \"stepNumber\": 1, \"stageName\": \"...\", \"detail\": \"...\", \"badge\": \"...\" }, ...] }",
        "map" =>
            "Include \"chartSpec\" comparing two time periods across 9 compass zones (\"NW\",\"N\",\"NE\",\"W\",\"C\",\"E\",\"SW\",\"S\",\"SE\") and categories (\"residential\"|\"nature\"|\"commercial\"|\"industrial\"|\"transport\"|\"leisure\"|\"civic\"):\n{ \"kind\": \"map\", \"title\": \"...\", \"subtitle\": \"...\", \"periodA\": \"1995\", \"periodB\": \"Present Day\", \"waterway\": \"coastal_south\" | \"river_vertical\" | \"none\", \"mainRoad\": \"horizontal\" | \"vertical\", \"featuresA\": [{ \"zone\": \"NW\", \"label\": \"...\", \"category\": \"nature\" }, ...all 9 zones], \"featuresB\": [{ \"zone\": \"NW\", \"label\": \"...\", \"category\": \"leisure\", \"changed\": true }, ...all 9 zones] }",
        _ => string.Empty,
    };

    /* ============================================================================
     * 5. 5-MINUTE TARGETED MICRO-DRILLS (Generation & Instant Scoring)
     * ============================================================================ */

    public async Task<MicroDrillChallenge> GenerateMicroDrillWithAiAsync(
        string userId,
        string requestedDrillType,
        string? requestedLevel,
        IReadOnlyList<string>? vaultHighlights = null,
        ResolvedAiConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        var cfg = config ?? await configResolver.ResolveAsync(userId, cancellationToken);
        var level = Ielts.IsDrillLevel(requestedLevel) ? requestedLevel! : "advanced";
        var drillType = DrillTemplates.Builtin.ContainsKey(requestedDrillType) ? requestedDrillType : "cohesion";
        var l1Code = cfg.L1CoachingEnabled ? cfg.NativeLanguage : "neutral";

        var baseTemplate = level == "beginner"
            ? (drillType == "l1_surgeon"
                ? (DrillTemplates.BeginnerL1Templates.TryGetValue(l1Code, out var l1T) ? l1T : DrillTemplates.BeginnerL1Templates["neutral"])
                : DrillTemplates.Beginner[drillType])
            : DrillTemplates.Builtin[drillType];

        var fallback = baseTemplate with
        {
            Level = level,
            Steps = baseTemplate.Steps ?? (level == "intermediate" && DrillTemplates.IntermediateSteps.TryGetValue(drillType, out var intSteps) ? intSteps : null),
        };

        if (string.IsNullOrEmpty(cfg.Fast.ApiKey))
        {
            return fallback;
        }

        var guidance = GetLevelGuidance(level, cfg.CurrentBand, cfg.TargetBand);
        var l1Profile = cfg.L1CoachingEnabled ? L1Profiles.Get(cfg.NativeLanguage) : L1Profiles.Get("neutral");
        var vaultNote = vaultHighlights is { Count: > 0 }
            ? $"Incorporate patterns similar to the student's recent Vault weaknesses: {string.Join(" | ", vaultHighlights)}"
            : string.Empty;

        var typePrompt = drillType == "overview_sprint"
            ? $"Include a \"chartSpec\" with {(level == "beginner" ? 2 : 3)} series and 5 data points using this EXACT shape (all data values must be plain numbers, and \"yAxisUnit\" must be a short suffix like \"%\", \"k\", \"M\", or \"\" — put longer unit notes in \"subtitle\"):\n{{ \"kind\": \"line\" | \"bar\", \"title\": \"...\", \"subtitle\": \"...\", \"xAxisKey\": \"year\", \"yAxisUnit\": \"%\", \"series\": [{{ \"key\": \"s1\", \"label\": \"Series 1\", \"color\": \"#16656e\" }}, {{ \"key\": \"s2\", \"label\": \"Series 2\", \"color\": \"#c55e1b\" }}], \"data\": [{{ \"year\": \"2015\", \"s1\": 20, \"s2\": 45 }}, ... (5 points)] }}\nSet \"flawedText\" to null. Ask the student to write ONLY a 2-sentence Task 1 Overview without raw statistics."
            : $"Set \"chartSpec\" to null and provide a {(level == "beginner" ? "1-3 short sentence" : "2-3 sentence")} \"flawedText\" paragraph exhibiting realistic Band {(level == "beginner" ? "4.5–5.5" : "6.0–6.5")} weaknesses for \"{drillType}\" ({(drillType == "l1_surgeon" ? l1Profile.CoachingFocus : "mechanical linkers or informal unhedged claims")}).";

        var defaults = AiProviders.Defaults.GetValueOrDefault(cfg.Fast.Provider);
        var drillTools = level != "beginner" ? historyTools?.CreateForDrillGenerator(cfg.UserId) : null;
        var raw = await ai.CallLlmAsync(new LlmCompletionRequest
        {
            UserId = cfg.UserId,
            KeySource = cfg.Fast.KeySource,
            Plan = cfg.Plan,
            Provider = cfg.Fast.Provider,
            ApiKey = cfg.Fast.ApiKey,
            Model = cfg.Fast.Model,
            FallbackModels = defaults?.FastFallbacks,
            Feature = "drill_generate",
            AgentName = "DrillGeneratorAgent",
            Tools = drillTools,
            JsonMode = true,
            ThinkingMode = "disabled",
            TimeoutMs = 40_000,
            SystemPrompt = level == "advanced"
                ? $"You design crisp 5-minute Academic IELTS writing micro-drills calibrated for Band {cfg.TargetBand.ToString("F1", CultureInfo.InvariantCulture)}+ ({l1Profile.Label})."
                : $"You design friendly, scaffolded 5-minute IELTS writing micro-drills for {level} writers, aiming at about Band {guidance.Band.ToString("F1", CultureInfo.InvariantCulture)} ({l1Profile.Label}).",
            UserPrompt = $"Create a fresh 5-minute drill of type \"{drillType}\".\n{guidance.Generation}\n{vaultNote}\n{typePrompt}\n\nReturn JSON:\n{{\n  \"drillType\": \"{drillType}\",\n  \"targetCriterion\": \"{fallback.TargetCriterion}\",\n  \"title\": \"...\",\n  \"instructions\": \"...\",\n  \"timeLimitSeconds\": {(drillType == "overview_sprint" && level != "beginner" ? 240 : 300)},\n  \"sourceContext\": \"...\",\n  \"chartSpec\": {(drillType == "overview_sprint" ? "{ ... }" : "null")},\n  \"flawedText\": {(drillType == "overview_sprint" ? "null" : "\"...\"")},\n  \"hints\": [\"...\", \"...\"],\n  \"referenceModelAnswer\": \"{(level == "advanced" ? "Band 8.5" : $"Band {guidance.Band.ToString("F1", CultureInfo.InvariantCulture)}")} reference rewrite/overview\"{guidance.StepsSchema}\n}}",
        }, cancellationToken);

        var parsed = JsonExtractor.ExtractJsonNode(raw);
        var chartSpec = drillType == "overview_sprint"
            ? ChartSpecNormalizer.NormalizeCartesian(parsed["chartSpec"]) ?? fallback.ChartSpec
            : null;

        var parsedHints = (parsed["hints"] as JsonArray)?
            .Select(TryGetString)
            .Where(h => !string.IsNullOrEmpty(h))
            .Cast<string>()
            .ToList();

        return new MicroDrillChallenge
        {
            DrillType = drillType,
            TargetCriterion = TryGetString(parsed["targetCriterion"]) ?? fallback.TargetCriterion,
            Title = TryGetString(parsed["title"]) ?? fallback.Title,
            Instructions = TryGetString(parsed["instructions"]) ?? fallback.Instructions,
            TimeLimitSeconds = TryGetInt(parsed["timeLimitSeconds"]) is > 0 and var t ? t : fallback.TimeLimitSeconds,
            SourceContext = TryGetString(parsed["sourceContext"]) ?? fallback.SourceContext,
            ChartSpec = chartSpec,
            FlawedText = TryGetString(parsed["flawedText"]) ?? fallback.FlawedText,
            Hints = parsedHints is { Count: > 0 } ? parsedHints : fallback.Hints,
            ReferenceModelAnswer = TryGetString(parsed["referenceModelAnswer"]) ?? fallback.ReferenceModelAnswer,
            Level = level,
            Steps = SanitizeSteps(parsed["steps"]) ?? fallback.Steps,
            WordBank = SanitizeWordBank(parsed["wordBank"]) ?? fallback.WordBank,
        };
    }

    public async Task<MicroDrillScore> ScoreMicroDrillWithAiAsync(
        string userId,
        MicroDrillChallenge challenge,
        string userResponse,
        string? requestedLevel = null,
        ResolvedAiConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        var cfg = config ?? await configResolver.ResolveAsync(userId, cancellationToken);
        var level = Ielts.IsDrillLevel(requestedLevel) ? requestedLevel!
            : Ielts.IsDrillLevel(challenge.Level) ? challenge.Level!
            : "advanced";

        if (string.IsNullOrEmpty(cfg.Fast.ApiKey) && level != "advanced")
        {
            return ScoreScaffoldedDrillLocally(challenge, userResponse, level);
        }

        if (level != "advanced")
        {
            return await ScoreScaffoldedDrillWithAiAsync(cfg, challenge, userResponse, level, cancellationToken);
        }

        if (string.IsNullOrEmpty(cfg.Fast.ApiKey))
        {
            var words = Ielts.CountWords(userResponse);
            var hasMechanical = MechanicalAdvancedRegex().IsMatch(userResponse);
            var score = words < 15 ? 4 : hasMechanical ? 6 : 8;
            return new MicroDrillScore
            {
                ScoreOutOf10 = score,
                EstimatedBandImpact = score >= 8 ? "Band 7.5–8.0 execution" : "Band 6.0–6.5 execution",
                Feedback = score >= 8
                    ? "Clean, compact academic phrasing! Compare your version against the Band 8.5 reference below (add your DeepSeek API key in Settings for live line-by-line drill scoring)."
                    : hasMechanical
                        ? "Watch out—a mechanical connector slipped in. Try linking clauses with demonstrative noun phrases ('This shift...') instead."
                        : "Expand your response into complete academic sentences addressing every flaw in the prompt.",
                ImprovedVersion = challenge.ReferenceModelAnswer,
                KeyTakeaway = "High-band Academic IELTS prose relies on clause integration, hedging, and precision rather than memorised transition phrases.",
            };
        }

        var defaults = AiProviders.Defaults.GetValueOrDefault(cfg.Fast.Provider);
        var flawedBlock = !string.IsNullOrEmpty(challenge.FlawedText) ? $"ORIGINAL FLAWED PASSAGE:\n{challenge.FlawedText}\n" : string.Empty;
        var chartBlock = challenge.ChartSpec is not null ? $"CHART DATA:\n{challenge.ChartSpec.ToJsonString()}\n" : string.Empty;

        var raw = await ai.CallLlmAsync(new LlmCompletionRequest
        {
            UserId = cfg.UserId,
            KeySource = cfg.Fast.KeySource,
            Plan = cfg.Plan,
            Provider = cfg.Fast.Provider,
            ApiKey = cfg.Fast.ApiKey,
            Model = cfg.Fast.Model,
            FallbackModels = defaults?.FastFallbacks,
            Feature = "drill_score",
            AgentName = "DrillScorerAgent",
            JsonMode = true,
            ThinkingMode = "disabled",
            TimeoutMs = 35_000,
            SystemPrompt = "You are a sharp, encouraging Academic IELTS Writing Coach grading a 5-minute targeted micro-drill.",
            UserPrompt = $"DRILL: {challenge.Title} ({challenge.DrillType})\nINSTRUCTIONS: {challenge.Instructions}\n{flawedBlock}{chartBlock}REFERENCE BAND 8.5 ANSWER:\n{challenge.ReferenceModelAnswer}\n\nSTUDENT SUBMISSION:\n\"\"\"\n{userResponse}\n\"\"\"\n\nReturn JSON:\n{{\n  \"scoreOutOf10\": 8,\n  \"estimatedBandImpact\": \"e.g. Band 8.0 Coherence execution\",\n  \"feedback\": \"2-3 crisp sentences praising what worked and pinpointing any remaining slip.\",\n  \"improvedVersion\": \"Polished version of the student's own phrasing at Band 8.5\",\n  \"keyTakeaway\": \"1 memorable rule to carry into their next timed essay\"\n}}",
        }, cancellationToken);

        return JsonExtractor.ExtractJsonObject<MicroDrillScore>(raw);
    }

    private async Task<MicroDrillScore> ScoreScaffoldedDrillWithAiAsync(
        ResolvedAiConfig cfg,
        MicroDrillChallenge challenge,
        string userResponse,
        string level,
        CancellationToken cancellationToken)
    {
        var guidance = GetLevelGuidance(level, cfg.CurrentBand, cfg.TargetBand);
        var steps = challenge.Steps is { Count: > 0 }
            ? $"GUIDED STEPS:\n{string.Join("\n", challenge.Steps.Select((st, i) => $"{i + 1}. {st.Prompt}"))}\n"
            : string.Empty;
        var flawedBlock = !string.IsNullOrEmpty(challenge.FlawedText) ? $"ORIGINAL PASSAGE:\n{challenge.FlawedText}\n" : string.Empty;
        var chartBlock = challenge.ChartSpec is not null ? $"CHART DATA:\n{challenge.ChartSpec.ToJsonString()}\n" : string.Empty;
        var bandLabel = guidance.Band.ToString("F1", CultureInfo.InvariantCulture);
        var defaults = AiProviders.Defaults.GetValueOrDefault(cfg.Fast.Provider);

        var raw = await ai.CallLlmAsync(new LlmCompletionRequest
        {
            UserId = cfg.UserId,
            KeySource = cfg.Fast.KeySource,
            Plan = cfg.Plan,
            Provider = cfg.Fast.Provider,
            ApiKey = cfg.Fast.ApiKey!,
            Model = cfg.Fast.Model,
            FallbackModels = defaults?.FastFallbacks,
            Feature = "drill_score",
            AgentName = "DrillScorerAgent",
            JsonMode = true,
            ThinkingMode = "disabled",
            TimeoutMs = 35_000,
            SystemPrompt = level == "beginner"
                ? "You are a warm, patient writing coach for beginner English writers. Use simple English (CEFR B1) and short sentences. Never use grammar jargon. Always praise one real strength first, then give ONE clear fix."
                : "You are a clear, encouraging IELTS writing coach for intermediate students. Explain any technical term in plain words. Praise one strength, then give the most important fix.",
            UserPrompt = $"DRILL: {challenge.Title} ({challenge.DrillType}, level: {level})\nINSTRUCTIONS: {challenge.Instructions}\n{steps}{flawedBlock}{chartBlock}EXAMPLE ANSWER (about Band {bandLabel}):\n{challenge.ReferenceModelAnswer}\n\nSTUDENT SUBMISSION:\n\"\"\"\n{userResponse}\n\"\"\"\n\nScore kindly but honestly for a {level} writer (do not compare with Band 8 writing; reward effort and correct basics).\n\nReturn JSON:\n{{\n  \"scoreOutOf10\": 7,\n  \"estimatedBandImpact\": \"Short encouraging progress label, e.g. 'Moving toward Band {bandLabel}'\",\n  \"feedback\": \"2 short, simple sentences: one real strength, then what to improve.\",\n  \"oneThingToFix\": \"The single most important change, in one simple sentence.\",\n  \"stepFeedback\": [\"One short line per guided step: done well / try this\"],\n  \"improvedVersion\": \"The student's OWN answer with only the necessary fixes (keep their words and ideas; about Band {bandLabel}, not advanced)\",\n  \"keyTakeaway\": \"1 simple rule to remember, in under 15 words\"\n}}",
        }, cancellationToken);

        var parsed = JsonExtractor.ExtractJsonNode(raw);
        var stepFeedback = (parsed["stepFeedback"] as JsonArray)?
            .Select(TryGetString)
            .Where(x => x is not null)
            .Cast<string>()
            .ToList();

        return new MicroDrillScore
        {
            ScoreOutOf10 = TryGetInt(parsed["scoreOutOf10"]) ?? 6,
            EstimatedBandImpact = TryGetString(parsed["estimatedBandImpact"]) ?? "Good progress",
            Feedback = TryGetString(parsed["feedback"]) ?? "Good effort! Compare your answer with the example below.",
            ImprovedVersion = TryGetString(parsed["improvedVersion"]) ?? challenge.ReferenceModelAnswer,
            KeyTakeaway = TryGetString(parsed["keyTakeaway"]) ?? challenge.Hints?.FirstOrDefault() ?? string.Empty,
            OneThingToFix = TryGetString(parsed["oneThingToFix"]),
            StepFeedback = stepFeedback,
        };
    }

    private static string? TryGetString(JsonNode? node) =>
        node is JsonValue val && val.TryGetValue<string>(out var s) ? s : null;

    private static int? TryGetInt(JsonNode? node)
    {
        if (node is not JsonValue val)
        {
            return null;
        }

        if (val.TryGetValue<int>(out var i))
        {
            return i;
        }

        if (val.TryGetValue<double>(out var d) && double.IsFinite(d))
        {
            return (int)Math.Round(d, MidpointRounding.AwayFromZero);
        }

        if (val.TryGetValue<string>(out var s)
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed))
        {
            return (int)Math.Round(parsed, MidpointRounding.AwayFromZero);
        }

        return null;
    }

    private static MicroDrillScore ScoreScaffoldedDrillLocally(
        MicroDrillChallenge challenge,
        string userResponse,
        string level)
    {
        var words = Ielts.CountWords(userResponse);
        var lower = userResponse.ToLowerInvariant();
        var minWords = level == "beginner" ? 8 : 15;
        var usedWords = (challenge.WordBank ?? [])
            .Where(w => lower.Contains(w.ToLowerInvariant().TrimEnd(',', '.'), StringComparison.Ordinal))
            .ToList();
        var unfilledGaps = UnfilledGapsRegex().IsMatch(userResponse);
        var mechanical = MechanicalScaffoldedRegex().IsMatch(userResponse);
        var hasNumbers = challenge.DrillType == "overview_sprint" && DigitRegex().IsMatch(userResponse);
        var copiedOriginal = !string.IsNullOrEmpty(challenge.FlawedText)
            && string.Equals(userResponse.Trim(), challenge.FlawedText.Trim(), StringComparison.OrdinalIgnoreCase);

        var score = 5;
        if (words >= minWords) score += 2;
        if (usedWords.Count >= 2) score += 1;
        if (!unfilledGaps && !mechanical && !hasNumbers && !copiedOriginal) score += 1;
        if (words < minWords / 2.0) score -= 2;
        if (unfilledGaps || copiedOriginal) score = Math.Min(score, 5);
        score = Math.Clamp(score, 3, 9);

        var oneThingToFix = unfilledGaps
            ? "Fill in the “___” gaps in the sentence starter with your own words."
            : copiedOriginal
                ? "Your answer is the same as the original. Try changing the words the steps point to."
                : hasNumbers
                    ? "Remove the numbers. A summary talks about the general change, not exact figures."
                    : mechanical
                        ? "Try removing list words like “Firstly” or “Also”, and start with your idea."
                        : words < minWords
                            ? $"Write a little more — aim for at least {minWords} words."
                            : "Compare your answer with the example and pick one phrase to borrow next time.";

        return new MicroDrillScore
        {
            ScoreOutOf10 = score,
            EstimatedBandImpact = score >= 7 ? "Great progress — keep going!" : "Good start — you are getting there",
            Feedback = (score >= 7 ? "Nice work! Your answer is clear and complete. " : "Good start! ")
                + (usedWords.Count > 0
                    ? $"You used useful words like “{string.Join("”, “", usedWords.Take(2))}”. "
                    : "Try using 2 or 3 words from the word bank next time. ")
                + "Compare your answer with the example below. (Add an AI key in Settings for personal feedback.)",
            ImprovedVersion = challenge.ReferenceModelAnswer,
            KeyTakeaway = challenge.Hints?.FirstOrDefault() ?? "Short, clear sentences are better than long, confusing ones.",
            OneThingToFix = oneThingToFix,
        };
    }

    /* ============================================================================
     * 6. SHARED PEDAGOGY, SANITIZATION & LOCAL DIAGNOSTIC PREVIEW
     * ============================================================================ */

    private static string GetSharedPedagogyRules(string taskType)
    {
        var taskSpecificRule = taskType == Ielts.Task1
            ? "- TASK 1 OVERVIEW RULE (CRITICAL — DO NOT CONTRADICT): The Task 1 Overview paragraph MUST be 100% free of raw numbers, percentages, and specific years. It must synthesise 2–3 overarching macro trends, dominant categories, or crossovers ONLY. Exact figures (e.g., \"coal fell from 65% to 0%\") belong strictly in Body Paragraphs 1 & 2, NEVER in the Overview. Never advise adding numbers or percentages to the Overview.\n- TASK 1 REGISTER CONSISTENCY: Across all tools (Outline Check, Paragraph Check, Collocations, and Final Grader), avoid dramatic/journalistic metaphors (\"collapsed\", \"skyrocketed\", \"crashed\", \"exploded\"). Consistently recommend objective statistical phrasing (\"declined precipitously\", \"fell sharply to zero\", \"experienced a steep contraction\", \"surged\", \"expanded rapidly\")."
            : "- TASK 2 THESIS & EXTENSION RULE: The introduction must state a clear, nuanced position that is maintained throughout. Body paragraphs must develop one central claim each with specific causal mechanisms and concrete examples rather than sweeping generalisations.";

        return $"SHARED HOUSE PEDAGOGY & CONSISTENCY RULES:\n{taskSpecificRule}\n- FALSE COGNATES & WHOLE-PHRASE REPLACEMENTS: When \"actually\" is used to mean \"currently / at present / in recent years\" or inside a trend/growth verb phrase (e.g., \"Actually, ...\" or \"it has actually grown very fast\"), it is a Romance L1 false cognate (Spanish/Portuguese/French \"actualmente/atualmente/actuellement\"). NEVER keep \"actually\" in a suggested revision (do NOT suggest \"it has actually grown very fast\") and NEVER suggest swapping \"actually\" with an intensifier like \"extremely\" (which creates ungrammatical double intensifiers like \"grown extremely very fast\"). Instead, replace the entire verb/adverb phrase with natural academic English (e.g., \"it has expanded rapidly in recent years\" or \"Currently, ...\"). Every suggested replacement MUST be grammatically valid in its exact surrounding sentence context.\n- STRICT EVIDENCE & ANTI-HALLUCINATION RULE: Every error or limiting factor mentioned in feedback MUST exist verbatim in the candidate's text. NEVER invent or assume errors from the L1 profile (such as claiming a \"missing subject\" when every sentence has a valid subject). Note that compound predicates sharing a single subject (e.g., \"Coal stood at 65% in 1990 and fell to 0% by 2025\") are grammatically complete and are NOT missing a subject.\n- STRICT CATEGORY BOUNDARIES:\n  * GRA (Grammatical Range & Accuracy): Comma splices (joining two independent clauses with a comma or \", however,\"), run-on sentences, fragments, punctuation errors, verb tense errors, subject-verb agreement, and article/preposition grammar errors MUST be classified under GRA (with severity: \"error\"), NEVER under CC.\n  * CC (Coherence & Cohesion): Paragraphing, topic sentences, logical progression, and replacing mechanical sentence-initial linkers (\"Firstly\", \"Moreover\", \"On the other hand\") with demonstrative referencing (\"This shift...\", \"Such measures...\") — never punctuation or comma splices.\n  * LR (Lexical Resource): Upgrading genuinely informal vocabulary (\"very fast\", \"a lot of\", \"big drop\") or imprecise collocations to C1/C2 academic register.\n  * L1 (Native-Language Interference): False cognates (\"actually\" for \"actualmente\", \"assist to\", \"sensible\") and direct L1 calques.\n- NO LATERAL OR TRIVIAL SYNONYM SWAPS: Never suggest swapping words that are already accurate Academic English of the same register — specifically, NEVER suggest swapping \"whereas\" ↔ \"while\", \"although\" ↔ \"even though\", or \"graph\" ↔ \"chart\". Prefer fewer, genuinely high-leverage annotations over padding.";
    }

    internal static string CleanDoubleIntensifiers(string text)
    {
        var step1 = DoubleIntensifierRegex().Replace(text, "rapidly");
        return GrownRapidlyFastRegex().Replace(step1, "expanded rapidly");
    }

    internal static bool IsTrivialLateralSwap(string originalSpan, string replacement, string explanation)
    {
        var normOrig = TrailingPunctuationRegex().Replace(originalSpan.Trim().ToLowerInvariant(), "");
        var normRepl = TrailingPunctuationRegex().Replace(replacement.Trim().ToLowerInvariant(), "");
        if (normOrig == normRepl)
        {
            return true;
        }

        static string NormalizeLateral(string s)
        {
            var r = ContrastConjRegex().Replace(s, "__CONTRAST_CONJ__");
            r = ConcessiveConjRegex().Replace(r, "__CONCESSIVE_CONJ__");
            return WhitespaceRegex().Replace(r, " ").Trim();
        }

        if (NormalizeLateral(normOrig) == NormalizeLateral(normRepl))
        {
            return true;
        }

        return LateralSwapExplanationRegex().IsMatch(explanation);
    }

    internal static List<InlineAnnotation> NormalizeAndFilterAnnotations(IEnumerable<InlineAnnotation> rawAnnotations)
    {
        var result = new List<InlineAnnotation>();
        var idx = 0;

        foreach (var a in rawAnnotations)
        {
            idx++;
            if (a is null || string.IsNullOrWhiteSpace(a.OriginalSpan))
            {
                continue;
            }

            var originalSpan = a.OriginalSpan.Trim();
            var replacement = CleanDoubleIntensifiers((a.Replacement ?? originalSpan).Trim());
            var explanation = (a.Explanation ?? "Academic refinement suggested.").Trim();
            var category = ValidCategories.Contains(a.Category) ? a.Category : "LR";
            var severity = a.Severity == "error" ? "error" : "upgrade";

            if (IsTrivialLateralSwap(originalSpan, replacement, explanation))
            {
                continue;
            }

            if (ActuallyWordRegex().IsMatch(originalSpan))
            {
                if (ActuallyWordRegex().IsMatch(replacement))
                {
                    replacement = HasActuallyGrownRegex().Replace(replacement, "has expanded rapidly in recent years");
                    replacement = ActuallyGrownRegex().Replace(replacement, "expanded rapidly in recent years");
                    replacement = ActuallyGrewRegex().Replace(replacement, "expanded rapidly");
                    replacement = ActuallyCommaRegex().Replace(replacement, "Currently, ");
                    replacement = ActuallySpaceRegex().Replace(replacement, "");
                }

                if (VeryFastRegex().IsMatch(replacement))
                {
                    replacement = GrownVeryFastRegex().Replace(replacement, "expanded rapidly");
                    replacement = GrewVeryFastRegex().Replace(replacement, "expanded rapidly");
                    replacement = VeryFastRegex().Replace(replacement, "rapidly");
                }

                if (ActuallyWithExtremelyRegex().IsMatch(explanation))
                {
                    explanation = "'Actually' is a Romance false cognate for 'actualmente' ('currently / in recent years'); replace the whole phrase with formal academic phrasing such as 'expanded rapidly'.";
                }

                if (FalseCognateHintRegex().IsMatch(explanation))
                {
                    category = "L1";
                    severity = "error";
                }
            }

            if (GraBoundaryRegex().IsMatch(explanation))
            {
                category = "GRA";
                severity = "error";
            }
            else if (FalseCognateRegex().IsMatch(explanation))
            {
                category = "L1";
                severity = "error";
            }

            result.Add(new InlineAnnotation
            {
                Id = string.IsNullOrEmpty(a.Id) ? $"ann-{idx}" : a.Id,
                Category = category,
                Severity = severity,
                OriginalSpan = originalSpan,
                Replacement = replacement,
                Explanation = explanation,
            });
        }

        return result;
    }

    private static bool HasLikelyMissingSubjectInEssay(string essayText) =>
        MissingSubjectSentenceRegex().IsMatch(essayText);

    private static EvaluationCriteria SanitizeCriteriaFeedback(
        EvaluationCriteria criteria,
        string taskType,
        string essayText)
    {
        if (taskType == Ielts.Task1)
        {
            static bool OverviewHasNumbersAdvice(string text) =>
                OverviewRegex().IsMatch(text)
                && FigureSignalRegex().IsMatch(text)
                && !NegationBeforeNumbersRegex().IsMatch(text);

            if (OverviewHasNumbersAdvice(criteria.TaTr.HowToGainHalfBand))
            {
                criteria.TaTr.HowToGainHalfBand =
                    "Keep your Overview paragraph strictly number-free (2–3 macro trends and key crossovers only) and place exact start/end figures and percentage comparisons in Body Paragraphs 1 and 2.";
            }

            criteria.TaTr.LimitingFactors = criteria.TaTr.LimitingFactors
                .Select(lf => OverviewHasNumbersAdvice(lf)
                    ? "Body paragraphs need sharper comparative figures (keep raw percentages in Body 1 & Body 2 while keeping the Overview number-free)"
                    : lf)
                .ToList();
        }

        static bool BadActuallyTip(string text) => ActuallyRegex().IsMatch(text) && ExtremelyRegex().IsMatch(text);

        if (BadActuallyTip(criteria.Lr.HowToGainHalfBand))
        {
            criteria.Lr.HowToGainHalfBand =
                "Replace L1 false cognates like 'actually' (actualmente → 'currently / in recent years') and conversational intensifiers ('very fast') with C1 academic verb-adverb collocations ('expanded rapidly', 'rose sharply').";
        }
        else
        {
            criteria.Lr.HowToGainHalfBand = CleanDoubleIntensifiers(criteria.Lr.HowToGainHalfBand);
        }

        criteria.Lr.LimitingFactors = criteria.Lr.LimitingFactors
            .Select(lf => BadActuallyTip(lf)
                ? "Uses L1 false cognate 'actually' (for 'currently') alongside informal intensifiers ('very fast')"
                : CleanDoubleIntensifiers(lf))
            .ToList();

        if (!HasLikelyMissingSubjectInEssay(essayText))
        {
            if (MissingSubjectRegex().IsMatch(criteria.Gra.DescriptorSummary))
            {
                var cleaned = MissingSubjectClauseRegex().Replace(criteria.Gra.DescriptorSummary, "");
                cleaned = MissingSubjectPrefixRegex().Replace(cleaned, "").Trim();
                criteria.Gra.DescriptorSummary = cleaned.Length < 15
                    ? "Uses a mix of simple and complex sentence structures; punctuation across clause boundaries and subordinate clauses can be tightened."
                    : cleaned;
            }

            criteria.Gra.LimitingFactors = criteria.Gra.LimitingFactors
                .Where(lf => !MissingSubjectRegex().IsMatch(lf))
                .ToList();

            if (MissingSubjectRegex().IsMatch(criteria.Gra.HowToGainHalfBand))
            {
                criteria.Gra.HowToGainHalfBand =
                    "Eliminate comma splices between independent clauses (use a semicolon, period, or subordinating conjunction) and vary participial and relative clauses.";
            }
        }

        return criteria;
    }

    private static EvaluationPayload BuildHeuristicDiagnosticPreview(
        EvaluateEssayInput input,
        ResolvedAiConfig cfg,
        int wordCount,
        int minWords)
    {
        var text = input.EssayText;
        var paragraphs = ParagraphSplitRegex().Split(text).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        var annotations = new List<InlineAnnotation>();

        var linkerMatches = MechanicalLinkerRegex().Matches(text);
        foreach (Match match in linkerMatches.Take(2))
        {
            var span = match.Value.Trim();
            annotations.Add(new InlineAnnotation
            {
                Id = $"prev-cc-{annotations.Count + 1}",
                Category = "CC",
                Severity = "upgrade",
                OriginalSpan = span,
                Replacement = MechanicalPrefixRegex().Replace(span, "Notably, "),
                Explanation = "Mechanical sequence linkers often cap Coherence & Cohesion at Band 6.5. Prefer demonstrative referencing ('This trend...', 'Such measures...').",
            });
        }

        var actuallyMatch = HeuristicActuallyRegex().Match(text);
        if (actuallyMatch.Success)
        {
            var rawSpan = actuallyMatch.Value.Trim();
            var cleanedSpan = rawSpan.StartsWith("actually,", StringComparison.OrdinalIgnoreCase)
                ? ActuallyPrefixRegex().Replace(rawSpan, "Currently")
                : VeryFastRegex().Replace(
                    GrownVeryFastRegex().Replace(ActuallySpaceRegex().Replace(rawSpan, ""), "expanded rapidly in recent years"),
                    "rapidly");

            annotations.Add(new InlineAnnotation
            {
                Id = $"prev-l1-{annotations.Count + 1}",
                Category = "L1",
                Severity = "error",
                OriginalSpan = rawSpan,
                Replacement = cleanedSpan,
                Explanation = "Spanish → English false friend: 'actually' means 'in fact', whereas 'actualmente' translates to 'currently / in recent years'.",
            });
        }

        var commaSpliceMatch = HeuristicCommaSpliceRegex().Match(text);
        if (commaSpliceMatch.Success)
        {
            var span = commaSpliceMatch.Value.Trim();
            annotations.Add(new InlineAnnotation
            {
                Id = $"prev-gra-{annotations.Count + 1}",
                Category = "GRA",
                Severity = "error",
                OriginalSpan = span,
                Replacement = HeuristicCommaSpliceReplaceRegex().Replace(span, "; $1,"),
                Explanation = "Comma splice (Grammar error): conjunctive adverbs ('however', 'therefore') between two independent clauses require a semicolon or period.",
            });
        }

        var informalMatch = HeuristicInformalRegex().Match(text);
        if (informalMatch.Success)
        {
            var span = informalMatch.Value.Trim();
            var repl = LotsOfRegex().Replace(span, "a substantial proportion of");
            repl = VeryBigRegex().Replace(repl, "pronounced");
            repl = ReallyImportantRegex().Replace(repl, "pivotal");
            annotations.Add(new InlineAnnotation
            {
                Id = $"prev-lr-{annotations.Count + 1}",
                Category = "LR",
                Severity = "upgrade",
                OriginalSpan = span,
                Replacement = repl,
                Explanation = "Upgrade conversational high-frequency phrasing to formal C1 academic register.",
            });
        }

        var underLengthPenalty = wordCount < minWords ? 1.0 : 0.0;
        var hasOverviewSignal = input.TaskType == Ielts.Task1
            ? OverviewSignalRegex().IsMatch(text)
            : paragraphs.Count >= 4;

        var taTrBand = Ielts.RoundBand((hasOverviewSignal ? 7.0 : 6.0) - underLengthPenalty);
        var ccBand = Ielts.RoundBand(paragraphs.Count >= 3 && linkerMatches.Count <= 1 ? 7.0 : 6.5);
        var lrBand = Ielts.RoundBand(informalMatch.Success ? 6.5 : 7.0);
        var graBand = Ielts.RoundBand(commaSpliceMatch.Success ? 6.0 : 6.5);
        var overallBand = Ielts.RoundBand((taTrBand + ccBand + lrBand + graBand) / 4.0);

        return new EvaluationPayload
        {
            OverallBand = overallBand,
            TaTrBand = taTrBand,
            CcBand = ccBand,
            LrBand = lrBand,
            GraBand = graBand,
            ExaminerSummary = $"[Local Diagnostic Preview — Add your DeepSeek API key in Settings for full DeepSeek-V4-Pro grading & personalised Band {cfg.TargetBand.ToString("F1", CultureInfo.InvariantCulture)} rewrites.] Structural scan analysed {wordCount} words across {paragraphs.Count} paragraph(s).",
            Criteria = new EvaluationCriteria
            {
                TaTr = new CriterionFeedback
                {
                    Band = taTrBand,
                    DescriptorSummary = wordCount < minWords
                        ? $"Under minimum length ({wordCount}/{minWords} words), which directly penalises Task fulfilment."
                        : hasOverviewSignal
                            ? "Clear macro structure detected; verify every prompt facet is fully extended."
                            : input.TaskType == Ielts.Task1
                                ? "No explicit Overview signpost ('Overall,...') detected—missing a clear overview caps Task Achievement at Band 5.0–6.0."
                                : "Aim for a 4-paragraph structure (Intro with clear thesis, 2 extended body paragraphs, concise conclusion).",
                    Strengths = wordCount >= minWords ? [$"Meets the {minWords}-word minimum ({wordCount} words)"] : [],
                    LimitingFactors = wordCount < minWords ? [$"Add at least {minWords - wordCount} more words of substantive analysis"] : [],
                    HowToGainHalfBand = input.TaskType == Ielts.Task1
                        ? "Pair a 2-sentence number-free Overview with crisp comparative figures in both body paragraphs."
                        : "Back each body paragraph's topic claim with a specific mechanism and concrete example.",
                },
                Cc = new CriterionFeedback
                {
                    Band = ccBand,
                    DescriptorSummary = $"Organised into {paragraphs.Count} paragraph(s){(linkerMatches.Count > 0 ? $" with {linkerMatches.Count} mechanical connector(s) flagged" : string.Empty)}.",
                    Strengths = paragraphs.Count >= 3 ? ["Clear multi-paragraph visual separation"] : [],
                    LimitingFactors = linkerMatches.Count > 0 ? ["Relies on formulaic sentence-initial linkers"] : [],
                    HowToGainHalfBand = "Connect sentences via demonstrative noun phrases ('This disparity...', 'Such policies...') rather than list markers.",
                },
                Lr = new CriterionFeedback
                {
                    Band = lrBand,
                    DescriptorSummary = "Heuristic scan of register formality and academic collocations.",
                    Strengths = ["Readable academic vocabulary base"],
                    LimitingFactors = informalMatch.Success ? ["Conversational intensifiers/quantifiers detected"] : [],
                    HowToGainHalfBand = "Replace generic verbs ('make', 'get', 'have') with domain-precise verbs ('generate', 'incur', 'exhibit').",
                },
                Gra = new CriterionFeedback
                {
                    Band = graBand,
                    DescriptorSummary = "Clause boundary and punctuation consistency check.",
                    Strengths = ["Complete sentence framing"],
                    LimitingFactors = commaSpliceMatch.Success ? ["Comma splice across conjunctive adverb"] : [],
                    HowToGainHalfBand = "Combine participial phrases and concessive clauses ('While X..., Y...') with spotless semicolon/comma control.",
                },
            },
            InlineAnnotations = annotations,
            TargetBandRewrite = "Connect your DeepSeek API key (or Gemini / OpenAI / Anthropic) in Settings → AI Engine to generate a full side-by-side Band 8.0+ rewrite of your exact essay arguments!",
            RewriteKeyChanges =
            [
                "Add DEEPSEEK_API_KEY in Settings (or .env) to activate DeepSeek-V4-Pro rewrites and multimodal Task 1 chart verification.",
            ],
            TopPriorities =
            [
                wordCount < minWords
                    ? $"Expand response past the {minWords}-word threshold ({wordCount} words written)."
                    : "Configure your DeepSeek API key in Settings to unlock full examiner commentary.",
                "Eliminate mechanical sequence linkers in favour of thematic referencing.",
                "Double-check L1 false cognates and comma-spliced conjunctive adverbs.",
            ],
            VaultItems = annotations.Take(3).Select(a => new EvaluationVaultItem
            {
                ItemType = a.Severity == "error" ? "error" : "collocation",
                Criterion = a.Category,
                OriginalText = a.OriginalSpan,
                UpgradedText = a.Replacement,
                Explanation = a.Explanation,
                ExampleSentence = a.Replacement,
            }).ToList(),
        };
    }

    private static (double Band, string Generation, string StepsSchema) GetLevelGuidance(string level, double currentBand, double targetBand)
    {
        if (level == "beginner")
        {
            var band = Math.Max(currentBand + 1, 5.5);
            return (band,
                $"The student is a BEGINNER writer (current band about {currentBand.ToString("F1", CultureInfo.InvariantCulture)}). Use simple everyday topics and simple English (CEFR B1) in the title, instructions, steps and hints. Do NOT use grammar jargon (no \"nominalisation\", \"subordination\", \"calque\", \"cohesive device\"). Keep any \"flawedText\" to 1-3 short sentences with at most 2 errors. Give 3 small \"steps\", each with a short \"prompt\" and, where useful, a fill-in \"starter\" sentence frame using \"___\" for the gaps. Give a \"wordBank\" of 6-9 useful words or short phrases. The \"referenceModelAnswer\" must be a simple, correct answer at about Band {band.ToString("F1", CultureInfo.InvariantCulture)} — NOT an advanced Band 8 answer.",
                ",\n  \"steps\": [{ \"prompt\": \"...\", \"starter\": \"... ___ ...\" }],\n  \"wordBank\": [\"...\", \"...\"]");
        }

        if (level == "intermediate")
        {
            var band = Math.Max(currentBand + 1, 6.5);
            return (band,
                $"The student is an INTERMEDIATE writer (current band about {currentBand.ToString("F1", CultureInfo.InvariantCulture)}). Keep explanations clear and explain any technical term briefly. Give 2-3 short \"steps\" that break the task into parts. The \"referenceModelAnswer\" should be at about Band {band.ToString("F1", CultureInfo.InvariantCulture)}.",
                ",\n  \"steps\": [{ \"prompt\": \"...\" }]");
        }

        return (Math.Max(targetBand, 8.0), string.Empty, string.Empty);
    }

    private static List<MicroDrillStep>? SanitizeSteps(JsonNode? node)
    {
        if (node is not JsonArray arr)
        {
            return null;
        }

        var clean = new List<MicroDrillStep>();
        foreach (var item in arr)
        {
            if (clean.Count >= 4)
            {
                break;
            }

            if (item is JsonObject obj && obj["prompt"]?.GetValue<string>() is { } prompt)
            {
                var starter = obj["starter"]?.GetValue<string>();
                clean.Add(new MicroDrillStep
                {
                    Prompt = prompt,
                    Starter = !string.IsNullOrWhiteSpace(starter) ? starter : null,
                });
            }
        }

        return clean.Count > 0 ? clean : null;
    }

    private static List<string>? SanitizeWordBank(JsonNode? node)
    {
        if (node is not JsonArray arr)
        {
            return null;
        }

        var clean = arr
            .Select(w => w is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Cast<string>()
            .Take(12)
            .ToList();

        return clean.Count > 0 ? clean : null;
    }

    private sealed class RubricScoreOutput
    {
        public double? TaTrBand { get; set; }
        public double? CcBand { get; set; }
        public double? LrBand { get; set; }
        public double? GraBand { get; set; }
        public string? ExaminerSummary { get; set; }
        public RubricCriteriaInput? Criteria { get; set; }
        public List<string>? TopPriorities { get; set; }
    }

    private sealed class RubricCriteriaInput
    {
        [System.Text.Json.Serialization.JsonPropertyName("TA_TR")]
        public CriterionFeedback? TaTr { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("CC")]
        public CriterionFeedback? Cc { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("LR")]
        public CriterionFeedback? Lr { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("GRA")]
        public CriterionFeedback? Gra { get; set; }
    }

    private sealed class AnnotationRewriteOutput
    {
        public List<InlineAnnotation>? InlineAnnotations { get; set; }
        public string? TargetBandRewrite { get; set; }
        public List<string>? RewriteKeyChanges { get; set; }
        public List<EvaluationVaultItem>? VaultItems { get; set; }
    }

    [GeneratedRegex(@"\b(extremely|very|really|highly)\s+(very|extremely|really)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DoubleIntensifierRegex();

    [GeneratedRegex(@"\bgrown\s+rapidly\s+fast\b", RegexOptions.IgnoreCase)]
    private static partial Regex GrownRapidlyFastRegex();

    [GeneratedRegex(@"[.,;!?]+$")]
    private static partial Regex TrailingPunctuationRegex();

    [GeneratedRegex(@"\b(whereas|while)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ContrastConjRegex();

    [GeneratedRegex(@"\b(although|even though|though)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ConcessiveConjRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\bswap(?:ping)?\s+['""“”]?whereas['""“”]?\s+(?:for|with|to)\s+['""“”]?while['""“”]?|\breplac(?:e|ing)\s+['""“”]?whereas['""“”]?\s+with\s+['""“”]?while['""“”]?|\breplac(?:e|ing)\s+['""“”]?while['""“”]?\s+with\s+['""“”]?whereas['""“”]?", RegexOptions.IgnoreCase)]
    private static partial Regex LateralSwapExplanationRegex();

    [GeneratedRegex(@"\bactually\b", RegexOptions.IgnoreCase)]
    private static partial Regex ActuallyWordRegex();

    [GeneratedRegex(@"\bhas actually grown very fast\b", RegexOptions.IgnoreCase)]
    private static partial Regex HasActuallyGrownRegex();

    [GeneratedRegex(@"\bactually grown very fast\b", RegexOptions.IgnoreCase)]
    private static partial Regex ActuallyGrownRegex();

    [GeneratedRegex(@"\bactually grew very fast\b", RegexOptions.IgnoreCase)]
    private static partial Regex ActuallyGrewRegex();

    [GeneratedRegex(@"\bactually,\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ActuallyCommaRegex();

    [GeneratedRegex(@"\bactually\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ActuallySpaceRegex();

    [GeneratedRegex(@"\bvery fast\b", RegexOptions.IgnoreCase)]
    private static partial Regex VeryFastRegex();

    [GeneratedRegex(@"\bgrown very fast\b", RegexOptions.IgnoreCase)]
    private static partial Regex GrownVeryFastRegex();

    [GeneratedRegex(@"\bgrew very fast\b", RegexOptions.IgnoreCase)]
    private static partial Regex GrewVeryFastRegex();

    [GeneratedRegex(@"['""“”]?actually['""“”]?\s+with\s+['""“”]?extremely['""“”]?", RegexOptions.IgnoreCase)]
    private static partial Regex ActuallyWithExtremelyRegex();

    [GeneratedRegex(@"false cognate|actualmente|in fact|currently", RegexOptions.IgnoreCase)]
    private static partial Regex FalseCognateHintRegex();

    [GeneratedRegex(@"\b(comma splice|run-on|independent clauses?|semicolon|clause boundary|subject-verb|verb tense|missing verb)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GraBoundaryRegex();

    [GeneratedRegex(@"\b(false cognate|false friend|actualmente|calque)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FalseCognateRegex();

    [GeneratedRegex(@"\b(comma splice|run-on|independent clauses?|semicolon)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CommaSpliceRegex();

    [GeneratedRegex(@"(?:^|[.!?;]\s+)(?:is|are|was|were|has|have|had|will|would|can|could)\s+(?!there\b|it\b)[a-z]", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex MissingSubjectSentenceRegex();

    [GeneratedRegex(@"\boverview\b", RegexOptions.IgnoreCase)]
    private static partial Regex OverviewRegex();

    [GeneratedRegex(@"\b(?:\d+%|(?:figures|statistics|numbers|percentages|data|from \d+)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex FigureSignalRegex();

    [GeneratedRegex(@"\b(without|no|avoid|free of|never)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NegationSignalRegex();

    [GeneratedRegex(@"\b(without|no|avoid|free of|never|rather than|instead of)\b[^.!?]{0,35}\b(?:(?:numbers|figures|statistics|percentages)\b|\d+%)", RegexOptions.IgnoreCase)]
    private static partial Regex NegationBeforeNumbersRegex();

    [GeneratedRegex("actually", RegexOptions.IgnoreCase)]
    private static partial Regex ActuallyRegex();

    [GeneratedRegex("extremely", RegexOptions.IgnoreCase)]
    private static partial Regex ExtremelyRegex();

    [GeneratedRegex(@"\bmissing subject(s|\s+pronouns?)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex MissingSubjectRegex();

    [GeneratedRegex(@"[,;]?\s*(?:and\s+|with\s+|including\s+)?(?:occasional\s+|some\s+|a\s+)?missing subject(?:s|\s+pronouns?)?", RegexOptions.IgnoreCase)]
    private static partial Regex MissingSubjectClauseRegex();

    [GeneratedRegex(@"missing subject(?:s|\s+pronouns?)?\s*(?:and|,)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex MissingSubjectPrefixRegex();

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex ParagraphSplitRegex();

    [GeneratedRegex(@"\b(Firstly|Secondly|Thirdly|Moreover|Furthermore|In a nutshell|Last but not least|On the other hand)[^.!?]{0,55}", RegexOptions.IgnoreCase)]
    private static partial Regex MechanicalLinkerRegex();

    [GeneratedRegex(@"^(Firstly|Secondly|Thirdly|Moreover|Furthermore|In a nutshell|Last but not least),?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex MechanicalPrefixRegex();

    [GeneratedRegex(@"\b(?:Actually,\s+[^.!?]{5,60}|(?:has|have|had)?\s*actually\s+(?:grown|grew|risen|rose|fallen|fell|increased)[^.!?]{0,35})", RegexOptions.IgnoreCase)]
    private static partial Regex HeuristicActuallyRegex();

    [GeneratedRegex(@"^Actually", RegexOptions.IgnoreCase)]
    private static partial Regex ActuallyPrefixRegex();

    [GeneratedRegex(@"[^.!?\n]{10,45},\s*(however|therefore|nevertheless),\s*[^.!?\n]{10,45}", RegexOptions.IgnoreCase)]
    private static partial Regex HeuristicCommaSpliceRegex();

    [GeneratedRegex(@",\s*(however|therefore|nevertheless),", RegexOptions.IgnoreCase)]
    private static partial Regex HeuristicCommaSpliceReplaceRegex();

    [GeneratedRegex(@"\b(a lot of|lots of|very big|really important|get better|nowadays people)\b[^.!?]{0,35}", RegexOptions.IgnoreCase)]
    private static partial Regex HeuristicInformalRegex();

    [GeneratedRegex(@"a lot of|lots of", RegexOptions.IgnoreCase)]
    private static partial Regex LotsOfRegex();

    [GeneratedRegex(@"very big", RegexOptions.IgnoreCase)]
    private static partial Regex VeryBigRegex();

    [GeneratedRegex(@"really important", RegexOptions.IgnoreCase)]
    private static partial Regex ReallyImportantRegex();

    [GeneratedRegex(@"\b(overall|in general|broadly|main trend)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OverviewSignalRegex();

    [GeneratedRegex(@"_{2,}")]
    private static partial Regex UnfilledGapsRegex();

    [GeneratedRegex(@"\b(firstly|secondly|moreover|also,)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MechanicalScaffoldedRegex();

    [GeneratedRegex(@"\b(firstly|secondly|moreover|in conclusion|on the other hand)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MechanicalAdvancedRegex();

    [GeneratedRegex(@"\d")]
    private static partial Regex DigitRegex();
}
