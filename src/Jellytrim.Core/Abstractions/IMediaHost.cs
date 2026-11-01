using Jellytrim.Core.Domain;

namespace Jellytrim.Core.Abstractions;

/// <summary>Everything jellytrim needs from the media server. Only the plugin adapter implements this against Jellyfin.</summary>
public interface IMediaHost
{
    IReadOnlyList<HostLibrary> GetLibraries();

    IReadOnlyList<HostUser> GetUsers();

    /// <summary>Snapshots every movie, series, season and episode id in a library. Paging by id stays stable while the library changes.</summary>
    IReadOnlyList<Guid> ListItemIds(Guid libraryId);

    /// <summary>Reads a bounded batch of items with state for the given users. Alternate versions are folded into their primary.</summary>
    HostItemPage ReadItems(Guid libraryId, IReadOnlyList<Guid> itemIds, IReadOnlyCollection<Guid> userIds);

    /// <summary>Reads the current server view of one item, or null when the server no longer knows it.</summary>
    HostItem? GetItem(Guid itemId);

    /// <summary>Reads live state of the item and its ancestors (favorites propagate down).</summary>
    IReadOnlyDictionary<(Guid ItemId, Guid UserId), UserItemState> ReadLiveStates(IReadOnlyCollection<Guid> itemIds, IReadOnlyCollection<Guid> userIds);

    /// <summary>Ids and paths in active playback, at the moment of the call.</summary>
    ActivePlayback GetActivePlayback();

    /// <summary>Removes the item's server record WITHOUT touching source media.</summary>
    void RemoveItemRecord(Guid itemId);

    /// <summary>True if the server still holds child items below this container.</summary>
    bool HasChildren(Guid containerId);
}

public sealed record HostLibrary(Guid Id, string Name, string? CollectionType, IReadOnlyList<string> Locations);

public sealed record HostUser(Guid Id, string Name, bool IsAdministrator, bool IsDisabled, bool HasAllLibraries, IReadOnlyList<Guid> EnabledLibraries)
{
    public bool CanAccess(Guid libraryId) => HasAllLibraries || EnabledLibraries.Contains(libraryId);
}

public sealed record HostItemFile(string Path, FileRole Role, Guid VersionItemId);

public sealed class HostItem
{
    public required Guid Id { get; init; }

    public required MediaKind Kind { get; init; }

    public required string Name { get; init; }

    public string? SortName { get; init; }

    public Guid LibraryId { get; init; }

    public Guid? SeriesId { get; init; }

    public Guid? SeasonId { get; init; }

    public string? SeriesName { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public DateTime? DateAddedUtc { get; init; }

    public DateTime? PremiereDateUtc { get; init; }

    public long? RuntimeTicks { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Directory or file path recorded by the server, for containers and diagnostics.</summary>
    public string? Path { get; init; }

    /// <summary>All media files of all versions and parts. Empty if the item has no local file.</summary>
    public IReadOnlyList<HostItemFile> Files { get; init; } = [];

    /// <summary>False for virtual episodes, remote streams and anything else without a local file.</summary>
    public bool IsFileBacked { get; init; }

    /// <summary>The item plus any alternate version ids.</summary>
    public IReadOnlyList<Guid> VersionIds { get; init; } = [];
}

public sealed class HostItemPage
{
    public required IReadOnlyList<HostItem> Items { get; init; }

    /// <summary>Non-default state only. Absence means Unplayed and not favorite, for users not listed in <see cref="FailedUsers"/>.</summary>
    public required IReadOnlyDictionary<(Guid ItemId, Guid UserId), UserItemState> States { get; init; }

    /// <summary>Users whose state could not be read for this page. Their state must be recorded as Unknown.</summary>
    public IReadOnlyCollection<Guid> FailedUsers { get; init; } = [];
}

/// <summary>
/// One user's state on one item, merged across alternate versions by the adapter.
/// Any version in progress makes the item Partial, otherwise any played version makes it Played.
/// </summary>
public sealed record UserItemState(
    WatchState State,
    long PositionTicks = 0,
    int PlayCount = 0,
    DateTime? LastPlayedUtc = null,
    bool IsFavorite = false)
{
    public static readonly UserItemState Default = new(WatchState.Unplayed);

    public static readonly UserItemState Unknown = new(WatchState.Unknown);

    public bool IsDefault => State == WatchState.Unplayed && PositionTicks == 0 && PlayCount == 0 && LastPlayedUtc is null && !IsFavorite;
}

public sealed record ActivePlayback(IReadOnlySet<Guid> ItemIds, IReadOnlySet<string> Paths)
{
    public static readonly ActivePlayback None = new(new HashSet<Guid>(), new HashSet<string>());
}
