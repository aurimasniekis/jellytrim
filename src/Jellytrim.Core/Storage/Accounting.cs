using System.Globalization;
using Jellytrim.Core.Policies;

namespace Jellytrim.Core.Storage;

/// <summary>A physical file instance in a reference set.</summary>
public sealed record FileRef(string Path, string? Identity, long Size, long AllocatedBytes, int LinkCount);

public sealed record ReclaimEstimate(long LogicalBytes, long UniqueBytes, long EstimatedReclaimableBytes, int HardLinkedRetained, int Unverifiable);

/// <summary>
/// The four measurements jellytrim keeps separate.
/// Logical: sum of st_size over every reference. Unique: st_size counted once per physical identity.
/// Estimated reclaimable: allocated blocks of files whose every hard link is in the set. Free space: observed separately per volume.
/// </summary>
public static class StorageAccounting
{
    public static ReclaimEstimate Estimate(IEnumerable<FileRef> references)
    {
        long logical = 0, unique = 0, reclaim = 0;
        int retained = 0, unverifiable = 0;
        foreach (var group in references.GroupBy(r => r.Identity ?? "path:" + r.Path, StringComparer.Ordinal))
        {
            var first = group.First();
            logical += group.Sum(r => r.Size);
            unique += first.Size;
            if (first.Identity is null)
            {
                unverifiable++;
                continue;
            }

            // Space returns only when the last link goes. A distinct path count is compared because the same path may be listed twice.
            var linksInSet = group.Select(r => r.Path).Distinct(StringComparer.Ordinal).Count();
            if (linksInSet >= first.LinkCount)
            {
                reclaim += first.AllocatedBytes;
            }
            else
            {
                retained++;
            }
        }

        return new ReclaimEstimate(logical, unique, reclaim, retained, unverifiable);
    }
}

public sealed record BudgetStatus(Guid LibraryId, long BudgetBytes, long UsedBytes, double UsedFraction, bool Exceeded, long OverBytes);

public static class Budgets
{
    /// <summary>Budget use is measured in unique bytes. Advisory only: nothing here can cause deletion.</summary>
    public static BudgetStatus Evaluate(Guid libraryId, long budgetBytes, long uniqueBytes)
    {
        var fraction = budgetBytes <= 0 ? 0 : (double)uniqueBytes / budgetBytes;
        var over = Math.Max(0, uniqueBytes - budgetBytes);
        return new BudgetStatus(libraryId, budgetBytes, uniqueBytes, fraction, over > 0, over);
    }
}

public sealed class RecommendationCandidate
{
    public required LeafItem Item { get; init; }

    public DateTime? LastPlayedUtc { get; init; }

    public required ItemEvaluation Evaluation { get; init; }
}

public sealed record Recommendation(int Rank, Guid ItemId, string Name, long ReclaimableBytes, long CumulativeBytes, DateTime? LastPlayedUtc, DateTime? DateAddedUtc, string Reason);

public sealed record RecommendationSet(long RequestedBytes, long SelectedBytes, bool TargetMet, IReadOnlyList<Recommendation> Items, int EligibleConsidered, int SkippedNoReclaim);

/// <summary>
/// Ranking, in order: longest-idle age bucket (over 365, 180, 90, 30 days, then the rest; never-timestamped last),
/// larger reclaimable first, older added first, then id. The buckets stop one huge recently-watched file from
/// outranking old content. The order is total, so it is deterministic.
/// </summary>
public static class RecommendationRanker
{
    private static readonly int[] Buckets = [365, 180, 90, 30];

    public static RecommendationSet Rank(IEnumerable<RecommendationCandidate> candidates, long requestedBytes, DateTime nowUtc, int maxItems = 1000)
    {
        var eligible = candidates.Where(c => c.Evaluation.Eligible).ToList();
        var skipped = eligible.Count(c => c.Item.ReclaimableBytes <= 0);
        var ordered = eligible
            .Where(c => c.Item.ReclaimableBytes > 0)
            .OrderBy(c => BucketOf(c.LastPlayedUtc, nowUtc))
            .ThenByDescending(c => c.Item.ReclaimableBytes)
            .ThenBy(c => c.Item.DateAddedUtc ?? DateTime.MaxValue)
            .ThenBy(c => c.Item.Id)
            .ToList();

        var result = new List<Recommendation>();
        long total = 0;
        foreach (var candidate in ordered)
        {
            if (result.Count >= maxItems || (requestedBytes > 0 && total >= requestedBytes))
            {
                break;
            }

            total += candidate.Item.ReclaimableBytes;
            result.Add(new Recommendation(result.Count + 1, candidate.Item.Id, candidate.Item.Name, candidate.Item.ReclaimableBytes, total, candidate.LastPlayedUtc, candidate.Item.DateAddedUtc, Explain(candidate, nowUtc)));
        }

        return new RecommendationSet(requestedBytes, total, requestedBytes > 0 && total >= requestedBytes, result, eligible.Count, skipped);
    }

    private static int BucketOf(DateTime? last, DateTime now)
    {
        if (last is not { } at)
        {
            return Buckets.Length + 1;
        }

        var days = (now - at).TotalDays;
        for (var i = 0; i < Buckets.Length; i++)
        {
            if (days >= Buckets[i])
            {
                return i;
            }
        }

        return Buckets.Length;
    }

    private static string Explain(RecommendationCandidate c, DateTime now)
    {
        var age = c.LastPlayedUtc is { } at
            ? string.Create(CultureInfo.InvariantCulture, $"last played {(int)(now - at).TotalDays} day(s) ago")
            : "no last-played time";
        var watched = c.Evaluation.Rules.FirstOrDefault(r => r.RuleId is RuleIds.WatchedAll or RuleIds.WatchedAny && r.Outcome == RuleOutcome.Pass)?.Message;
        return watched is null ? $"Eligible, {age}." : $"{watched} Eligible, {age}.";
    }
}
