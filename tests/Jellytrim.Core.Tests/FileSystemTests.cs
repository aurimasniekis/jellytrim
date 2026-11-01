using Jellytrim.Core.Domain;
using Jellytrim.Core.FileSystem;

namespace Jellytrim.Core.Tests;

public class NativeFileProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jt-probe-" + Guid.NewGuid().ToString("N"));
    private readonly NativeFileProbe _probe = new();

    public NativeFileProbeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Reports_regular_file_facts()
    {
        var path = TestRig.Write(Path.Combine(_dir, "a.bin"), 12345);
        var o = _probe.Probe(path);
        Assert.Equal(ProbeStatus.Ok, o.Status);
        Assert.True(o.IsRegularFile);
        Assert.False(o.IsSymlink);
        Assert.Equal(12345, o.Size);
        Assert.Equal(1, o.LinkCount);
        Assert.NotNull(o.Identity);
        Assert.NotNull(o.VolumeId);
        Assert.StartsWith(o.VolumeId!, o.Identity);
        Assert.InRange(new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds() - (o.ModifiedUnixNanos / 1_000_000_000), -1, 1);
    }

    [Fact]
    public void Missing_path_is_not_found()
    {
        Assert.Equal(ProbeStatus.NotFound, _probe.Probe(Path.Combine(_dir, "nope")).Status);
        Assert.Equal(ProbeStatus.NotFound, _probe.Probe(Path.Combine(_dir, "nodir", "nope")).Status);
    }

    [Fact]
    public void Symlink_is_observed_not_followed()
    {
        var target = TestRig.Write(Path.Combine(_dir, "target.bin"));
        var link = Path.Combine(_dir, "link.bin");
        File.CreateSymbolicLink(link, target);
        var o = _probe.Probe(link);
        Assert.True(o.IsSymlink);
        Assert.False(o.IsRegularFile);
        Assert.NotEqual(_probe.Probe(target).Identity, o.Identity);
    }

    [Fact]
    public void Hard_links_share_identity_and_report_link_count()
    {
        var a = TestRig.Write(Path.Combine(_dir, "a.bin"));
        var b = Path.Combine(_dir, "b.bin");
        var ln = System.Diagnostics.Process.Start("ln", [a, b]);
        ln.WaitForExit();
        var oa = _probe.Probe(a);
        var ob = _probe.Probe(b);
        Assert.Equal(oa.Identity, ob.Identity);
        Assert.Equal(2, oa.LinkCount);
    }

    [Fact]
    public void Sparse_file_allocates_less_than_its_size()
    {
        var path = Path.Combine(_dir, "sparse.bin");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            System.Diagnostics.Process.Start("truncate", ["-s", "50M", path])?.WaitForExit();
        }

        var o = _probe.Probe(path);
        if (o.Size == 50 * 1024 * 1024)
        {
            Assert.True(o.AllocatedBytes < o.Size, $"allocated {o.AllocatedBytes}");
        }
    }

    [Fact]
    public void Replacing_a_file_changes_identity_or_mtime()
    {
        var path = TestRig.Write(Path.Combine(_dir, "r.bin"));
        var before = _probe.Probe(path);
        var replacement = TestRig.Write(Path.Combine(_dir, "r2.bin"));
        File.Move(replacement, path, true);
        var after = _probe.Probe(path);
        Assert.NotEqual(before.Identity, after.Identity);
    }
}

public class VolumeProbeTests
{
    [Fact]
    public void Reports_plausible_capacity_matching_df()
    {
        var measured = new Jellytrim.Core.Inventory.NativeVolumeProbe().Measure(Path.GetTempPath());
        Assert.NotNull(measured);
        Assert.True(measured.Value.Total > 0 && measured.Value.Free >= 0 && measured.Value.Free <= measured.Value.Total);

        var df = new System.Diagnostics.ProcessStartInfo("df", ["-k", Path.GetTempPath()]) { RedirectStandardOutput = true };
        using var process = System.Diagnostics.Process.Start(df)!;
        var line = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var totalKb = long.Parse(line[1], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(measured.Value.Total / 1024.0 / totalKb, 0.95, 1.05);
    }

    [Fact]
    public void Unreachable_path_is_null_not_zero() =>
        Assert.Null(new Jellytrim.Core.Inventory.NativeVolumeProbe().Measure("/nonexistent-" + Guid.NewGuid()));
}

public class PathGuardTests : IDisposable
{
    private readonly string _base;
    private readonly string _root;
    private readonly NativeFileProbe _probe = new();

