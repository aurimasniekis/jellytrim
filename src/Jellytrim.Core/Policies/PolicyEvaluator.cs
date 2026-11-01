using System.Globalization;
using System.Text.Json.Serialization;
using Jellytrim.Core.Abstractions;
using Jellytrim.Core.Domain;

namespace Jellytrim.Core.Policies;

[JsonConverter(typeof(JsonStringEnumConverter<RuleOutcome>))]
public enum RuleOutcome
{
    /// <summary>The rule allows deletion.</summary>
    Pass,

    /// <summary>The rule forbids deletion.</summary>
    Block,

    /// <summary>The rule could not be decided. Treated exactly like Block for eligibility.</summary>
    Unknown,

    /// <summary>The rule is not configured or does not apply to this item.</summary>
    NotApplicable,
}

[JsonConverter(typeof(JsonStringEnumConverter<RuleKind>))]
public enum RuleKind { Criterion, Protection, Availability }

/// <summary>Stable rule identifiers exposed through the API and used by the UI for labels and counts.</summary>
public static class RuleIds
{
    public const string HealthAvailable = "health.available";
    public const string ActivePlayback = "protect.activePlayback";
    public const string Explicit = "protect.explicit";
    public const string Favorite = "protect.favorite";
    public const string InProgress = "protect.inProgress";
    public const string KeepRecentlyAdded = "protect.keepRecentlyAdded";
    public const string KeepNewestEpisodes = "protect.keepNewestEpisodes";
    public const string ExcludedLibrary = "protect.excludedLibrary";
    public const string ExcludedPath = "protect.excludedPath";
    public const string ExcludedTag = "protect.excludedTag";
    public const string ScopeLibrary = "scope.library";
    public const string ScopeKind = "scope.mediaKind";
    public const string Users = "users.configured";
    public const string WatchedAll = "watched.all";
    public const string WatchedAny = "watched.any";
    public const string LastWatchedAge = "lastWatched.age";
    public const string AddedAge = "added.age";
    public const string MinSize = "size.min";
    public const string UnitComplete = "unit.complete";
}

public sealed record RuleEvaluation(string RuleId, RuleKind Kind, RuleOutcome Outcome, string Message);

public sealed record ItemEvaluation(Guid ItemId, bool Eligible, IReadOnlyList<RuleEvaluation> Rules)
{
    public IEnumerable<RuleEvaluation> Blocking => Rules.Where(r => r.Outcome is RuleOutcome.Block or RuleOutcome.Unknown);
}

/// <summary>The evaluator's view of one movie or episode.</summary>
public sealed class LeafItem
{
    public required Guid Id { get; init; }

    public required MediaKind Kind { get; init; }

    public required string Name { get; init; }

    public required Guid LibraryId { get; init; }

    public Guid? SeriesId { get; init; }

