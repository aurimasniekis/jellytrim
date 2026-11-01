using System.Text.Json.Serialization;
using Jellytrim.Core.Domain;

namespace Jellytrim.Core.Policies;

[JsonConverter(typeof(JsonStringEnumConverter<WatchedRequirement>))]
public enum WatchedRequirement
{
    /// <summary>Every effective user has the Played flag.</summary>
    AllSelected = 0,

    /// <summary>At least one effective user has the Played flag.</summary>
    AnySelected = 1,

    /// <summary>Internal: explicit item selection, where the administrator named the items. Stored policies may not use this.</summary>
    NotRequired = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<UserProtectionScope>))]
public enum UserProtectionScope
{
    /// <summary>Protection ignored. Not valid for in-progress protection.</summary>
    None = 0,

    /// <summary>Evaluated across the policy's effective users only.</summary>
    SelectedUsers = 1,

    /// <summary>Evaluated across every user the server knows, including excluded users.</summary>
    AllKnownUsers = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<CleanupUnit>))]
public enum CleanupUnit
{
    /// <summary>Movies and episodes qualify individually.</summary>
    Item = 0,

    /// <summary>An episode qualifies only if every file-backed episode of its season qualifies.</summary>
    CompleteSeason = 1,

    /// <summary>An episode qualifies only if every file-backed episode of its show qualifies.</summary>
    CompleteShow = 2,
}

/// <summary>
/// A cleanup policy. All criteria must pass (AND). Any protection blocks, whatever the criteria say.
/// See docs/policy-semantics.md for the precise definitions; tests pin them.
/// </summary>
public sealed class CleanupPolicy
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Revision { get; set; } = 1;

    // Users
    public List<Guid> ParticipatingUserIds { get; set; } = [];

    public List<Guid> ExcludedUserIds { get; set; } = [];

    // Criteria
    public WatchedRequirement Watched { get; set; } = WatchedRequirement.AllSelected;

    public int? MinDaysSinceLastWatched { get; set; }

    public int? MinDaysSinceAdded { get; set; }

    public long? MinSizeBytes { get; set; }

    public CleanupUnit Unit { get; set; } = CleanupUnit.Item;

    public List<MediaKind> MediaKinds { get; set; } = [MediaKind.Movie, MediaKind.Episode];

    // Scope. Empty IncludedLibraryIds means every library.
    public List<Guid> IncludedLibraryIds { get; set; } = [];

    public List<Guid> ExcludedLibraryIds { get; set; } = [];

    public List<string> ExcludedPathPrefixes { get; set; } = [];

    public List<string> ExcludedTags { get; set; } = [];

    // Protections
    public UserProtectionScope FavoriteProtection { get; set; } = UserProtectionScope.AllKnownUsers;

    public UserProtectionScope InProgressProtection { get; set; } = UserProtectionScope.AllKnownUsers;

    public int? KeepAddedWithinDays { get; set; }

    public int? KeepNewestEpisodesPerShow { get; set; }

    [JsonIgnore]
    public IReadOnlyList<Guid> EffectiveUsers => ParticipatingUserIds.Except(ExcludedUserIds).Distinct().OrderBy(u => u).ToList();

    /// <summary>The fixed rule set for explicit selection: no watch requirement, every protection at its strictest.</summary>
    public static CleanupPolicy ManualSelection() => new()
    {
        Id = Guid.Empty,
        Name = "Explicit selection",
        Watched = WatchedRequirement.NotRequired,
        FavoriteProtection = UserProtectionScope.AllKnownUsers,
        InProgressProtection = UserProtectionScope.AllKnownUsers,
        MediaKinds = [MediaKind.Movie, MediaKind.Episode],
    };

    /// <summary>Validates a stored policy, returning human-readable problems.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 120)
        {
            problems.Add("Name is required and must be at most 120 characters.");
        }

        if (Watched == WatchedRequirement.NotRequired)
        {
            problems.Add("A stored policy must require watched by all or any selected users.");
        }

        if (EffectiveUsers.Count == 0)
        {
            problems.Add("Select at least one participating user who is not also excluded.");
        }

        if (InProgressProtection == UserProtectionScope.None)
        {
            problems.Add("In-progress protection cannot be disabled.");
        }

        foreach (var (value, label) in new[] { (MinDaysSinceLastWatched, "Days since last watched"), (MinDaysSinceAdded, "Days since added"), (KeepAddedWithinDays, "Keep recently added days"), (KeepNewestEpisodesPerShow, "Keep newest episodes") })
        {
            if (value is < 0 or > 36500)
            {
                problems.Add($"{label} must be between 0 and 36500.");
            }
        }

        if (MinSizeBytes is < 0)
        {
            problems.Add("Minimum size cannot be negative.");
        }

        if (MediaKinds.Count == 0 || MediaKinds.Any(k => k is not (MediaKind.Movie or MediaKind.Episode)))
        {
            problems.Add("Media kinds must be Movie and/or Episode.");
        }

        if (ExcludedPathPrefixes.Any(p => !Path.IsPathFullyQualified(p)))
        {
            problems.Add("Excluded paths must be absolute.");
        }

        return problems;
    }
}

/// <summary>An explicit protection on an item (propagating to descendants) or a whole library.</summary>
public sealed record Protection(Guid TargetId, ProtectionTarget TargetType, string Name, string? Note, DateTime CreatedAtUtc, string? CreatedBy);
