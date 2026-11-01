using System.Text.Json.Serialization;

namespace Jellytrim.Core.FileSystem;

[JsonConverter(typeof(JsonStringEnumConverter<PathRefusal>))]
public enum PathRefusal
{
    None = 0,
    Malformed,
    Traversal,
    NoApprovedRoot,
    OutsideApprovedRoots,
    IsRoot,
    RootUnavailable,
    SymlinkInPath,
    TargetIsLink,
    NotARegularFile,
    TargetMissing,
    TargetInaccessible,
}

public sealed record PathCheck(PathRefusal Refusal, string? MatchedRoot = null)
{
    public bool IsSafe => Refusal == PathRefusal.None;
}

/// <summary>
/// The one place that decides whether a path may be touched. A path passes only if it is absolute and
/// already normalized, sits strictly below an approved root, and no component below that root is a
/// symlink or junction. Because nothing below the root redirects, the root's own real location bounds
/// the target. Roots themselves may be symlinks, which is common for mount aliases.
/// </summary>
public sealed class PathGuard
{
    private readonly string[] _roots;
    private readonly string[] _neverTargets;
    private readonly StringComparison _comparison;

    /// <param name="approvedRoots">Directories inside which targets are allowed.</param>
    /// <param name="comparison">Path comparison; defaults to the platform's.</param>
    /// <param name="otherRoots">Further roots that are never targets themselves, such as unapproved library locations nested in an approved one.</param>
    public PathGuard(IEnumerable<string> approvedRoots, StringComparison? comparison = null, IEnumerable<string>? otherRoots = null)
    {
        _comparison = comparison ?? PlatformComparison;
        _roots = approvedRoots.Select(TrimRoot).Where(r => r.Length > 0).Distinct().ToArray();
        _neverTargets = _roots.Concat((otherRoots ?? []).Select(TrimRoot).Where(r => r.Length > 0)).Distinct().ToArray();
    }

    /// <summary>Windows and default macOS volumes are case-insensitive; Linux is compared exactly.</summary>
    public static StringComparison PlatformComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string TrimRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            return string.Empty;
        }

        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // A filesystem root such as "/" or "C:\" can never be an approved root.
        return string.IsNullOrEmpty(trimmed) || Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar) == trimmed ? string.Empty : trimmed;
    }

    /// <summary>Lexical containment and link checks, without requiring the target itself to exist.</summary>
    public PathCheck CheckLocation(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !Path.IsPathFullyQualified(path))
        {
            return new PathCheck(PathRefusal.Malformed);
        }

        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Contains("..") || segments.Contains("."))
        {
            return new PathCheck(PathRefusal.Traversal);
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new PathCheck(PathRefusal.Malformed);
        }

        if (!string.Equals(full, path, StringComparison.Ordinal))
        {
            return new PathCheck(PathRefusal.Malformed);
        }

        if (_roots.Length == 0)
        {
            return new PathCheck(PathRefusal.NoApprovedRoot);
        }

        // Checked across all roots first: with nested roots, an inner one would otherwise pass as a path inside the outer.
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar);
        if (_neverTargets.FirstOrDefault(r => string.Equals(trimmed, r, _comparison)) is { } isRoot)
        {
            return new PathCheck(PathRefusal.IsRoot, isRoot);
        }

        foreach (var root in _roots)
        {
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, _comparison))
            {
                continue;
            }

            if (!Directory.Exists(root))
            {
                return new PathCheck(PathRefusal.RootUnavailable, root);
            }

            // Walk every directory component below the root, plus the leaf.
            var current = root;
            foreach (var part in path[(root.Length + 1)..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                {
                    return new PathCheck(PathRefusal.None, root); // Nothing further exists, so nothing further can redirect.
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return new PathCheck(PathRefusal.TargetInaccessible, root);
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return new PathCheck(string.Equals(current, path, _comparison) ? PathRefusal.TargetIsLink : PathRefusal.SymlinkInPath, root);
                }
            }

            return new PathCheck(PathRefusal.None, root);
        }

        return new PathCheck(PathRefusal.OutsideApprovedRoots);
    }

    /// <summary>Full check for a deletion target. It must exist and be a regular file, not a link.</summary>
    public PathCheck CheckDeletableFile(string path, FileObservation observation)
    {
        var location = CheckLocation(path);
        if (!location.IsSafe)
        {
            return location;
        }

        return observation.Status switch
        {
            Domain.ProbeStatus.NotFound => location with { Refusal = PathRefusal.TargetMissing },
            Domain.ProbeStatus.Ok when observation.IsSymlink => location with { Refusal = PathRefusal.TargetIsLink },
            Domain.ProbeStatus.Ok when !observation.IsRegularFile => location with { Refusal = PathRefusal.NotARegularFile },
            Domain.ProbeStatus.Ok => location,
            _ => location with { Refusal = PathRefusal.TargetInaccessible },
        };
    }
}