    public Guid? SeasonId { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public DateTime? DateAddedUtc { get; init; }

    public long LogicalBytes { get; init; }

    public long ReclaimableBytes { get; init; }

    public ItemHealth Health { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<string> Paths { get; init; } = [];
}

/// <summary>
/// Immutable facts for one evaluation. Sparse states: a user in <see cref="ReadableUsers"/> with no row is
/// Unplayed and not favorite. A user not in <see cref="ReadableUsers"/> is Unknown on every item.
/// </summary>
public sealed class EvaluationInput
{
    public required IReadOnlyList<LeafItem> Leaves { get; init; }

    public required DateTime NowUtc { get; init; }

    public required IReadOnlyCollection<Guid> KnownUsers { get; init; }

    public required IReadOnlySet<Guid> ReadableUsers { get; init; }

    public required IReadOnlyDictionary<(Guid ItemId, Guid UserId), UserItemState> States { get; init; }

    /// <summary>Items, seasons, series and libraries with an explicit protection.</summary>
    public IReadOnlySet<Guid> ProtectedIds { get; init; } = new HashSet<Guid>();

    public ActivePlayback Playback { get; init; } = ActivePlayback.None;

    /// <summary>Per-library users without access, whose state is reported as Unknown.</summary>
    public Func<Guid, Guid, bool> UserCanAccessLibrary { get; init; } = (_, _) => true;

    /// <summary>Sets false for execution-time re-evaluation of a single item, where sibling state is not live.</summary>
    public bool EvaluateUnit { get; init; } = true;

    public StringComparison PathComparison { get; init; } = StringComparison.Ordinal;
}

public sealed class EvaluationResult
{
    public required IReadOnlyDictionary<Guid, ItemEvaluation> Items { get; init; }

    public int EligibleCount => Items.Values.Count(i => i.Eligible);
}

/// <summary>
/// Deterministic, side-effect-free rule evaluation. Every rule always runs, so the UI can explain every
/// reason rather than the first one. Unknown fails closed: an item is eligible only if no rule is Block or Unknown.
/// </summary>
public static class PolicyEvaluator
{
    public static EvaluationResult Evaluate(CleanupPolicy policy, EvaluationInput input)
    {
        var users = policy.EffectiveUsers;
        var keepNewest = BuildKeepNewest(policy, input);
        var results = new Dictionary<Guid, ItemEvaluation>(input.Leaves.Count);
        foreach (var leaf in input.Leaves)
        {
            var rules = EvaluateLeaf(policy, users, input, leaf, keepNewest);
            results[leaf.Id] = new ItemEvaluation(leaf.Id, rules.All(IsNotBlocking), rules);
        }

        if (input.EvaluateUnit && policy.Unit != CleanupUnit.Item)
        {
            ApplyUnit(policy, input, results);
        }

        return new EvaluationResult { Items = results };
    }

    private static bool IsNotBlocking(RuleEvaluation r) => r.Outcome is RuleOutcome.Pass or RuleOutcome.NotApplicable;

    private static List<RuleEvaluation> EvaluateLeaf(CleanupPolicy policy, IReadOnlyList<Guid> users, EvaluationInput input, LeafItem leaf, Dictionary<Guid, RuleEvaluation> keepNewest)
    {
        var rules = new List<RuleEvaluation>(20);

        // Availability
        rules.Add(leaf.Health switch
        {
            ItemHealth.Ok => Rule(RuleIds.HealthAvailable, RuleKind.Availability, RuleOutcome.Pass, "All referenced files were observed."),
            ItemHealth.Missing => Rule(RuleIds.HealthAvailable, RuleKind.Availability, RuleOutcome.Block, "A referenced file is missing while its root is online. This is a stale entry with nothing to delete."),
            ItemHealth.NotFileBacked => Rule(RuleIds.HealthAvailable, RuleKind.Availability, RuleOutcome.Block, "This item has no local file (virtual or remote)."),
            _ => Rule(RuleIds.HealthAvailable, RuleKind.Availability, RuleOutcome.Unknown, "Storage for this item was unavailable at the last scan, so its files could not be verified."),
        });

        // Protections
        rules.Add(leaf.Id is var id && input.Playback.ItemIds.Contains(id) || leaf.Paths.Any(input.Playback.Paths.Contains)
            ? Rule(RuleIds.ActivePlayback, RuleKind.Protection, RuleOutcome.Block, "Someone is playing this right now.")
            : Rule(RuleIds.ActivePlayback, RuleKind.Protection, RuleOutcome.Pass, "Not in active playback when evaluated."));
        rules.Add(ExplicitProtection(input, leaf));
        rules.Add(Favorite(policy, users, input, leaf));
        rules.Add(InProgress(policy, users, input, leaf));
        rules.Add(KeepRecentlyAdded(policy, input, leaf));
        if (keepNewest.Count > 0 && keepNewest.TryGetValue(leaf.Id, out var keep))
        {
            rules.Add(keep);
        }
        else
        {
            rules.Add(Rule(RuleIds.KeepNewestEpisodes, RuleKind.Protection, RuleOutcome.NotApplicable, policy.KeepNewestEpisodesPerShow is null ? "Not configured." : "Does not apply to this item."));
        }

        rules.Add(policy.ExcludedLibraryIds.Contains(leaf.LibraryId)
            ? Rule(RuleIds.ExcludedLibrary, RuleKind.Protection, RuleOutcome.Block, "The library is excluded by this policy.")
            : Rule(RuleIds.ExcludedLibrary, RuleKind.Protection, RuleOutcome.NotApplicable, "Library is not excluded."));
        rules.Add(ExcludedPath(policy, input, leaf));
        rules.Add(ExcludedTag(policy, leaf));

        // Scope
        rules.Add(policy.IncludedLibraryIds.Count > 0 && !policy.IncludedLibraryIds.Contains(leaf.LibraryId)
            ? Rule(RuleIds.ScopeLibrary, RuleKind.Criterion, RuleOutcome.Block, "The library is outside this policy's scope.")
            : Rule(RuleIds.ScopeLibrary, RuleKind.Criterion, RuleOutcome.Pass, "Library is in scope."));
        rules.Add(policy.MediaKinds.Contains(leaf.Kind)
            ? Rule(RuleIds.ScopeKind, RuleKind.Criterion, RuleOutcome.Pass, $"{leaf.Kind} is in scope.")
            : Rule(RuleIds.ScopeKind, RuleKind.Criterion, RuleOutcome.Block, $"{leaf.Kind} is outside this policy's scope."));

        // Watched criteria
        if (policy.Watched != WatchedRequirement.NotRequired)
        {
            rules.Add(users.Count == 0
                ? Rule(RuleIds.Users, RuleKind.Criterion, RuleOutcome.Block, "No users are selected. An empty set never counts as watched by everyone.")
                : Rule(RuleIds.Users, RuleKind.Criterion, RuleOutcome.Pass, $"{users.Count} effective user(s)."));
            rules.Add(Watched(policy.Watched, users, input, leaf));
        }

        rules.Add(LastWatched(policy, users, input, leaf));
        rules.Add(AddedAge(policy, input, leaf));
        rules.Add(policy.MinSizeBytes is { } min
            ? leaf.LogicalBytes >= min
                ? Rule(RuleIds.MinSize, RuleKind.Criterion, RuleOutcome.Pass, $"Size {leaf.LogicalBytes} bytes meets the minimum of {min}.")
                : Rule(RuleIds.MinSize, RuleKind.Criterion, RuleOutcome.Block, $"Size {leaf.LogicalBytes} bytes is below the minimum of {min}.")
            : Rule(RuleIds.MinSize, RuleKind.Criterion, RuleOutcome.NotApplicable, "Not configured."));
        return rules;
    }

    private static RuleEvaluation Rule(string id, RuleKind kind, RuleOutcome outcome, string message) => new(id, kind, outcome, message);

    /// <summary>Reads state, mapping an unreadable user or one without library access to Unknown.</summary>
    private static UserItemState StateOf(EvaluationInput input, Guid itemId, Guid userId, Guid libraryId)
    {
        if (!input.ReadableUsers.Contains(userId) || !input.UserCanAccessLibrary(userId, libraryId))
        {
            return UserItemState.Unknown;
        }

        return input.States.TryGetValue((itemId, userId), out var state) ? state : UserItemState.Default;
    }

    private static RuleEvaluation ExplicitProtection(EvaluationInput input, LeafItem leaf)
    {
        var scope = input.ProtectedIds.Contains(leaf.Id) ? "item"
            : leaf.SeasonId is { } season && input.ProtectedIds.Contains(season) ? "season"
            : leaf.SeriesId is { } series && input.ProtectedIds.Contains(series) ? "show"
            : input.ProtectedIds.Contains(leaf.LibraryId) ? "library"
            : null;
        return scope is null
            ? Rule(RuleIds.Explicit, RuleKind.Protection, RuleOutcome.Pass, "No explicit protection.")
            : Rule(RuleIds.Explicit, RuleKind.Protection, RuleOutcome.Block, $"Explicitly protected at {scope} level.");
    }

    private static RuleEvaluation Favorite(CleanupPolicy policy, IReadOnlyList<Guid> users, EvaluationInput input, LeafItem leaf)
    {
        if (policy.FavoriteProtection == UserProtectionScope.None)
        {
            return Rule(RuleIds.Favorite, RuleKind.Protection, RuleOutcome.NotApplicable, "Favorite protection is off for this policy.");
        }

        var scope = policy.FavoriteProtection == UserProtectionScope.SelectedUsers ? users : (IReadOnlyCollection<Guid>)input.KnownUsers;
        var unknown = false;
        foreach (var user in scope)
        {
            // Favorites propagate downward: a favorited show or season protects its episodes.
            foreach (var id in Ancestry(leaf))
            {
                var state = StateOf(input, id, user, leaf.LibraryId);
                if (state.State == WatchState.Unknown)
                {
                    unknown = true;
                }
                else if (state.IsFavorite)
                {
                    return Rule(RuleIds.Favorite, RuleKind.Protection, RuleOutcome.Block, id == leaf.Id ? "Favorited by a user in scope." : "Its show or season is favorited by a user in scope.");
                }
            }
        }

        return unknown
            ? Rule(RuleIds.Favorite, RuleKind.Protection, RuleOutcome.Unknown, "Favorite state is unknown for at least one user in scope.")
            : Rule(RuleIds.Favorite, RuleKind.Protection, RuleOutcome.Pass, "Not favorited by any user in scope.");
    }

    private static IEnumerable<Guid> Ancestry(LeafItem leaf)
    {
        yield return leaf.Id;
        if (leaf.SeasonId is { } season)
        {
            yield return season;
        }

        if (leaf.SeriesId is { } series)
        {
            yield return series;
        }
    }

    private static RuleEvaluation InProgress(CleanupPolicy policy, IReadOnlyList<Guid> users, EvaluationInput input, LeafItem leaf)
    {
        // A stored policy cannot set None; treat it as the strictest scope if one ever does.
        var scope = policy.InProgressProtection == UserProtectionScope.SelectedUsers ? users : (IReadOnlyCollection<Guid>)input.KnownUsers;
        var unknown = false;
        foreach (var user in scope)
        {
            var state = StateOf(input, leaf.Id, user, leaf.LibraryId);
            if (state.State == WatchState.Partial)
            {
                return Rule(RuleIds.InProgress, RuleKind.Protection, RuleOutcome.Block, "A user in scope has a saved resume position.");
            }

            unknown |= state.State == WatchState.Unknown;
        }

        return unknown
            ? Rule(RuleIds.InProgress, RuleKind.Protection, RuleOutcome.Unknown, "Progress is unknown for at least one user in scope.")
            : Rule(RuleIds.InProgress, RuleKind.Protection, RuleOutcome.Pass, "No user in scope has a saved resume position.");
    }

    private static RuleEvaluation KeepRecentlyAdded(CleanupPolicy policy, EvaluationInput input, LeafItem leaf)
    {
        if (policy.KeepAddedWithinDays is not { } days)
        {
            return Rule(RuleIds.KeepRecentlyAdded, RuleKind.Protection, RuleOutcome.NotApplicable, "Not configured.");
        }

        if (leaf.DateAddedUtc is not { } added)
        {
            return Rule(RuleIds.KeepRecentlyAdded, RuleKind.Protection, RuleOutcome.Unknown, "The date added is missing.");
        }

        return input.NowUtc - added < TimeSpan.FromDays(days)
            ? Rule(RuleIds.KeepRecentlyAdded, RuleKind.Protection, RuleOutcome.Block, $"Added within the last {days} day(s).")
            : Rule(RuleIds.KeepRecentlyAdded, RuleKind.Protection, RuleOutcome.Pass, $"Added more than {days} day(s) ago.");
    }

    private static RuleEvaluation ExcludedPath(CleanupPolicy policy, EvaluationInput input, LeafItem leaf)
    {
        foreach (var prefix in policy.ExcludedPathPrefixes)
        {
            var bounded = prefix.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (leaf.Paths.Any(p => p.StartsWith(bounded, input.PathComparison)))
            {
                return Rule(RuleIds.ExcludedPath, RuleKind.Protection, RuleOutcome.Block, "A file is under an excluded path.");
            }
        }

        return Rule(RuleIds.ExcludedPath, RuleKind.Protection, RuleOutcome.NotApplicable, "No excluded path matches.");
    }

    private static RuleEvaluation ExcludedTag(CleanupPolicy policy, LeafItem leaf)
    {
        var hit = policy.ExcludedTags.FirstOrDefault(t => leaf.Tags.Contains(t, StringComparer.OrdinalIgnoreCase));
        return hit is null
            ? Rule(RuleIds.ExcludedTag, RuleKind.Protection, RuleOutcome.NotApplicable, "No excluded tag matches.")
            : Rule(RuleIds.ExcludedTag, RuleKind.Protection, RuleOutcome.Block, "Carries an excluded tag.");
    }

    private static RuleEvaluation Watched(WatchedRequirement requirement, IReadOnlyList<Guid> users, EvaluationInput input, LeafItem leaf)
    {
        int played = 0, unknown = 0;
        foreach (var user in users)
        {
            var state = StateOf(input, leaf.Id, user, leaf.LibraryId).State;
            played += state == WatchState.Played ? 1 : 0;
            unknown += state == WatchState.Unknown ? 1 : 0;
        }

        var notPlayed = users.Count - played - unknown;
        if (requirement == WatchedRequirement.AllSelected)
        {
            // Kleene AND: any definite "not played" decides, otherwise any unknown leaves it undecided.
            return users.Count == 0 || notPlayed > 0
                ? Rule(RuleIds.WatchedAll, RuleKind.Criterion, RuleOutcome.Block, $"Marked played by {played} of {users.Count} selected user(s); all are required.")
                : unknown > 0
                    ? Rule(RuleIds.WatchedAll, RuleKind.Criterion, RuleOutcome.Unknown, $"State is unknown for {unknown} of {users.Count} selected user(s).")
                    : Rule(RuleIds.WatchedAll, RuleKind.Criterion, RuleOutcome.Pass, $"Marked played by all {users.Count} selected user(s).");
        }

        // Kleene OR: any definite "played" decides, otherwise any unknown leaves it undecided.
        return played > 0
            ? Rule(RuleIds.WatchedAny, RuleKind.Criterion, RuleOutcome.Pass, $"Marked played by {played} of {users.Count} selected user(s).")
            : unknown > 0
                ? Rule(RuleIds.WatchedAny, RuleKind.Criterion, RuleOutcome.Unknown, $"No selected user has it played, and {unknown} are unknown.")
                : Rule(RuleIds.WatchedAny, RuleKind.Criterion, RuleOutcome.Block, "No selected user has it marked played.");
    }

    private static RuleEvaluation LastWatched(CleanupPolicy policy, IReadOnlyList<Guid> users, EvaluationInput input, LeafItem leaf)
    {
        if (policy.MinDaysSinceLastWatched is not { } days)
        {
            return Rule(RuleIds.LastWatchedAge, RuleKind.Criterion, RuleOutcome.NotApplicable, "Not configured.");
        }

        DateTime? latest = null;
        var undecidable = false;
        foreach (var user in users)
        {
            var state = StateOf(input, leaf.Id, user, leaf.LibraryId);
            undecidable |= state.State == WatchState.Unknown || (state.State == WatchState.Played && state.LastPlayedUtc is null);
            if (state.LastPlayedUtc is { } at && (latest is null || at > latest))
            {
                latest = at;
            }
        }

        // A known-recent play decides against deletion even if another user's timestamp is missing.
        if (latest is { } value && input.NowUtc - value < TimeSpan.FromDays(days))
        {
            return Rule(RuleIds.LastWatchedAge, RuleKind.Criterion, RuleOutcome.Block, $"Last played {value.ToString("u", CultureInfo.InvariantCulture)}, within {days} day(s).");
        }

        if (undecidable)
        {
            return Rule(RuleIds.LastWatchedAge, RuleKind.Criterion, RuleOutcome.Unknown, "A selected user's last-played time is missing or unknown, so the most recent play cannot be established.");
        }

        return latest is { } oldest
            ? Rule(RuleIds.LastWatchedAge, RuleKind.Criterion, RuleOutcome.Pass, $"Most recent play across selected users was {oldest.ToString("u", CultureInfo.InvariantCulture)}.")
            : Rule(RuleIds.LastWatchedAge, RuleKind.Criterion, RuleOutcome.Unknown, "No last-played time is recorded for any selected user.");
    }

    private static RuleEvaluation AddedAge(CleanupPolicy policy, EvaluationInput input, LeafItem leaf)
    {
        if (policy.MinDaysSinceAdded is not { } days)
        {
            return Rule(RuleIds.AddedAge, RuleKind.Criterion, RuleOutcome.NotApplicable, "Not configured.");
        }

        if (leaf.DateAddedUtc is not { } added)
        {
            return Rule(RuleIds.AddedAge, RuleKind.Criterion, RuleOutcome.Unknown, "The date added is missing.");
        }

        return input.NowUtc - added >= TimeSpan.FromDays(days)
            ? Rule(RuleIds.AddedAge, RuleKind.Criterion, RuleOutcome.Pass, $"Added at least {days} day(s) ago.")
            : Rule(RuleIds.AddedAge, RuleKind.Criterion, RuleOutcome.Block, $"Added less than {days} day(s) ago.");
    }

    /// <summary>
    /// Keep-newest: within a show, regular episodes sort by (season, episode) and the top N are kept.
    /// Specials (season 0) neither count toward N nor are protected by this rule. If any regular file-backed
    /// episode lacks numbering, order cannot be established and the whole show is Unknown.
    /// </summary>
    private static Dictionary<Guid, RuleEvaluation> BuildKeepNewest(CleanupPolicy policy, EvaluationInput input)
    {
        var results = new Dictionary<Guid, RuleEvaluation>();
        if (policy.KeepNewestEpisodesPerShow is not { } keep)
        {
            return results;
        }

        foreach (var leaf in input.Leaves.Where(l => l.Kind == MediaKind.Episode && l.SeriesId is null))
        {
            results[leaf.Id] = Rule(RuleIds.KeepNewestEpisodes, RuleKind.Protection, RuleOutcome.Unknown, "This episode has no show to rank it in.");
        }

        var groups = input.Leaves
            .Where(l => l.Kind == MediaKind.Episode && l.SeriesId is not null && l.Health != ItemHealth.NotFileBacked)
            .GroupBy(l => l.SeriesId!.Value);
        foreach (var group in groups)
        {
            var regular = group.Where(l => l.SeasonNumber != 0).ToList();
            var undecidable = regular.Any(l => l.SeasonNumber is null || l.EpisodeNumber is null);
            foreach (var special in group.Where(l => l.SeasonNumber == 0))
            {
                results[special.Id] = Rule(RuleIds.KeepNewestEpisodes, RuleKind.Protection, RuleOutcome.NotApplicable, "Specials are not ranked for keep-newest.");
            }

            if (undecidable)
            {
                foreach (var leaf in regular)
                {
                    results[leaf.Id] = Rule(RuleIds.KeepNewestEpisodes, RuleKind.Protection, RuleOutcome.Unknown, "An episode in this show has no season or episode number, so newest cannot be determined.");
                }

                continue;
            }

            var ordered = regular
                .OrderByDescending(l => l.SeasonNumber)
                .ThenByDescending(l => l.EpisodeNumber)
                .ThenBy(l => l.Id)
                .ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                results[ordered[i].Id] = i < keep
                    ? Rule(RuleIds.KeepNewestEpisodes, RuleKind.Protection, RuleOutcome.Block, $"Among the newest {keep} episode(s) of this show.")
                    : Rule(RuleIds.KeepNewestEpisodes, RuleKind.Protection, RuleOutcome.Pass, $"Older than the newest {keep} episode(s).");
            }
        }

        return results;
    }

    private static void ApplyUnit(CleanupPolicy policy, EvaluationInput input, Dictionary<Guid, ItemEvaluation> results)
    {
        var episodes = input.Leaves.Where(l => l.Kind == MediaKind.Episode && l.Health != ItemHealth.NotFileBacked);
        var unit = policy.Unit;
        var groups = unit == CleanupUnit.CompleteSeason
            ? episodes.GroupBy(l => l.SeasonId ?? l.Id)
            : episodes.GroupBy(l => l.SeriesId ?? l.Id);
        var label = unit == CleanupUnit.CompleteSeason ? "season" : "show";
        foreach (var group in groups)
        {
            var members = group.ToList();
            var eligible = members.Count(m => results[m.Id].Eligible);
            var complete = eligible == members.Count;
            var rule = complete
                ? Rule(RuleIds.UnitComplete, RuleKind.Criterion, RuleOutcome.Pass, $"All {members.Count} episode(s) of this {label} qualify.")
                : Rule(RuleIds.UnitComplete, RuleKind.Criterion, RuleOutcome.Block, $"Only {eligible} of {members.Count} episode(s) of this {label} qualify; the whole {label} is required.");
            foreach (var member in members)
            {
                var previous = results[member.Id];
                var rules = previous.Rules.Append(rule).ToList();
                results[member.Id] = new ItemEvaluation(member.Id, previous.Eligible && complete, rules);
            }
        }
    }
}