    public PathGuardTests()
    {
        var temp = Path.GetTempPath().TrimEnd('/');
        var real = Directory.ResolveLinkTarget(temp, true)?.FullName ?? temp;
        _base = Path.Combine(real, "jt-guard-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "library");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_base, true);

    private PathGuard Guard => new([_root], StringComparison.Ordinal);

    [Fact]
    public void Accepts_a_regular_file_strictly_inside_the_root()
    {
        var file = TestRig.Write(Path.Combine(_root, "a", "b.mkv"));
        Assert.True(Guard.CheckDeletableFile(file, _probe.Probe(file)).IsSafe);
    }

    [Fact]
    public void Refuses_when_no_root_is_approved() =>
        Assert.Equal(PathRefusal.NoApprovedRoot, new PathGuard([]).CheckLocation(Path.Combine(_root, "x")).Refusal);

    [Theory]
    [InlineData("")]
    [InlineData("relative/path.mkv")]
    public void Refuses_malformed_paths(string path) => Assert.Equal(PathRefusal.Malformed, Guard.CheckLocation(path).Refusal);

    [Fact]
    public void Refuses_nul_bytes() => Assert.Equal(PathRefusal.Malformed, Guard.CheckLocation(_root + "/a\0.mkv").Refusal);

    [Theory]
    [InlineData("/../secret.mkv")]
    [InlineData("/a/../../secret.mkv")]
    [InlineData("/./a.mkv")]
    public void Refuses_traversal_even_if_it_would_stay_inside(string suffix) =>
        Assert.Equal(PathRefusal.Traversal, Guard.CheckLocation(_root + suffix).Refusal);

    [Fact]
    public void A_sibling_sharing_the_root_prefix_is_outside()
    {
        var sibling = TestRig.Write(_root + "-other" + Path.DirectorySeparatorChar + "a.mkv");
        Assert.Equal(PathRefusal.OutsideApprovedRoots, Guard.CheckLocation(sibling).Refusal);
    }

    [Fact]
    public void The_root_itself_is_not_a_target()
    {
        Assert.Equal(PathRefusal.IsRoot, Guard.CheckLocation(_root).Refusal);
        Assert.Equal(PathRefusal.IsRoot, new PathGuard([_root + "/"], StringComparison.Ordinal).CheckLocation(_root).Refusal);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("relative")]
    [InlineData("")]
    public void A_filesystem_root_or_relative_path_cannot_be_approved(string root)
    {
        var guard = new PathGuard([root], StringComparison.Ordinal);
        Assert.Equal(PathRefusal.NoApprovedRoot, guard.CheckLocation("/etc/passwd").Refusal);
    }

    [Fact]
    public void Refuses_a_symlinked_directory_inside_the_root()
    {
        var outside = Path.Combine(_base, "outside");
        var victim = TestRig.Write(Path.Combine(outside, "victim.mkv"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "escape"), outside);
        var through = Path.Combine(_root, "escape", "victim.mkv");
        Assert.Equal(PathRefusal.SymlinkInPath, Guard.CheckDeletableFile(through, _probe.Probe(through)).Refusal);
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public void Refuses_a_symlink_target()
    {
        var real = TestRig.Write(Path.Combine(_base, "real.mkv"));
        var link = Path.Combine(_root, "link.mkv");
        File.CreateSymbolicLink(link, real);
        Assert.Equal(PathRefusal.TargetIsLink, Guard.CheckDeletableFile(link, _probe.Probe(link)).Refusal);
    }

    [Fact]
    public void Refuses_a_directory_as_a_file_target()
    {
        var dir = Path.Combine(_root, "dir");
        Directory.CreateDirectory(dir);
        Assert.Equal(PathRefusal.NotARegularFile, Guard.CheckDeletableFile(dir, _probe.Probe(dir)).Refusal);
    }

    [Fact]
    public void Reports_an_unavailable_root_rather_than_a_missing_file()
    {
        var gone = Path.Combine(_base, "unmounted");
        var guard = new PathGuard([gone], StringComparison.Ordinal);
        Assert.Equal(PathRefusal.RootUnavailable, guard.CheckLocation(Path.Combine(gone, "a.mkv")).Refusal);
    }

    [Fact]
    public void A_root_that_is_itself_a_symlink_is_allowed()
    {
        var realRoot = Path.Combine(_base, "real-root");
        var file = TestRig.Write(Path.Combine(realRoot, "a.mkv"));
        var alias = Path.Combine(_base, "alias");
        Directory.CreateSymbolicLink(alias, realRoot);
        var guard = new PathGuard([alias], StringComparison.Ordinal);
        var viaAlias = Path.Combine(alias, "a.mkv");
        Assert.True(guard.CheckDeletableFile(viaAlias, _probe.Probe(viaAlias)).IsSafe);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Comparison_mode_decides_case_sensitivity()
    {
        var file = TestRig.Write(Path.Combine(_root, "a.mkv"));
        var other = file.Replace("library", "LIBRARY");
        Assert.Equal(PathRefusal.OutsideApprovedRoots, new PathGuard([_root], StringComparison.Ordinal).CheckLocation(other).Refusal);
        Assert.NotEqual(PathRefusal.OutsideApprovedRoots, new PathGuard([_root], StringComparison.OrdinalIgnoreCase).CheckLocation(other).Refusal);
    }
}

public class SidecarDiscoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jt-side-" + Guid.NewGuid().ToString("N"));

    public SidecarDiscoveryTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string F(string name) => TestRig.Write(Path.Combine(_dir, name), 1);

    private static string[] Names(IEnumerable<string> paths) => paths.Select(p => Path.GetFileName(p)!).ToArray();

    [Fact]
    public void Claims_exact_stem_sidecars_only()
    {
        var movie = F("Foo.mkv");
        F("Foo.srt");
        F("Foo.en.srt");
        F("Foo-poster.jpg");
        F("Foo.nfo");
        F("Foo 2.srt");
        F("Foobar.srt");
        F("Bar.srt");
        F("notes.txt");
        F("Foo.txt");
        var scan = SidecarDiscovery.Discover(_dir, [movie], [], includeFolderArtwork: false);
        Assert.Equal(["Foo-poster.jpg", "Foo.en.srt", "Foo.nfo", "Foo.srt"], Names(scan.Sidecars));
        Assert.Contains("Foo 2.srt", Names(scan.Unplanned));
        Assert.Contains("Foobar.srt", Names(scan.Unplanned));
        Assert.Contains("notes.txt", Names(scan.Unplanned));
        Assert.Contains("Foo.txt", Names(scan.Unplanned));
    }

    [Fact]
    public void Never_claims_a_neighbors_sidecar_with_a_longer_stem()
    {
        var a = F("Show-1.mkv");
        var b = F("Show-1.director.mkv");
        F("Show-1.director.srt");
        F("Show-1.srt");
        var scan = SidecarDiscovery.Discover(_dir, [a], [b], includeFolderArtwork: false);
        Assert.Equal(["Show-1.srt"], Names(scan.Sidecars));
        Assert.Contains("Show-1.director.srt", Names(scan.Unplanned));
    }

    [Fact]
    public void Folder_artwork_requires_explicit_inclusion()
    {
        var movie = F("Foo.mkv");
        F("poster.jpg");
        F("movie.nfo");
        Assert.Empty(SidecarDiscovery.Discover(_dir, [movie], [], includeFolderArtwork: false).Sidecars);
        Assert.Equal(["movie.nfo", "poster.jpg"], Names(SidecarDiscovery.Discover(_dir, [movie], [], includeFolderArtwork: true).Sidecars));
    }

    [Fact]
    public void Extras_directories_stay_unplanned_but_trickplay_is_claimed()
    {
        var movie = F("Foo.mkv");
        TestRig.Write(Path.Combine(_dir, "extras", "making-of.mkv"), 1);
        TestRig.Write(Path.Combine(_dir, "Foo.trickplay", "320", "0.jpg"), 1);
        var scan = SidecarDiscovery.Discover(_dir, [movie], [], includeFolderArtwork: true);
        Assert.Equal(["0.jpg"], Names(scan.Sidecars));
        Assert.Contains("extras", Names(scan.Unplanned));
        Assert.True(scan.HasSubdirectories);
    }

    [Fact]
    public void Links_are_never_claimed()
    {
        var movie = F("Foo.mkv");
        var target = F("real.srt");
        File.CreateSymbolicLink(Path.Combine(_dir, "Foo.srt"), target);
        var scan = SidecarDiscovery.Discover(_dir, [movie], [], includeFolderArtwork: false);
        Assert.DoesNotContain("Foo.srt", Names(scan.Sidecars));
        Assert.Contains("Foo.srt", Names(scan.Unplanned));
    }
}
