using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellytrim.Core.Abstractions;
using Jellytrim.Core.Domain;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;
using Movie = MediaBrowser.Controller.Entities.Movies.Movie;
using Season = MediaBrowser.Controller.Entities.TV.Season;
using Series = MediaBrowser.Controller.Entities.TV.Series;

namespace Jellyfin.Plugin.Jellytrim.Adapter;

/// <summary>
/// The only code that talks to Jellyfin's library, user and session managers. Verified against the 12.4 source:
/// see docs/verified-integration.md. RemoveItemRecord is the only call into a deleting API, and it always passes
/// DeleteFileLocation=false because Jellyfin's own file deletion can remove a whole movie folder recursively.
/// </summary>
public sealed class JellyfinMediaHost(
    ILibraryManager library,
    IUserManager users,
    IUserDataManager userData,
    ISessionManager sessions,
    ILogger<JellyfinMediaHost> logger) : IMediaHost
{
    private static readonly BaseItemKind[] Kinds = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode];

    public IReadOnlyList<HostLibrary> GetLibraries() => library.GetVirtualFolders()
        .Where(f => f.CollectionType is null or CollectionTypeOptions.movies or CollectionTypeOptions.tvshows or CollectionTypeOptions.mixed)
        .Where(f => Guid.TryParse(f.ItemId, out _))
        .Select(f => new HostLibrary(Guid.Parse(f.ItemId), f.Name, f.CollectionType?.ToString(), f.Locations ?? []))
        .ToList();

    public IReadOnlyList<HostUser> GetUsers() => users.GetUsers().Select(u => new HostUser(
        u.Id,
        u.Username,
        u.HasPermission(PermissionKind.IsAdministrator),
        u.HasPermission(PermissionKind.IsDisabled),
        u.HasPermission(PermissionKind.EnableAllFolders),
        u.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders))).ToList();

    public IReadOnlyList<Guid> ListItemIds(Guid libraryId)
    {
        var query = new InternalItemsQuery { IncludeItemTypes = Kinds, Recursive = true };
        if (library.GetItemById(libraryId) is CollectionFolder { PhysicalFolderIds.Length: > 0 } folder)
        {
            query.TopParentIds = folder.PhysicalFolderIds;
        }
        else
        {
            query.AncestorIds = [libraryId];
        }

        return library.GetItemIds(query);
    }

    public HostItemPage ReadItems(Guid libraryId, IReadOnlyList<Guid> itemIds, IReadOnlyCollection<Guid> userIds)
    {
        var loaded = library.GetItemList(new InternalItemsQuery { ItemIds = itemIds.ToArray(), IncludeItemTypes = Kinds })
            .Where(i => i is not Video { PrimaryVersionId: not null })
            .ToList();
        var items = new List<HostItem>(loaded.Count);
        var groups = new List<(BaseItem Item, List<BaseItem> Versions)>(loaded.Count);
        foreach (var item in loaded)
        {
            var versions = VersionsOf(item);
            groups.Add((item, versions));
            items.Add(ToHostItem(item, versions, libraryId));
        }

        var (states, failed) = ReadStates(groups, userIds);
        return new HostItemPage { Items = items, States = states, FailedUsers = failed };
    }

    public HostItem? GetItem(Guid itemId)
    {
        var item = library.GetItemById(itemId);
        return item is null || !Kinds.Contains(item.GetBaseItemKind()) ? null : ToHostItem(item, VersionsOf(item), Guid.Empty);
    }

    public IReadOnlyDictionary<(Guid ItemId, Guid UserId), UserItemState> ReadLiveStates(IReadOnlyCollection<Guid> itemIds, IReadOnlyCollection<Guid> userIds)
    {
        var groups = itemIds
            .Select(library.GetItemById)
            .OfType<BaseItem>()
            .Select(i => (Item: i, Versions: VersionsOf(i)))
            .ToList();
        var (states, failed) = ReadStates(groups, userIds);
        if (failed.Count == 0)
        {
            return states;
        }

        // A live-read failure must never read as Unplayed: record Unknown so that re-evaluation fails closed.
        var result = new Dictionary<(Guid, Guid), UserItemState>(states);
        foreach (var user in failed)
        {
            foreach (var (item, _) in groups)
            {
                result[(item.Id, user)] = UserItemState.Unknown;
            }
        }

        return result;
    }

    public ActivePlayback GetActivePlayback()
    {
        var ids = new HashSet<Guid>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in sessions.Sessions)
        {
            if (session.NowPlayingItem is { } dto)
            {
                ids.Add(dto.Id);
            }

            if (session.FullNowPlayingItem is { } full)
            {
                ids.Add(full.Id);
                if (!string.IsNullOrEmpty(full.Path))
                {
                    paths.Add(full.Path);
                }
            }
        }

        return new ActivePlayback(ids, paths);
    }

    public void RemoveItemRecord(Guid itemId)
    {
        var item = library.GetItemById(itemId);
        if (item is null)
        {
            return;
        }

        // Files were already removed by jellytrim and DeleteFileLocation stays false. See docs/deletion-safety.md.
        library.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, notifyParentItem: true);
    }

    public bool HasChildren(Guid containerId) =>
        library.GetItemIds(new InternalItemsQuery { AncestorIds = [containerId], Recursive = true, Limit = 1 }).Count > 0;

    private List<BaseItem> VersionsOf(BaseItem item)
    {
        var versions = new List<BaseItem> { item };
        if (item is Video video)
        {
            versions.AddRange(library.GetLocalAlternateVersionIds(video).Select(library.GetItemById).OfType<BaseItem>());
            versions.AddRange(library.GetLinkedAlternateVersions(video));
        }

        return versions.DistinctBy(v => v.Id).ToList();
    }

    private static HostItem ToHostItem(BaseItem item, List<BaseItem> versions, Guid libraryId)
    {
        var fileBacked = item is Video video && IsPlainFile(video) && versions.OfType<Video>().All(IsPlainFile);
        var files = new List<HostItemFile>();
        if (fileBacked)
        {
            foreach (var version in versions.OfType<Video>())
            {
                files.Add(new HostItemFile(version.Path, version.Id == item.Id ? FileRole.Main : FileRole.AlternateVersion, version.Id));
                files.AddRange(version.AdditionalParts.Select(part => new HostItemFile(part, FileRole.Part, version.Id)));
            }
        }

        var episode = item as Episode;
        return new HostItem
        {
            Id = item.Id,
            Kind = item switch
            {
                Movie => MediaKind.Movie,
                Series => MediaKind.Series,
                Season => MediaKind.Season,
                _ => MediaKind.Episode,
            },
            Name = item.Name ?? string.Empty,
            SortName = item.SortName,
            LibraryId = libraryId,
            SeriesId = item switch
            {
                Episode e => NonEmpty(e.SeriesId),
                Season s => NonEmpty(s.SeriesId),
                _ => null,
            },
            SeasonId = episode is null ? null : NonEmpty(episode.SeasonId),
            SeriesName = item switch
            {
                Episode e => e.SeriesName,
                Season s => s.SeriesName,
                _ => null,
            },
            SeasonNumber = item is Season season ? season.IndexNumber : episode?.ParentIndexNumber,
            EpisodeNumber = episode?.IndexNumber,

            // Jellyfin leaves DateCreated at MinValue when unknown. Treat that as missing rather than as very old.
            DateAddedUtc = item.DateCreated.Year > 1970 ? DateTime.SpecifyKind(item.DateCreated, DateTimeKind.Utc) : null,
            PremiereDateUtc = item.PremiereDate is { } premiere ? DateTime.SpecifyKind(premiere, DateTimeKind.Utc) : null,
            RuntimeTicks = item.RunTimeTicks,
            Tags = item.Tags ?? [],
            Path = item.Path,
            Files = files,
            IsFileBacked = fileBacked,
            VersionIds = versions.Select(v => v.Id).ToList(),
        };
    }

    /// <summary>Only plain single-file videos are deletion candidates. Remote streams, .strm shortcuts and disc folders are not.</summary>
    private static bool IsPlainFile(Video video) =>
        video.IsFileProtocol && !video.IsVirtualItem && !video.IsShortcut && !video.IsPlaceHolder
        && video.VideoType == VideoType.VideoFile && !string.IsNullOrEmpty(video.Path);

    private static Guid? NonEmpty(Guid id) => id == Guid.Empty ? null : id;

    private (Dictionary<(Guid, Guid), UserItemState> States, List<Guid> Failed) ReadStates(List<(BaseItem Item, List<BaseItem> Versions)> groups, IReadOnlyCollection<Guid> userIds)
    {
        var states = new Dictionary<(Guid, Guid), UserItemState>();
        var failed = new List<Guid>();
        var all = groups.SelectMany(g => g.Versions).DistinctBy(v => v.Id).ToList();
        foreach (var userId in userIds)
        {
            var user = users.GetUserById(userId);
            if (user is null)
            {
                failed.Add(userId);
                continue;
            }

            try
            {
                var batch = userData.GetUserDataBatch(all, user);
                foreach (var (item, versions) in groups)
                {
                    var merged = Merge(item, versions.Select(v => batch.GetValueOrDefault(v.Id)).OfType<UserItemData>());
                    if (!merged.IsDefault)
                    {
                        states[(item.Id, userId)] = merged;
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "jellytrim could not read user data {UserId}", userId);
                failed.Add(userId);
            }
        }

        return (states, failed);
    }

    /// <summary>Any version in progress wins, then any played version. A resume position must keep protecting the item.</summary>
    private static UserItemState Merge(BaseItem item, IEnumerable<UserItemData> data)
    {
        var rows = data.ToList();
        if (rows.Count == 0)
        {
            return UserItemState.Default;
        }

        var favorite = rows.Any(r => r.IsFavorite);
        if (item is Folder)
        {
            // Jellyfin's Played flag on a series or season is derived. jellytrim derives its own from episodes, so only favorites apply here.
            return new UserItemState(WatchState.Unplayed, IsFavorite: favorite);
        }

        var state = rows.Any(r => !r.Played && r.PlaybackPositionTicks > 0) ? WatchState.Partial
            : rows.Any(r => r.Played) ? WatchState.Played
            : WatchState.Unplayed;
        var lastPlayed = rows.Select(r => r.LastPlayedDate).Max();
        return new UserItemState(
            state,
            rows.Max(r => r.PlaybackPositionTicks),
            rows.Sum(r => r.PlayCount),
            lastPlayed is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null,
            favorite);
    }
}
