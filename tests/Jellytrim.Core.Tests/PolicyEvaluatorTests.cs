using Jellytrim.Core.Abstractions;
using Jellytrim.Core.Domain;
using Jellytrim.Core.Policies;

namespace Jellytrim.Core.Tests;

public class PolicyEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Ann = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Bob = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Cat = new("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid Lib = new("11111111-0000-0000-0000-000000000000");

    private static LeafItem Movie(Guid? id = null, ItemHealth health = ItemHealth.Ok, DateTime? added = null, long size = 1000) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Kind = MediaKind.Movie,
        Name = "M",
        LibraryId = Lib,
        Health = health,
        DateAddedUtc = added ?? Now.AddDays(-400),
        LogicalBytes = size,
        Paths = ["/media/movies/m.mkv"],
    };

    private static LeafItem Ep(Guid series, Guid season, int? sNo, int? eNo) => new()
    {
        Id = Guid.NewGuid(),
        Kind = MediaKind.Episode,
        Name = $"E{sNo}x{eNo}",
        LibraryId = Lib,
        SeriesId = series,
        SeasonId = season,
        SeasonNumber = sNo,
        EpisodeNumber = eNo,
        Health = ItemHealth.Ok,
        DateAddedUtc = Now.AddDays(-400),
        LogicalBytes = 100,
    };

    private static EvaluationInput Input(IReadOnlyList<LeafItem> leaves, Dictionary<(Guid, Guid), UserItemState>? states = null, IEnumerable<Guid>? readable = null, IEnumerable<Guid>? known = null, IReadOnlySet<Guid>? protections = null, ActivePlayback? playback = null) => new()
    {
        Leaves = leaves,
        NowUtc = Now,
        KnownUsers = (known ?? [Ann, Bob]).ToList(),
        ReadableUsers = (readable ?? [Ann, Bob, Cat]).ToHashSet(),
        States = states ?? [],
        ProtectedIds = protections ?? new HashSet<Guid>(),
        Playback = playback ?? ActivePlayback.None,
    };

    private static CleanupPolicy Policy(params Guid[] users) => new() { Name = "p", ParticipatingUserIds = [.. users] };

    private static UserItemState Played(int daysAgo = 100) => new(WatchState.Played, 0, 1, Now.AddDays(-daysAgo));

    private static ItemEvaluation Eval(CleanupPolicy policy, EvaluationInput input, Guid id) => PolicyEvaluator.Evaluate(policy, input).Items[id];

    private static RuleOutcome Outcome(ItemEvaluation e, string rule) => e.Rules.Single(r => r.RuleId == rule).Outcome;

    [Fact]
    public void Empty_user_set_never_counts_as_watched_by_everyone()
    {
        var m = Movie();
        var e = Eval(Policy(), Input([m]), m.Id);
        Assert.False(e.Eligible);
        Assert.Equal(RuleOutcome.Block, Outcome(e, RuleIds.Users));
        Assert.Equal(RuleOutcome.Block, Outcome(e, RuleIds.WatchedAll));
    }

    [Fact]
    public void Participants_entirely_excluded_is_an_empty_set()
    {
        var m = Movie();
        var p = Policy(Ann);
        p.ExcludedUserIds = [Ann];
        Assert.False(Eval(p, Input([m], new() { [(m.Id, Ann)] = Played() }), m.Id).Eligible);
    }

    [Fact]
    public void Watched_by_all_requires_every_selected_user()
    {
        var m = Movie();
        var some = new Dictionary<(Guid, Guid), UserItemState> { [(m.Id, Ann)] = Played() };
        Assert.False(Eval(Policy(Ann, Bob), Input([m], some), m.Id).Eligible);
        some[(m.Id, Bob)] = Played();
        Assert.True(Eval(Policy(Ann, Bob), Input([m], some), m.Id).Eligible);
    }

    [Fact]
    public void Watched_by_any_needs_one_played()
    {
        var m = Movie();
        var p = Policy(Ann, Bob);
        p.Watched = WatchedRequirement.AnySelected;
        Assert.False(Eval(p, Input([m]), m.Id).Eligible);
        Assert.True(Eval(p, Input([m], new() { [(m.Id, Bob)] = Played() }), m.Id).Eligible);
    }

    [Fact]
    public void Unreadable_user_fails_closed_for_all_even_if_others_played()
    {
        var m = Movie();
        var e = Eval(Policy(Ann, Bob), Input([m], new() { [(m.Id, Ann)] = Played() }, readable: [Ann]), m.Id);
        Assert.False(e.Eligible);
        Assert.Equal(RuleOutcome.Unknown, Outcome(e, RuleIds.WatchedAll));
    }

    [Fact]
    public void Kleene_all_a_definite_no_outranks_unknown()
    {
        var m = Movie();
        var e = Eval(Policy(Ann, Bob, Cat), Input([m], new() { [(m.Id, Ann)] = Played() }, readable: [Ann, Bob]), m.Id);
        Assert.Equal(RuleOutcome.Block, Outcome(e, RuleIds.WatchedAll));
    }

    [Fact]
    public void Deleted_selected_user_with_any_mode_passes_watched_but_protections_still_fail_closed()
    {
        // Ann played it, and Cat was deleted, so Cat is unreadable. "Any" is true, but in-progress and favorite are not decidable for Cat.
        var m = Movie();
        var p = Policy(Ann, Cat);
        p.Watched = WatchedRequirement.AnySelected;
        p.FavoriteProtection = UserProtectionScope.SelectedUsers;
        p.InProgressProtection = UserProtectionScope.SelectedUsers;
        var e = Eval(p, Input([m], new() { [(m.Id, Ann)] = Played() }, readable: [Ann]), m.Id);
        Assert.Equal(RuleOutcome.Pass, Outcome(e, RuleIds.WatchedAny));
        Assert.Equal(RuleOutcome.Unknown, Outcome(e, RuleIds.Favorite));
        Assert.Equal(RuleOutcome.Unknown, Outcome(e, RuleIds.InProgress));
        Assert.False(e.Eligible);
    }

    [Fact]
    public void User_without_library_access_is_unknown_not_unplayed()
    {
        var m = Movie();
        var input = new EvaluationInput
        {
            Leaves = [m],
            NowUtc = Now,
            KnownUsers = [Ann],
            ReadableUsers = new HashSet<Guid> { Ann },
            States = new Dictionary<(Guid, Guid), UserItemState>(),
            UserCanAccessLibrary = (_, _) => false,
        };
        Assert.Equal(RuleOutcome.Unknown, Outcome(Eval(Policy(Ann), input, m.Id), RuleIds.WatchedAll));
    }

    [Fact]
    public void Partial_state_blocks_through_in_progress_protection()
    {
        var m = Movie();
        var p = Policy(Ann);
        var e = Eval(p, Input([m], new() { [(m.Id, Ann)] = Played(), [(m.Id, Bob)] = new UserItemState(WatchState.Partial, 500) }), m.Id);
        Assert.Equal(RuleOutcome.Block, Outcome(e, RuleIds.InProgress));
        Assert.False(e.Eligible);
    }

    [Fact]
    public void In_progress_of_excluded_user_is_ignored_only_when_scope_is_selected_users()
    {
        var m = Movie();
        var p = Policy(Ann);
        p.InProgressProtection = UserProtectionScope.SelectedUsers;
        p.FavoriteProtection = UserProtectionScope.SelectedUsers;
        Assert.True(Eval(p, Input([m], new() { [(m.Id, Ann)] = Played(), [(m.Id, Bob)] = new UserItemState(WatchState.Partial, 500) }), m.Id).Eligible);
    }

    [Theory]
    [InlineData(UserProtectionScope.AllKnownUsers, false)]
    [InlineData(UserProtectionScope.SelectedUsers, true)]
    [InlineData(UserProtectionScope.None, true)]
    public void Favorite_scope_semantics(UserProtectionScope scope, bool eligible)
    {
        var m = Movie();
        var p = Policy(Ann);
        p.FavoriteProtection = scope;
        var states = new Dictionary<(Guid, Guid), UserItemState> { [(m.Id, Ann)] = Played(), [(m.Id, Bob)] = new UserItemState(WatchState.Unplayed, IsFavorite: true) };
        Assert.Equal(eligible, Eval(p, Input([m], states), m.Id).Eligible);
    }

    [Fact]
    public void Favorite_on_series_or_season_propagates_to_episodes()
    {
        Guid series = Guid.NewGuid(), season = Guid.NewGuid();
        var e = Ep(series, season, 1, 1);
        foreach (var holder in new[] { series, season })
        {
            var states = new Dictionary<(Guid, Guid), UserItemState> { [(e.Id, Ann)] = Played(), [(holder, Bob)] = new UserItemState(WatchState.Unplayed, IsFavorite: true) };
            var result = Eval(Policy(Ann), Input([e], states), e.Id);
            Assert.Equal(RuleOutcome.Block, Outcome(result, RuleIds.Favorite));
        }
    }

    [Fact]
    public void Protection_overrides_every_passing_criterion_at_every_level()
    {
        Guid series = Guid.NewGuid(), season = Guid.NewGuid();
        var e = Ep(series, season, 1, 1);
        foreach (var id in new[] { e.Id, season, series, Lib })
        {
            var result = Eval(Policy(Ann), Input([e], new() { [(e.Id, Ann)] = Played() }, protections: new HashSet<Guid> { id }), e.Id);
            Assert.False(result.Eligible);
            Assert.Equal(RuleOutcome.Block, Outcome(result, RuleIds.Explicit));
            Assert.Equal(RuleOutcome.Pass, Outcome(result, RuleIds.WatchedAll));
        }
    }

    [Fact]
    public void Active_playback_blocks_by_id_and_by_path()
    {
        var m = Movie();
        var states = new Dictionary<(Guid, Guid), UserItemState> { [(m.Id, Ann)] = Played(), [(m.Id, Bob)] = Played() };
        Assert.False(Eval(Policy(Ann, Bob), Input([m], states, playback: new ActivePlayback(new HashSet<Guid> { m.Id }, new HashSet<string>())), m.Id).Eligible);
        Assert.False(Eval(Policy(Ann, Bob), Input([m], states, playback: new ActivePlayback(new HashSet<Guid>(), new HashSet<string> { m.Paths[0] })), m.Id).Eligible);
    }

    [Theory]
    [InlineData(ItemHealth.Missing, RuleOutcome.Block)]
    [InlineData(ItemHealth.NotFileBacked, RuleOutcome.Block)]
    [InlineData(ItemHealth.Unavailable, RuleOutcome.Unknown)]
    public void Unhealthy_items_are_never_eligible(ItemHealth health, RuleOutcome outcome)
    {
        var m = Movie(health: health);
        var e = Eval(Policy(Ann), Input([m], new() { [(m.Id, Ann)] = Played(), [(m.Id, Bob)] = Played() }), m.Id);
        Assert.False(e.Eligible);
        Assert.Equal(outcome, Outcome(e, RuleIds.HealthAvailable));
    }

    [Fact]
    public void Last_watched_uses_most_recent_across_selected_users_with_exact_day_boundary()
    {
        var m = Movie();
        var p = Policy(Ann, Bob);
        p.MinDaysSinceLastWatched = 30;
        var states = new Dictionary<(Guid, Guid), UserItemState> { [(m.Id, Ann)] = Played(400), [(m.Id, Bob)] = new UserItemState(WatchState.Played, 0, 1, Now.AddDays(-30)) };
        Assert.Equal(RuleOutcome.Pass, Outcome(Eval(p, Input([m], states), m.Id), RuleIds.LastWatchedAge));

        states[(m.Id, Bob)] = new UserItemState(WatchState.Played, 0, 1, Now.AddDays(-30).AddSeconds(1));
        Assert.Equal(RuleOutcome.Block, Outcome(Eval(p, Input([m], states), m.Id), RuleIds.LastWatchedAge));
    }

    [Fact]
    public void Missing_timestamp_on_a_played_user_is_unknown_unless_a_recent_play_decides()
    {
        var m = Movie();
        var p = Policy(Ann, Bob);
        p.MinDaysSinceLastWatched = 30;
        var states = new Dictionary<(Guid, Guid), UserItemState> { [(m.Id, Ann)] = Played(400), [(m.Id, Bob)] = new UserItemState(WatchState.Played, 0, 1) };
        Assert.Equal(RuleOutcome.Unknown, Outcome(Eval(p, Input([m], states), m.Id), RuleIds.LastWatchedAge));

        states[(m.Id, Ann)] = Played(1);
        Assert.Equal(RuleOutcome.Block, Outcome(Eval(p, Input([m], states), m.Id), RuleIds.LastWatchedAge));
    }

    [Fact]
    public void Missing_date_added_fails_closed_for_both_date_rules()
    {
        var leaf = new LeafItem { Id = Guid.NewGuid(), Kind = MediaKind.Movie, Name = "n", LibraryId = Lib, Health = ItemHealth.Ok };
        var p = Policy(Ann);
        p.MinDaysSinceAdded = 10;
        p.KeepAddedWithinDays = 10;
        var e = Eval(p, Input([leaf], new() { [(leaf.Id, Ann)] = Played(), [(leaf.Id, Bob)] = Played() }), leaf.Id);
        Assert.Equal(RuleOutcome.Unknown, Outcome(e, RuleIds.AddedAge));
        Assert.Equal(RuleOutcome.Unknown, Outcome(e, RuleIds.KeepRecentlyAdded));
    }

    [Fact]
    public void Added_boundaries_are_exact_and_timezone_independent()
    {
        var p = Policy(Ann);
        p.KeepAddedWithinDays = 7;
        p.MinDaysSinceAdded = 7;
        var exact = Movie(added: Now.AddDays(-7));
        var inside = Movie(added: Now.AddDays(-7).AddTicks(1));
        var states = new Dictionary<(Guid, Guid), UserItemState> { [(exact.Id, Ann)] = Played(), [(inside.Id, Ann)] = Played() };
        var input = Input([exact, inside], states);
        Assert.Equal(RuleOutcome.Pass, Outcome(Eval(p, input, exact.Id), RuleIds.KeepRecentlyAdded));
        Assert.Equal(RuleOutcome.Pass, Outcome(Eval(p, input, exact.Id), RuleIds.AddedAge));
        Assert.Equal(RuleOutcome.Block, Outcome(Eval(p, input, inside.Id), RuleIds.KeepRecentlyAdded));
        Assert.Equal(RuleOutcome.Block, Outcome(Eval(p, input, inside.Id), RuleIds.AddedAge));
    }

    [Fact]
    public void Min_size_tags_paths_libraries_and_kinds()
    {
        var m = Movie(size: 1000);
        var played = new Dictionary<(Guid, Guid), UserItemState> { [(m.Id, Ann)] = Played(), [(m.Id, Bob)] = Played() };
        var p = Policy(Ann);
        p.MinSizeBytes = 1000;
        Assert.True(Eval(p, Input([m], played), m.Id).Eligible);
        p.MinSizeBytes = 1001;
        Assert.False(Eval(p, Input([m], played), m.Id).Eligible);

        var excluded = Policy(Ann);
        excluded.ExcludedPathPrefixes = ["/media/movies"];
        Assert.Equal(RuleOutcome.Block, Outcome(Eval(excluded, Input([m], played), m.Id), RuleIds.ExcludedPath));

        var sibling = Policy(Ann);
        sibling.ExcludedPathPrefixes = ["/media/mov"]; // A prefix must end on a path boundary.
        Assert.Equal(RuleOutcome.NotApplicable, Outcome(Eval(sibling, Input([m], played), m.Id), RuleIds.ExcludedPath));

        var lib = Policy(Ann);
        lib.ExcludedLibraryIds = [Lib];
        Assert.False(Eval(lib, Input([m], played), m.Id).Eligible);

        var scope = Policy(Ann);
        scope.IncludedLibraryIds = [Guid.NewGuid()];
        Assert.False(Eval(scope, Input([m], played), m.Id).Eligible);

        var kind = Policy(Ann);
        kind.MediaKinds = [MediaKind.Episode];
        Assert.False(Eval(kind, Input([m], played), m.Id).Eligible);

        var tagged = new LeafItem { Id = m.Id, Kind = MediaKind.Movie, Name = "n", LibraryId = Lib, Health = ItemHealth.Ok, Tags = ["keep"], DateAddedUtc = Now.AddDays(-400) };
        var tagPolicy = Policy(Ann);
        tagPolicy.ExcludedTags = ["KEEP"];
        Assert.False(Eval(tagPolicy, Input([tagged], played), m.Id).Eligible);
    }

    [Fact]
    public void Keep_newest_orders_by_season_then_episode_and_ignores_specials()
    {
        Guid show = Guid.NewGuid(), s0 = Guid.NewGuid(), s1 = Guid.NewGuid(), s2 = Guid.NewGuid();
        var special = Ep(show, s0, 0, 9);
        var e11 = Ep(show, s1, 1, 1);
        var e12 = Ep(show, s1, 1, 12);
        var e21 = Ep(show, s2, 2, 1);
        var p = Policy(Ann);
        p.KeepNewestEpisodesPerShow = 2;
        var leaves = new[] { special, e11, e12, e21 };
        var states = leaves.ToDictionary(l => (l.Id, Ann), _ => Played());
        var input = Input(leaves, states, known: [Ann]);
        Assert.False(Eval(p, input, e21.Id).Eligible);
        Assert.False(Eval(p, input, e12.Id).Eligible);
        Assert.True(Eval(p, input, e11.Id).Eligible);
        Assert.True(Eval(p, input, special.Id).Eligible);
    }

    [Fact]
    public void Keep_newest_is_unknown_for_the_show_when_any_episode_is_unnumbered()
    {
        Guid show = Guid.NewGuid(), s1 = Guid.NewGuid();
        var numbered = Ep(show, s1, 1, 1);
        var unnumbered = Ep(show, s1, 1, null);
        var p = Policy(Ann);
        p.KeepNewestEpisodesPerShow = 1;
        var states = new[] { numbered, unnumbered }.ToDictionary(l => (l.Id, Ann), _ => Played());
        var e = Eval(p, Input([numbered, unnumbered], states, known: [Ann]), numbered.Id);
        Assert.Equal(RuleOutcome.Unknown, Outcome(e, RuleIds.KeepNewestEpisodes));
        Assert.False(e.Eligible);
    }

    [Fact]
    public void Complete_season_requires_every_episode_and_is_independent_per_season()
    {
        Guid show = Guid.NewGuid(), s1 = Guid.NewGuid(), s2 = Guid.NewGuid();
        var a = Ep(show, s1, 1, 1);
        var b = Ep(show, s1, 1, 2);
        var c = Ep(show, s2, 2, 1);
        var p = Policy(Ann);
        p.Unit = CleanupUnit.CompleteSeason;
        var states = new Dictionary<(Guid, Guid), UserItemState> { [(a.Id, Ann)] = Played(), [(c.Id, Ann)] = Played() };
        var result = PolicyEvaluator.Evaluate(p, Input([a, b, c], states, known: [Ann]));
        Assert.False(result.Items[a.Id].Eligible);
        Assert.Equal(RuleOutcome.Block, Outcome(result.Items[a.Id], RuleIds.UnitComplete));
        Assert.True(result.Items[c.Id].Eligible);
    }

    [Fact]
    public void Complete_show_is_blocked_by_one_unavailable_episode()
    {
        Guid show = Guid.NewGuid(), s1 = Guid.NewGuid();
        var a = Ep(show, s1, 1, 1);
        var offline = new LeafItem { Id = Guid.NewGuid(), Kind = MediaKind.Episode, Name = "x", LibraryId = Lib, SeriesId = show, SeasonId = s1, SeasonNumber = 1, EpisodeNumber = 2, Health = ItemHealth.Unavailable };
        var p = Policy(Ann);
        p.Unit = CleanupUnit.CompleteShow;
        var states = new Dictionary<(Guid, Guid), UserItemState> { [(a.Id, Ann)] = Played(), [(offline.Id, Ann)] = Played() };
        Assert.False(Eval(p, Input([a, offline], states, known: [Ann]), a.Id).Eligible);
    }

    [Fact]
    public void Virtual_episodes_do_not_block_a_complete_season()
    {
        Guid show = Guid.NewGuid(), s1 = Guid.NewGuid();
        var a = Ep(show, s1, 1, 1);
        var missing = new LeafItem { Id = Guid.NewGuid(), Kind = MediaKind.Episode, Name = "v", LibraryId = Lib, SeriesId = show, SeasonId = s1, SeasonNumber = 1, EpisodeNumber = 2, Health = ItemHealth.NotFileBacked };
        var p = Policy(Ann);
        p.Unit = CleanupUnit.CompleteSeason;
        Assert.True(Eval(p, Input([a, missing], new() { [(a.Id, Ann)] = Played() }, known: [Ann]), a.Id).Eligible);
    }

    [Fact]
    public void Every_rule_runs_so_every_reason_is_reported()
    {
        var m = Movie();
        var p = Policy(Ann);
        p.MinSizeBytes = long.MaxValue;
        var e = Eval(p, Input([m], protections: new HashSet<Guid> { m.Id }), m.Id);
        var blocking = e.Blocking.Select(b => b.RuleId).ToList();
        Assert.Contains(RuleIds.Explicit, blocking);
        Assert.Contains(RuleIds.WatchedAll, blocking);
        Assert.Contains(RuleIds.MinSize, blocking);
    }

    [Fact]
    public void Evaluation_is_deterministic()
    {
        Guid show = Guid.NewGuid(), s1 = Guid.NewGuid();
        var leaves = Enumerable.Range(1, 40).Select(i => Ep(show, s1, 1, i % 7)).ToList();
        var p = Policy(Ann);
        p.KeepNewestEpisodesPerShow = 5;
        var first = PolicyEvaluator.Evaluate(p, Input(leaves, known: [Ann]));
        var second = PolicyEvaluator.Evaluate(p, Input(Enumerable.Reverse(leaves).ToList(), known: [Ann]));
        foreach (var leaf in leaves)
        {
            Assert.Equal(first.Items[leaf.Id].Rules, second.Items[leaf.Id].Rules);
        }
    }

    [Fact]
    public void Manual_selection_needs_no_watch_state_but_keeps_protections()
    {
        var m = Movie();
        var manual = CleanupPolicy.ManualSelection();
        Assert.True(Eval(manual, Input([m]), m.Id).Eligible);
        Assert.False(Eval(manual, Input([m], new() { [(m.Id, Bob)] = new UserItemState(WatchState.Unplayed, IsFavorite: true) }), m.Id).Eligible);
        Assert.False(Eval(manual, Input([m], new() { [(m.Id, Bob)] = new UserItemState(WatchState.Partial, 10) }), m.Id).Eligible);
        Assert.False(Eval(manual, Input([m], readable: [Ann]), m.Id).Eligible);
    }

    [Fact]
    public void Validation_rejects_unsafe_policy_documents()
    {
        Assert.Contains(Policy().Validate(), v => v.Contains("participating"));
        var notRequired = Policy(Ann);
        notRequired.Watched = WatchedRequirement.NotRequired;
        Assert.NotEmpty(notRequired.Validate());
        var noInProgress = Policy(Ann);
        noInProgress.InProgressProtection = UserProtectionScope.None;
        Assert.NotEmpty(noInProgress.Validate());
        var relative = Policy(Ann);
        relative.ExcludedPathPrefixes = ["media/x"];
        Assert.NotEmpty(relative.Validate());
        Assert.Empty(Policy(Ann).Validate());
    }
}
