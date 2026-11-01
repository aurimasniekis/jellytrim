using Jellytrim.Core.Domain;
using Jellytrim.Core.Policies;
using Jellytrim.Core.Storage;

namespace Jellytrim.Core.Tests;

public class AccountingTests
{
    [Fact]
    public void Same_identity_counts_once_in_unique_but_every_reference_in_logical()
    {
        var e = StorageAccounting.Estimate([new FileRef("/a", "1:1", 100, 80, 2), new FileRef("/b", "1:1", 100, 80, 2)]);
        Assert.Equal(200, e.LogicalBytes);
        Assert.Equal(100, e.UniqueBytes);
        Assert.Equal(80, e.EstimatedReclaimableBytes); // Every link is in the set.
    }

    [Fact]
    public void Hard_link_outside_the_set_reclaims_nothing()
    {
        var e = StorageAccounting.Estimate([new FileRef("/a", "1:1", 100, 80, 2)]);
        Assert.Equal(0, e.EstimatedReclaimableBytes);
        Assert.Equal(1, e.HardLinkedRetained);
    }

    [Fact]
    public void Reclaim_uses_allocated_blocks_so_sparse_files_are_not_overstated()
    {
        var e = StorageAccounting.Estimate([new FileRef("/sparse", "1:9", 1_000_000, 4096, 1)]);
        Assert.Equal(1_000_000, e.LogicalBytes);
        Assert.Equal(4096, e.EstimatedReclaimableBytes);
    }

    [Fact]
    public void Same_path_listed_twice_does_not_look_like_two_links()
    {
        var e = StorageAccounting.Estimate([new FileRef("/a", "1:1", 100, 80, 2), new FileRef("/a", "1:1", 100, 80, 2)]);
        Assert.Equal(0, e.EstimatedReclaimableBytes);
    }

    [Fact]
    public void Unknown_identity_is_counted_unverifiable_and_claims_no_reclaim()
    {
        var e = StorageAccounting.Estimate([new FileRef("/a", null, 100, 80, 1)]);
        Assert.Equal(0, e.EstimatedReclaimableBytes);
        Assert.Equal(1, e.Unverifiable);
    }

    [Theory]
    [InlineData(100, 50, 0.5, false, 0)]
    [InlineData(100, 100, 1.0, false, 0)]
    [InlineData(100, 150, 1.5, true, 50)]
    public void Budgets_are_measured_in_unique_bytes(long budget, long used, double fraction, bool exceeded, long over)
    {
        var b = Budgets.Evaluate(Guid.Empty, budget, used);
        Assert.Equal(fraction, b.UsedFraction);
        Assert.Equal(exceeded, b.Exceeded);
        Assert.Equal(over, b.OverBytes);
    }

    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static RecommendationCandidate Candidate(int lastDays, long bytes, bool eligible = true, Guid? id = null, int addedDays = 500) => new()
    {
        Item = new LeafItem { Id = id ?? Guid.NewGuid(), Kind = MediaKind.Movie, Name = "n", LibraryId = Guid.Empty, ReclaimableBytes = bytes, DateAddedUtc = Now.AddDays(-addedDays) },
        LastPlayedUtc = lastDays < 0 ? null : Now.AddDays(-lastDays),
        Evaluation = new ItemEvaluation(id ?? Guid.Empty, eligible, []),
    };

    [Fact]
    public void Older_bucket_outranks_size_and_size_breaks_ties_within_a_bucket()
    {
        var recentHuge = Candidate(10, 1000);
        var oldSmall = Candidate(400, 10);
        var oldLarge = Candidate(500, 20);
        var ranked = RecommendationRanker.Rank([recentHuge, oldSmall, oldLarge], 0, Now);
        Assert.Equal([oldLarge.Item.Id, oldSmall.Item.Id, recentHuge.Item.Id], ranked.Items.Select(i => i.ItemId));
    }

    [Fact]
    public void Ranking_is_total_and_input_order_independent()
    {
        var items = Enumerable.Range(0, 50).Select(i => Candidate(400, 10, id: Guid.NewGuid())).ToList();
        var a = RecommendationRanker.Rank(items, 0, Now).Items.Select(i => i.ItemId);
        var b = RecommendationRanker.Rank(Enumerable.Reverse(items), 0, Now).Items.Select(i => i.ItemId);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Target_stops_once_met_and_reports_shortfall()
    {
        var items = new[] { Candidate(400, 60), Candidate(400, 50), Candidate(400, 40) };
        var met = RecommendationRanker.Rank(items, 100, Now);
        Assert.True(met.TargetMet);
        Assert.Equal(2, met.Items.Count);
        Assert.Equal(110, met.SelectedBytes);

        var shortfall = RecommendationRanker.Rank(items, 1000, Now);
        Assert.False(shortfall.TargetMet);
        Assert.Equal(150, shortfall.SelectedBytes);
    }

    [Fact]
    public void Ineligible_or_zero_reclaim_items_are_never_recommended()
    {
        var blocked = Candidate(400, 100, eligible: false);
        var hardLinked = Candidate(400, 0);
        var ranked = RecommendationRanker.Rank([blocked, hardLinked], 10, Now);
        Assert.Empty(ranked.Items);
        Assert.Equal(1, ranked.SkippedNoReclaim);
    }
}
