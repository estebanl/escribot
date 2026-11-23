using System.ComponentModel;
using System.Globalization;
using Escribot.Api.Data;
using Microsoft.Extensions.AI;

namespace Escribot.Api.Ai.Agents;

/// <summary>
/// Builds read-only <see cref="AITool"/> instances scoped to one authenticated user.
/// The <c>userId</c> is closed over in C# and is never exposed as a tool parameter, preserving
/// Escribot's strict Cosmos DB <c>/userId</c> tenant isolation boundary.
/// </summary>
public sealed class UserHistoryTools(IVaultRepository vaultRepo, IWritingRepository writingRepo)
{
    public IList<AITool> CreateForDrillGenerator(string userId)
    {
        [Description("Returns the student's most recent Personal Vault error fixes and academic collocations so the drill can target real recurring mistakes.")]
        async Task<string> GetRecentVaultItems(
            [Description("Maximum number of Vault items to return (1 to 10).")] int limit = 5,
            CancellationToken cancellationToken = default)
        {
            var clamped = Math.Clamp(limit, 1, 10);
            var items = await vaultRepo.ListAsync(userId, cancellationToken);
            if (items.Count == 0)
            {
                return "No Personal Vault items recorded yet.";
            }

            return string.Join(
                "\n",
                items.Take(clamped).Select(v =>
                    $"[{v.Criterion}/{v.ItemType}] \"{v.OriginalText}\" -> \"{v.UpgradedText}\" ({v.Explanation})"));
        }

        [Description("Summarizes the student's recent IELTS criterion band averages (TA_TR, CC, LR, GRA) to identify their weakest criterion.")]
        async Task<string> GetWeakestCriteria(CancellationToken cancellationToken = default)
        {
            var evals = await writingRepo.ListEvaluationsAsync(userId, cancellationToken);
            if (evals.Count == 0)
            {
                return "No graded essays recorded yet.";
            }

            var recent = evals.Take(10).ToList();
            var averages = new (string Name, double Avg)[]
            {
                ("TA_TR", recent.Average(e => e.TaTrBand)),
                ("CC", recent.Average(e => e.CcBand)),
                ("LR", recent.Average(e => e.LrBand)),
                ("GRA", recent.Average(e => e.GraBand)),
            };

            var ordered = averages.OrderBy(a => a.Avg).ToList();
            var summary = string.Join(
                ", ",
                ordered.Select(a => $"{a.Name}: {a.Avg.ToString("F2", CultureInfo.InvariantCulture)}"));
            return $"Last {recent.Count} evaluations (weakest first): {summary}.";
        }

        return
        [
            AIFunctionFactory.Create(GetRecentVaultItems),
            AIFunctionFactory.Create(GetWeakestCriteria),
        ];
    }
}
