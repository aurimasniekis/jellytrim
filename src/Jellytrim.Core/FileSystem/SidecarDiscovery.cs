namespace Jellytrim.Core.FileSystem;

public sealed record DirectoryScan(IReadOnlyList<string> Sidecars, IReadOnlyList<string> Unplanned, bool HasSubdirectories);

/// <summary>
/// Decides which files beside a video belong to it. Deliberately stricter than Jellyfin's own
/// GetLocalMetadataFilesToDelete, which is a bare prefix match and so treats "Foo 2.srt" as belonging to "Foo.mkv".
/// Anything not positively attributed stays and is reported as unplanned.
/// </summary>
public static class SidecarDiscovery
{
    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".smi", ".sup", ".pgs",
        ".nfo", ".xml", ".jpg", ".jpeg", ".png", ".webp", ".tbn", ".bif",
    };

    /// <summary>Folder-level names that describe "the movie in this folder"; only attributable when one movie lives here.</summary>
    private static readonly HashSet<string> MovieFolderArtwork = new(StringComparer.OrdinalIgnoreCase)
    {
        "movie.nfo", "poster.jpg", "poster.png", "folder.jpg", "folder.png", "cover.jpg", "cover.png", "default.jpg",
        "fanart.jpg", "fanart.png", "backdrop.jpg", "backdrop.png", "banner.jpg", "banner.png", "clearlogo.png",
        "logo.png", "landscape.jpg", "thumb.jpg", "disc.png", "clearart.png",
    };

    private static readonly HashSet<string> SeriesFolderArtwork = new(StringComparer.OrdinalIgnoreCase)
    {
        "tvshow.nfo", "season.nfo", "poster.jpg", "poster.png", "folder.jpg", "folder.png", "fanart.jpg", "fanart.png",
        "backdrop.jpg", "backdrop.png", "banner.jpg", "banner.png", "clearlogo.png", "logo.png", "landscape.jpg",
        "thumb.jpg", "clearart.png",
    };

    /// <summary>Anything that may be a video, indexed or not. Jellyfin does not index extras as leaves, and .iso or .strm are not plain files.</summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".mts", ".webm", ".mpg", ".mpeg", ".flv", ".ogv", ".ogm",
        ".vob", ".iso", ".img", ".strm", ".divx", ".xvid", ".3gp", ".asf", ".rm", ".rmvb", ".wtv", ".dvr-ms",
    };

    /// <summary>Subfolders that hold a movie's own extras, so their videos do not mean other movies share this folder.</summary>
    private static readonly HashSet<string> ExtrasFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "extras", "trailers", "behind the scenes", "deleted scenes", "featurettes", "interviews", "scenes", "samples", "shorts", "clips", "other", "backdrops", "theme-music",
    };

    private static readonly System.Text.RegularExpressions.Regex SeasonArtwork = new(
        @"^season[-\s]?(\d+|specials|all)?(-(poster|banner|fanart|landscape|thumb|clearart|logo))?\.(jpg|jpeg|png|webp|tbn|nfo)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool IsSidecarExtension(string path) => SidecarExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Finds sidecars of <paramref name="mediaPaths"/> in one directory.
    /// </summary>
    /// <param name="directory">The directory to read.</param>
    /// <param name="mediaPaths">The item's media files in this directory.</param>
    /// <param name="otherMediaPaths">All other indexed media paths in this directory, whose sidecars must not be claimed.</param>
    /// <param name="includeFolderArtwork">Whether folder-level artwork belongs to the item (a movie alone in its folder).</param>
    public static DirectoryScan Discover(string directory, IReadOnlyCollection<string> mediaPaths, IReadOnlyCollection<string> otherMediaPaths, bool includeFolderArtwork)
    {
        var own = new HashSet<string>(mediaPaths, StringComparer.Ordinal);
        var stems = mediaPaths.Select(Path.GetFileNameWithoutExtension).OfType<string>().ToArray();
        var entries = new DirectoryInfo(directory).EnumerateFileSystemInfos().ToList();

        // Other stems come from the live listing too, because the inventory only knows indexed movies and episodes.
        var otherVideos = entries.OfType<FileInfo>().Where(f => !own.Contains(f.FullName) && VideoExtensions.Contains(f.Extension)).Select(f => f.FullName);
        var otherStems = otherMediaPaths.Concat(otherVideos).Select(Path.GetFileNameWithoutExtension).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var soleOccupant = includeFolderArtwork && otherStems.Length == 0 && !entries.OfType<DirectoryInfo>().Any(HoldsOtherVideos);
        var sidecars = new List<string>();
        var unplanned = new List<string>();
        var hasSubdirectories = false;

        foreach (var entry in entries)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                unplanned.Add(entry.FullName); // Never follow or claim links.
                continue;
            }

            if (entry is DirectoryInfo sub)
            {
                // Jellyfin stores trickplay beside media as "<stem>.trickplay"; everything else (extras/, trailers/) is not ours.
                if (stems.Any(s => string.Equals(sub.Name, s + ".trickplay", StringComparison.OrdinalIgnoreCase))
                    && !otherStems.Any(s => string.Equals(sub.Name, s + ".trickplay", StringComparison.OrdinalIgnoreCase)))
                {
                    sidecars.AddRange(sub.EnumerateFiles("*", SearchOption.AllDirectories)
                        .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
                        .Select(f => f.FullName));
                }
                else
                {
                    hasSubdirectories = true;
                    unplanned.Add(entry.FullName);
                }

                continue;
            }

            if (own.Contains(entry.FullName))
            {
                continue;
            }

            if (Attributes(entry.Name, stems, otherStems) || (soleOccupant && MovieFolderArtwork.Contains(entry.Name)))
            {
                sidecars.Add(entry.FullName);
            }
            else
            {
                unplanned.Add(entry.FullName);
            }
        }

        sidecars.Sort(StringComparer.Ordinal);
        unplanned.Sort(StringComparer.Ordinal);
        return new DirectoryScan(sidecars, unplanned, hasSubdirectories);
    }

    /// <summary>Series and season folder metadata, attributable only when every episode below is being removed.</summary>
    public static DirectoryScan DiscoverContainerArtwork(string directory)
    {
        var sidecars = new List<string>();
        var unplanned = new List<string>();
        var hasSubdirectories = false;
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                hasSubdirectories = true;
                unplanned.Add(entry.FullName);
            }
            else if (SeriesFolderArtwork.Contains(entry.Name) || IsSeasonArtwork(entry.Name))
            {
                sidecars.Add(entry.FullName);
            }
            else
            {
                unplanned.Add(entry.FullName);
            }
        }

        sidecars.Sort(StringComparer.Ordinal);
        unplanned.Sort(StringComparer.Ordinal);
        return new DirectoryScan(sidecars, unplanned, hasSubdirectories);
    }

    private static bool IsSeasonArtwork(string name) => SeasonArtwork.IsMatch(name);

    /// <summary>A folder such as Marvel/Thor/Thor.mkv means Marvel/poster.jpg is not this movie's. Unreadable subfolders throw, so nothing gets claimed.</summary>
    private static bool HoldsOtherVideos(DirectoryInfo directory)
    {
        if (ExtrasFolders.Contains(directory.Name) || directory.Name.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase) || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
        return directory.EnumerateFiles("*", options).Any(f => VideoExtensions.Contains(f.Extension));
    }

    /// <summary>
    /// A file belongs to a stem if its name is exactly "stem.ext", or "stem.something.ext" or "stem-something.ext",
    /// and no other media file in the folder has a longer matching stem.
    /// </summary>
    private static bool Attributes(string fileName, string[] stems, string[] otherStems)
    {
        if (!IsSidecarExtension(fileName))
        {
            return false;
        }

        var bare = Path.GetFileNameWithoutExtension(fileName);
        var best = stems.Where(s => Matches(bare, s)).Select(s => s.Length).DefaultIfEmpty(-1).Max();
        return best >= 0 && !otherStems.Any(s => s.Length >= best && Matches(bare, s));
    }

    private static bool Matches(string bare, string stem) =>
        bare.Equals(stem, StringComparison.OrdinalIgnoreCase)
        || (bare.Length > stem.Length
            && bare.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
            && bare[stem.Length] is '.' or '-');
}
