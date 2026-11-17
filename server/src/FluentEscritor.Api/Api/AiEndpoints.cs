using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentEscritor.Api.Ai;
using FluentEscritor.Api.Auth;
using FluentEscritor.Api.Common;
using FluentEscritor.Api.Data;
using FluentEscritor.Api.Data.Models;

namespace FluentEscritor.Api.Api;

internal static class AiEndpoints
{
    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/ai/evaluate", EvaluateAsync);
        api.MapPost("/ai/outline-check", OutlineCheckAsync).OnFailure("Outline validation failed.");
        api.MapPost("/ai/toolkit", ToolkitAsync).OnFailure("Toolkit check failed.");
        api.MapPost("/ai/generate-prompt", GeneratePromptAsync).OnFailure("Failed to generate prompt.");
        api.MapGet("/ai/models", GetModelsAsync).OnFailure("Failed to fetch models");
        api.MapPost("/drills", DrillsActionAsync).OnFailure("Micro-drill request failed.");

        return api;
    }

    private static async Task<IResult> EvaluateAsync(
        JsonElement json,
        HttpContext context,
        IWritingRepository writing,
        IPromptRepository prompts,
        IVaultRepository vault,
        ImageStore imageStore,
        AiAccessGuard accessGuard,
        ExaminerService examiner,
        ErrorReporter errorReporter,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var account = context.GetSession()!.Account;
        var userId = account.Id;
        var body = new Body(json);

        var sessionId = body.Trimmed("sessionId");
        if (sessionId is null
            || !json.TryGetProperty("submissions", out var submissionsEl)
            || submissionsEl.ValueKind != JsonValueKind.Array
            || submissionsEl.GetArrayLength() == 0)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Session ID and at least one draft submission are required.");
        }

        var bundle = await writing.GetSessionBundleAsync(userId, sessionId, cancellationToken);
        if (bundle is null)
        {
            return ApiResults.Error(StatusCodes.Status404NotFound, "Writing session not found.");
        }

        var validated = new List<(EvaluateSubmissionInput Sub, Prompt Prompt)>();
        foreach (var item in submissionsEl.EnumerateArray())
        {
            var subBody = new Body(item);
            var taskType = subBody.String("taskType") == Ielts.Task1 ? Ielts.Task1 : Ielts.Task2;
            var essayText = subBody.String("essayText");
            if (string.IsNullOrWhiteSpace(essayText) || essayText.Trim().Length < 15)
            {
                return ApiResults.Error(
                    StatusCodes.Status400BadRequest,
                    $"Please write at least a short draft for {taskType.ToUpperInvariant()} before submitting.");
            }

            var promptId = subBody.Trimmed("promptId") ?? string.Empty;
            var prompt = await prompts.GetVisibleAsync(userId, promptId, cancellationToken);
            if (prompt is null)
            {
                return ApiResults.Error(StatusCodes.Status404NotFound, "Prompt not found.");
            }

            var outline = item.TryGetProperty("outline", out var outlineEl) && outlineEl.ValueKind == JsonValueKind.Object
                ? outlineEl.Deserialize<OutlineInput>(JsonExtractor.WebOptions)
                : null;
            var outlineFeedback = item.TryGetProperty("outlineFeedback", out var ofEl) && ofEl.ValueKind == JsonValueKind.Object
                ? ofEl.Deserialize<OutlineFeedback>(JsonExtractor.WebOptions)
                : null;
            var coachingHistory = item.TryGetProperty("coachingHistory", out var chEl) && chEl.ValueKind == JsonValueKind.Array
                ? chEl.Deserialize<List<ToolkitResponse>>(JsonExtractor.WebOptions)
                : null;
            var promptHighlights = item.TryGetProperty("promptHighlights", out var phEl) && phEl.ValueKind == JsonValueKind.Array
                ? phEl.Clone()
                : (JsonElement?)null;

            validated.Add((new EvaluateSubmissionInput(
                PromptId: promptId,
                TaskType: taskType,
                EssayText: essayText,
                SecondsSpent: (int)(subBody.Number("secondsSpent") ?? 0),
                OutlineJson: outlineEl.ValueKind == JsonValueKind.Object ? outlineEl.Clone() : null,
                OutlineFeedbackJson: ofEl.ValueKind == JsonValueKind.Object ? ofEl.Clone() : null,
                Outline: outline,
                OutlineFeedback: outlineFeedback,
                CoachingHistory: coachingHistory,
                PromptHighlightsJson: promptHighlights,
                ScratchNotes: subBody.String("scratchNotes")), prompt));
        }

        if (await accessGuard.GuardAsync(account, AiRequestKind.Evaluation, validated.Count, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        var totalSecondsSpent = body.Number("totalSecondsSpent") is { } tss ? (int?)tss : null;

        return new NdjsonEvaluateResult(
            async (sendEvent, ct) =>
            {
                await sendEvent(new JsonObject
                {
                    ["type"] = "progress",
                    ["stage"] = "Examiner reviewing prompt & essay...",
                });

                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var pingTask = RunKeepAlivePingsAsync(sendEvent, pingCts.Token);

                try
                {
                    var now = time.GetUtcNow();
                    var existingVault = await vault.ListAsync(userId, ct);
                    var knownOriginals = existingVault
                        .Select(v => v.OriginalText.Trim().ToLowerInvariant())
                        .ToHashSet(StringComparer.Ordinal);

                    var evaluationsByDraft = bundle.Evaluations.ToDictionary(e => e.DraftId);
                    var allDrafts = bundle.Drafts.ToList();
                    var allEvaluations = bundle.Evaluations.ToList();
                    var gradedResults = new JsonArray();

                    for (var i = 0; i < validated.Count; i++)
                    {
                        var (sub, prompt) = validated[i];
                        var taskPrefix = validated.Count > 1 ? $"[Part {i + 1}/{validated.Count}] " : string.Empty;

                        var visualContext = prompt.ChartSpec is { } cs ? cs.GetRawText()
                            : !string.IsNullOrEmpty(prompt.ExtractedVisualSpec) ? prompt.ExtractedVisualSpec
                            : null;

                        if (visualContext is null && prompt.UserId == userId && !string.IsNullOrEmpty(prompt.ImageUrl))
                        {
                            var imageForAi = await imageStore.ResolveAsDataUrlAsync(userId, prompt.ImageUrl, ct);
                            if (!string.IsNullOrEmpty(imageForAi))
                            {
                                await sendEvent(new JsonObject
                                {
                                    ["type"] = "progress",
                                    ["stage"] = $"{taskPrefix}Extracting chart data from uploaded image...",
                                });
                                visualContext = await examiner.ExtractVisualSpecFromImageAsync(imageForAi, prompt.PromptText, userId, cancellationToken: ct);
                                await prompts.SaveAsync(userId, prompt with { ExtractedVisualSpec = visualContext }, ct);
                            }
                        }

                        var evaluatedPrior = allDrafts
                            .Where(d => d.PromptId == sub.PromptId && evaluationsByDraft.ContainsKey(d.Id))
                            .OrderByDescending(d => d.Version)
                            .ToList();

                        var nextVersion = (evaluatedPrior.FirstOrDefault()?.Version ?? 0) + 1;
                        double? previousBand = evaluatedPrior.FirstOrDefault() is { } prevDraft
                            ? evaluationsByDraft[prevDraft.Id].OverallBand
                            : null;
                        var wordCount = Ielts.CountWords(sub.EssayText);

                        var evalResult = await examiner.EvaluateEssayWithAiAsync(
                            new EvaluateEssayInput(
                                UserId: userId,
                                TaskType: sub.TaskType,
                                PromptTitle: prompt.Title,
                                PromptText: prompt.PromptText,
                                VisualContext: visualContext,
                                EssayText: sub.EssayText,
                                DraftVersion: nextVersion,
                                PreviousBand: previousBand,
                                Outline: sub.Outline,
                                OutlineFeedback: sub.OutlineFeedback,
                                CoachingHistory: sub.CoachingHistory,
                                OnProgress: stage => _ = sendEvent(new JsonObject
                                {
                                    ["type"] = "progress",
                                    ["stage"] = $"{taskPrefix}{stage}",
                                })),
                            ct);

                        await sendEvent(new JsonObject
                        {
                            ["type"] = "progress",
                            ["stage"] = $"{taskPrefix}Saving band scores & marginalia...",
                        });

                        var draftId = $"draft-{Guid.NewGuid().ToString("N")[..10]}";
                        var draftRecord = new Draft
                        {
                            Id = draftId,
                            UserId = userId,
                            SessionId = sessionId,
                            PromptId = sub.PromptId,
                            TaskType = sub.TaskType,
                            Version = nextVersion,
                            Outline = sub.OutlineJson,
                            OutlineFeedback = sub.OutlineFeedbackJson,
                            EssayText = sub.EssayText,
                            WordCount = wordCount,
                            SecondsSpent = sub.SecondsSpent,
                            PromptHighlights = sub.PromptHighlightsJson,
                            ScratchNotes = string.IsNullOrEmpty(sub.ScratchNotes) ? null : sub.ScratchNotes,
                            CreatedAt = now,
                        };
                        await writing.SaveDraftAsync(userId, draftRecord, ct);
                        allDrafts.Add(draftRecord);

                        var p = evalResult.Payload;
                        var evalId = $"eval-{Guid.NewGuid().ToString("N")[..10]}";
                        var evalRecord = new Evaluation
                        {
                            Id = evalId,
                            UserId = userId,
                            SessionId = sessionId,
                            DraftId = draftId,
                            OverallBand = p.OverallBand,
                            TaTrBand = p.TaTrBand,
                            CcBand = p.CcBand,
                            LrBand = p.LrBand,
                            GraBand = p.GraBand,
                            ExaminerSummary = p.ExaminerSummary,
                            CriterionFeedback = JsonSerializer.SerializeToElement(p.Criteria, JsonExtractor.WebOptions),
                            InlineAnnotations = JsonSerializer.SerializeToElement(p.InlineAnnotations, JsonExtractor.WebOptions),
                            TargetBandRewrite = p.TargetBandRewrite,
                            RewriteKeyChanges = JsonSerializer.SerializeToElement(p.RewriteKeyChanges, JsonExtractor.WebOptions),
                            TopPriorities = JsonSerializer.SerializeToElement(p.TopPriorities, JsonExtractor.WebOptions),
                            ProviderUsed = evalResult.ProviderUsed,
                            ModelUsed = evalResult.ModelUsed,
                            CostUsd = evalResult.CostUsd,
                            CreatedAt = now,
                        };
                        await writing.SaveEvaluationAsync(userId, evalRecord, ct);
                        allEvaluations.Add(evalRecord);
                        evaluationsByDraft[draftId] = evalRecord;

                        var newVaultItems = new List<VaultItem>();
                        foreach (var v in p.VaultItems)
                        {
                            var key = v.OriginalText.Trim().ToLowerInvariant();
                            if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(v.UpgradedText) || !knownOriginals.Add(key))
                            {
                                continue;
                            }

                            newVaultItems.Add(new VaultItem
                            {
                                Id = $"vault-{Guid.NewGuid().ToString("N")[..9]}",
                                UserId = userId,
                                SourceDraftId = draftId,
                                ItemType = v.ItemType,
                                Criterion = v.Criterion,
                                OriginalText = v.OriginalText.Trim(),
                                UpgradedText = v.UpgradedText.Trim(),
                                Explanation = v.Explanation.Trim(),
                                ExampleSentence = string.IsNullOrWhiteSpace(v.ExampleSentence) ? v.UpgradedText.Trim() : v.ExampleSentence.Trim(),
                                MasteryLevel = 0,
                                ReviewCount = 0,
                                NextReviewAt = now,
                                CreatedAt = now,
                            });
                        }

                        if (newVaultItems.Count > 0)
                        {
                            await vault.SaveManyAsync(userId, newVaultItems, ct);
                        }

                        gradedResults.Add(new JsonObject
                        {
                            ["draftId"] = draftId,
                            ["version"] = nextVersion,
                            ["taskType"] = sub.TaskType,
                            ["wordCount"] = wordCount,
                            ["evaluationId"] = evalId,
                            ["providerUsed"] = evalResult.ProviderUsed,
                            ["modelUsed"] = evalResult.ModelUsed,
                            ["costUsd"] = evalResult.CostUsd,
                            ["payload"] = JsonSerializer.SerializeToNode(p, JsonExtractor.WebOptions),
                        });
                    }

                    double? t1EvalBand = null;
                    double? t2EvalBand = null;
                    foreach (var d in allDrafts.OrderByDescending(d => d.Version))
                    {
                        if (d.TaskType == Ielts.Task1 && t1EvalBand is null && evaluationsByDraft.TryGetValue(d.Id, out var e1))
                        {
                            t1EvalBand = e1.OverallBand;
                        }
                        else if (d.TaskType == Ielts.Task2 && t2EvalBand is null && evaluationsByDraft.TryGetValue(d.Id, out var e2))
                        {
                            t2EvalBand = e2.OverallBand;
                        }
                    }

                    double? weightedBand = t1EvalBand is { } b1 && t2EvalBand is { } b2
                        ? Ielts.WeightedExamBand(b1, b2)
                        : gradedResults.FirstOrDefault()?["payload"]?["overallBand"]?.GetValue<double?>();

                    await writing.SaveSessionAsync(
                        userId,
                        bundle.Session with
                        {
                            Status = "completed",
                            CompletedAt = now,
                            TotalSecondsSpent = totalSecondsSpent ?? allDrafts.Sum(d => d.SecondsSpent),
                            WeightedBand = weightedBand,
                        },
                        ct);

                    await sendEvent(new JsonObject
                    {
                        ["type"] = "result",
                        ["sessionId"] = sessionId,
                        ["weightedBand"] = weightedBand,
                        ["results"] = gradedResults,
                    });
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    await errorReporter.ReportServerErrorAsync(ex, "/api/ai/evaluate", userId);
                    var isTimeout = ex is TimeoutException || ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase);
                    await sendEvent(new JsonObject
                    {
                        ["type"] = "error",
                        ["isTimeout"] = isTimeout,
                        ["error"] = isTimeout
                            ? "Grading took too long, please try again. Your essay is safely preserved in the editor."
                            : ex.Message,
                    });
                }
                finally
                {
                    await pingCts.CancelAsync();
                    try
                    {
                        await pingTask;
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected on shutdown.
                    }
                }
            });
    }

    private static async Task RunKeepAlivePingsAsync(Func<JsonObject, Task> sendEvent, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
        while (await timer.WaitForNextTickAsync(ct))
        {
            await sendEvent(new JsonObject
            {
                ["type"] = "ping",
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        }
    }

    private static async Task<IResult> OutlineCheckAsync(
        JsonElement json,
        HttpContext context,
        IPromptRepository prompts,
        ImageStore imageStore,
        AiAccessGuard accessGuard,
        ExaminerService examiner,
        CancellationToken cancellationToken)
    {
        var account = context.GetSession()!.Account;
        var body = new Body(json);

        var promptText = body.Trimmed("promptText");
        if (!json.TryGetProperty("outline", out var outlineEl)
            || outlineEl.ValueKind != JsonValueKind.Object
            || promptText is null)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Outline fields and prompt text are required.");
        }

        if (await accessGuard.GuardAsync(account, AiRequestKind.Assist, 1, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        var outline = outlineEl.Deserialize<OutlineInput>(JsonExtractor.WebOptions) ?? new OutlineInput();
        var taskType = body.String("taskType") == Ielts.Task1 ? Ielts.Task1 : Ielts.Task2;
        var promptId = body.Trimmed("promptId");

        string? visualContext = null;
        if (promptId is not null)
        {
            var prompt = await prompts.GetVisibleAsync(account.Id, promptId, cancellationToken);
            if (prompt is not null)
            {
                if (prompt.ChartSpec is { } cs)
                {
                    visualContext = cs.GetRawText();
                }
                else if (!string.IsNullOrEmpty(prompt.ExtractedVisualSpec))
                {
                    visualContext = prompt.ExtractedVisualSpec;
                }
                else if (prompt.UserId == account.Id && !string.IsNullOrEmpty(prompt.ImageUrl))
                {
                    var imageForAi = await imageStore.ResolveAsDataUrlAsync(account.Id, prompt.ImageUrl, cancellationToken);
                    if (!string.IsNullOrEmpty(imageForAi))
                    {
                        visualContext = await examiner.ExtractVisualSpecFromImageAsync(imageForAi, prompt.PromptText, account.Id, cancellationToken: cancellationToken);
                        await prompts.SaveAsync(account.Id, prompt with { ExtractedVisualSpec = visualContext }, cancellationToken);
                    }
                }
            }
        }

        var feedback = await examiner.CheckOutlineWithAiAsync(account.Id, taskType, promptText, visualContext, outline, cancellationToken);
        return Results.Json(new { feedback }, JsonExtractor.WebOptions);
    }

    private static async Task<IResult> ToolkitAsync(
        JsonElement json,
        HttpContext context,
        AiAccessGuard accessGuard,
        ExaminerService examiner,
        CancellationToken cancellationToken)
    {
        var account = context.GetSession()!.Account;
        var body = new Body(json);

        var tool = body.Trimmed("tool");
        var selectedText = body.Trimmed("selectedText");
        var promptText = body.Trimmed("promptText");
        if (tool is null || selectedText is null || promptText is null)
        {
            return ApiResults.Error(StatusCodes.Status400BadRequest, "Select a phrase, sentence, or paragraph to inspect first.");
        }

        if (await accessGuard.GuardAsync(account, AiRequestKind.Assist, 1, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        var taskType = body.String("taskType") == Ielts.Task1 ? Ielts.Task1 : Ielts.Task2;
        var result = await examiner.RunToolkitWithAiAsync(
            account.Id,
            new ToolkitRequest(tool, taskType, promptText, selectedText, body.String("fullEssayContext")),
            cancellationToken);

        return Results.Json(new { result }, JsonExtractor.WebOptions);
    }

    private static async Task<IResult> GeneratePromptAsync(
        JsonElement json,
        HttpContext context,
        IPromptRepository prompts,
        AiAccessGuard accessGuard,
        ExaminerService examiner,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var account = context.GetSession()!.Account;
        var body = new Body(json);

        var taskType = body.String("taskType") == Ielts.Task1 ? Ielts.Task1 : Ielts.Task2;
        var category = body.Trimmed("category") ?? (taskType == Ielts.Task1 ? "line" : "opinion");
        var topic = body.String("topic");

        if (await accessGuard.GuardAsync(account, AiRequestKind.Assist, 1, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        var generated = await examiner.GeneratePromptWithAiAsync(account.Id, taskType, category, topic, cancellationToken);
        var id = $"ai-{Guid.NewGuid().ToString("N")[..8]}";
        var record = new Prompt
        {
            Id = id,
            UserId = account.Id,
            TaskType = taskType,
            Category = category,
            Topic = generated.Topic,
            Title = generated.Title,
            PromptText = generated.PromptText,
            ChartSpec = generated.ChartSpec is not null
                ? JsonSerializer.SerializeToElement(generated.ChartSpec, JsonExtractor.WebOptions)
                : null,
            ImageUrl = null,
            ExtractedVisualSpec = null,
            Source = "ai_generated",
            CreatedAt = time.GetUtcNow(),
        };

        await prompts.SaveAsync(account.Id, record, cancellationToken);
        return Results.Json(new JsonObject { ["prompt"] = Wire.Prompt(record) });
    }

    private static async Task<IResult> GetModelsAsync(
        HttpContext context,
        OpenRouterModelsClient modelsClient,
        CancellationToken cancellationToken)
    {
        var filterType = context.Request.Query["type"].ToString();
        var allModels = await modelsClient.GetModelsAsync(cancellationToken);
        var models = filterType == "vision" ? allModels.Where(m => m.HasVision).ToList() : allModels;
        return Results.Json(new { models }, JsonExtractor.WebOptions);
    }

    private static async Task<IResult> DrillsActionAsync(
        JsonElement json,
        HttpContext context,
        IDrillRepository drills,
        IVaultRepository vault,
        AiAccessGuard accessGuard,
        ExaminerService examiner,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var account = context.GetSession()!.Account;
        var body = new Body(json);
        var action = body.Trimmed("action");
        var requestedLevel = body.Trimmed("level");
        var level = Ielts.IsDrillLevel(requestedLevel) ? requestedLevel : "advanced";

        if (action == "generate")
        {
            if (await accessGuard.GuardAsync(account, AiRequestKind.Assist, 1, cancellationToken) is { } blocked)
            {
                return blocked;
            }

            var vaultRows = (await vault.ListAsync(account.Id, cancellationToken)).Take(5);
            var vaultHighlights = level == "beginner"
                ? []
                : vaultRows.Select(v => $"{v.OriginalText} -> {v.UpgradedText}").ToList();

            var drillType = body.Trimmed("drillType") ?? "cohesion";
            var challenge = await examiner.GenerateMicroDrillWithAiAsync(account.Id, drillType, level, vaultHighlights, cancellationToken);

            var id = $"drill-{Guid.NewGuid().ToString("N")[..9]}";
            var record = new MicroDrill
            {
                Id = id,
                UserId = account.Id,
                DrillType = challenge.DrillType,
                TargetCriterion = challenge.TargetCriterion,
                PromptPayload = JsonSerializer.SerializeToElement(challenge, JsonExtractor.WebOptions),
                UserResponse = null,
                Score = null,
                CreatedAt = time.GetUtcNow(),
                CompletedAt = null,
            };

            await drills.SaveAsync(account.Id, record, cancellationToken);
            return Results.Json(new { drillId = id, challenge }, JsonExtractor.WebOptions);
        }

        if (action == "score")
        {
            var userResponse = body.Trimmed("userResponse");
            if (userResponse is null)
            {
                return ApiResults.Error(StatusCodes.Status400BadRequest, "Write your drill response before checking.");
            }

            if (await accessGuard.GuardAsync(account, AiRequestKind.Assist, 1, cancellationToken) is { } blocked)
            {
                return blocked;
            }

            var challenge = json.TryGetProperty("challenge", out var chEl) && chEl.ValueKind == JsonValueKind.Object
                ? chEl.Deserialize<MicroDrillChallenge>(JsonExtractor.WebOptions) ?? new MicroDrillChallenge()
                : new MicroDrillChallenge();

            var scoreLevel = Ielts.IsDrillLevel(requestedLevel) ? requestedLevel : challenge.Level;
            var score = await examiner.ScoreMicroDrillWithAiAsync(account.Id, challenge, userResponse, scoreLevel, cancellationToken);

            var drillId = body.Trimmed("drillId");
            if (drillId is not null && await drills.GetAsync(account.Id, drillId, cancellationToken) is { } existing)
            {
                await drills.SaveAsync(
                    account.Id,
                    existing with
                    {
                        UserResponse = userResponse,
                        Score = JsonSerializer.SerializeToElement(score, JsonExtractor.WebOptions),
                        CompletedAt = time.GetUtcNow(),
                    },
                    cancellationToken);
            }

            return Results.Json(new { score }, JsonExtractor.WebOptions);
        }

        return ApiResults.Error(StatusCodes.Status400BadRequest, "Unknown drill action.");
    }

    private sealed record EvaluateSubmissionInput(
        string PromptId,
        string TaskType,
        string EssayText,
        int SecondsSpent,
        JsonElement? OutlineJson,
        JsonElement? OutlineFeedbackJson,
        OutlineInput? Outline,
        OutlineFeedback? OutlineFeedback,
        IReadOnlyList<ToolkitResponse>? CoachingHistory,
        JsonElement? PromptHighlightsJson,
        string? ScratchNotes);

    private sealed class NdjsonEvaluateResult(Func<Func<JsonObject, Task>, CancellationToken, Task> streamBody) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "application/x-ndjson; charset=utf-8";
            response.Headers.CacheControl = "no-cache, no-transform";
            response.Headers["X-Accel-Buffering"] = "no";

            using var writeLock = new SemaphoreSlim(1, 1);
            var ct = httpContext.RequestAborted;

            async Task SendEventAsync(JsonObject evt)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                await writeLock.WaitAsync(ct);
                try
                {
                    var bytes = Encoding.UTF8.GetBytes($"{evt.ToJsonString()}\n");
                    await response.Body.WriteAsync(bytes, ct);
                    await response.Body.FlushAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    // Client disconnected.
                }
                finally
                {
                    writeLock.Release();
                }
            }

            await streamBody(SendEventAsync, ct);
        }
    }
}
