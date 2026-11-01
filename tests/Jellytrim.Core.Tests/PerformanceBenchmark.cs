using System.Diagnostics;
using Jellytrim.Core.Abstractions;
using Jellytrim.Core.Cleanup;
using Jellytrim.Core.Domain;
using Jellytrim.Core.FileSystem;
using Jellytrim.Core.Inventory;
using Jellytrim.Core.Persistence;
using Jellytrim.Core.Policies;
using Jellytrim.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellytrim.Core.Tests;

/// <summary>
/// A 100,000-item library through the real scanner and SQLite, with a stubbed stat. Run with JELLYTRIM_BENCH=1.
/// Excluded from normal runs: it takes a while and timing asserts on shared CI machines are noisy.
/// </summary>
public class PerformanceBenchmark
{
    private sealed class StatStub : IFileProbe
    {
        public FileObservation Probe(string path) => new(ProbeStatus.Ok, true, false, "1:" + path.GetHashCode(StringComparison.Ordinal), "1", 1_000_000_000, 1_000_000_000, 1, 1);
    }

    [Fact]
    public async Task Hundred_thousand_items()
    {
        if (Environment.GetEnvironmentVariable("JELLYTRIM_BENCH") != "1")
        {
            return;
        }

        const int Shows = 2_000, EpisodesPerShow = 40, Movies = 18_000, UserCount = 10;
        using var rig = new TestRig();
        var ids = Enumerable.Range(0, UserCount).Select(_ => Guid.NewGuid()).ToList();
        rig.Host.Users.Clear();
        rig.Host.Users.AddRange(ids.Select((u, n) => new HostUser(u, "user" + n, false, false, true, [])));
        var random = new Random(42);
        var movies = rig.MoviesRoot;
        for (var m = 0; m < Movies; m++)
        {
            var id = Guid.NewGuid();
            var path = $"{movies}/Movie {m}/Movie {m}.mkv";
            rig.Host.Items[id] = new HostItem { Id = id, Kind = MediaKind.Movie, Name = "Movie " + m, LibraryId = TestRig.MoviesLib, DateAddedUtc = rig.Clock.Now.AddDays(-random.Next(1000)), IsFileBacked = true, Path = path, Files = [new HostItemFile(path, FileRole.Main, id)], VersionIds = [id] };
        }

        for (var s = 0; s < Shows; s++)
        {
            var seriesId = Guid.NewGuid();
            var seasonId = Guid.NewGuid();
            rig.Host.Items[seriesId] = new HostItem { Id = seriesId, Kind = MediaKind.Series, Name = "Show " + s, LibraryId = TestRig.TvLib, VersionIds = [seriesId] };
            rig.Host.Items[seasonId] = new HostItem { Id = seasonId, Kind = MediaKind.Season, Name = "Season 1", LibraryId = TestRig.TvLib, SeriesId = seriesId, SeasonNumber = 1, VersionIds = [seasonId] };
            for (var e = 1; e <= EpisodesPerShow; e++)
            {
                var id = Guid.NewGuid();
                var path = $"{rig.TvRoot}/Show {s}/Season 01/E{e}.mkv";
                rig.Host.Items[id] = new HostItem { Id = id, Kind = MediaKind.Episode, Name = "E" + e, LibraryId = TestRig.TvLib, SeriesId = seriesId, SeasonId = seasonId, SeriesName = "Show " + s, SeasonNumber = 1, EpisodeNumber = e, DateAddedUtc = rig.Clock.Now.AddDays(-random.Next(1000)), IsFileBacked = true, Path = path, Files = [new HostItemFile(path, FileRole.Main, id)], VersionIds = [id] };
            }
        }

        foreach (var item in rig.Host.Items.Values.Where(i => i.Kind is MediaKind.Movie or MediaKind.Episode))
        {
            foreach (var user in ids.Take(3))
            {
                if (random.NextDouble() < 0.4)
                {
                    rig.Host.States[(item.Id, user)] = new UserItemState(WatchState.Played, 0, 1, rig.Clock.Now.AddDays(-random.Next(400)));
                }
            }
        }

        // The real filesystem is stubbed, but roots must hold something or the empty-mount protection rightly skips them.
        TestRig.Write(Path.Combine(rig.MoviesRoot, "placeholder"), 1);
        TestRig.Write(Path.Combine(rig.TvRoot, "placeholder"), 1);
        var results = new List<string>();
        var total = rig.Host.Items.Count;

        var scanner = new InventoryScanner(rig.Host, rig.Inventory, new StatStub(), rig.Volumes, new RootChecker([]), new MaintenanceGate(), rig.Evaluations, rig.Clock, NullLogger<InventoryScanner>.Instance);
        var clock = Stopwatch.StartNew();
        await scanner.RunAsync("bench", null, CancellationToken.None);
        results.Add($"first scan, {total} items: {clock.ElapsedMilliseconds} ms");

        clock.Restart();
        await scanner.RunAsync("bench", null, CancellationToken.None);
        results.Add($"rescan: {clock.ElapsedMilliseconds} ms");

        var policy = rig.Policies.Create(new CleanupPolicy { Name = "bench", ParticipatingUserIds = ids.Take(3).ToList() }, rig.Clock.Now);
        clock.Restart();
        var run = rig.Evaluations.Evaluate(policy);
        results.Add($"evaluate policy: {clock.ElapsedMilliseconds} ms, {run.EligibleCount} eligible");

        async Task Time(string label, Func<object?> work)
        {
            clock.Restart();
            for (var i = 0; i < 5; i++)
            {
                work();
            }

            results.Add($"{label}: {clock.ElapsedMilliseconds / 5} ms avg");
            await Task.CompletedTask;
        }

        var users = ids.Take(3).ToList();
        await Time("explorer default page", () => rig.Explorer.Query(new ExplorerQuery { Kinds = [MediaKind.Movie] }));
        await Time("explorer deep page", () => rig.Explorer.Query(new ExplorerQuery { Kinds = [MediaKind.Episode], Offset = 70_000 }));
        await Time("explorer title search", () => rig.Explorer.Query(new ExplorerQuery { Search = "show 19" }));
        await Time("explorer watch filter, 3 users, leaves", () => rig.Explorer.Query(new ExplorerQuery { Kinds = [MediaKind.Movie], UserIds = users, WatchState = WatchState.Played, UserMatch = UserMatch.All }));
        await Time("explorer watch filter, 3 users, shows", () => rig.Explorer.Query(new ExplorerQuery { Kinds = [MediaKind.Series], UserIds = users, WatchState = WatchState.Played, UserMatch = UserMatch.Any }));
        await Time("explorer eligibility filter", () => rig.Explorer.Query(new ExplorerQuery { PolicyId = policy.Id, Eligible = true }));
        await Time("watch summaries", () => rig.Inventory.WatchSummaries(users));
        clock.Restart();
        var recommendations = rig.Evaluations.Recommend(policy, 500_000_000_000, ActivePlayback.None);
        results.Add($"recommendations: {clock.ElapsedMilliseconds} ms, {recommendations.Items.Count} picked");
        var dbSize = new FileInfo(rig.Db.Path_).Length + (File.Exists(rig.Db.Path_ + "-wal") ? new FileInfo(rig.Db.Path_ + "-wal").Length : 0);
        results.Add($"database size: {dbSize / 1024 / 1024} MiB");
        results.Add($"managed heap: {GC.GetTotalMemory(true) / 1024 / 1024} MiB after GC, including the bench's in-memory fake library");

        var report = string.Join(Environment.NewLine, results);
        TestContext.Current.SendDiagnosticMessage(report);
        await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("JELLYTRIM_BENCH_OUT") ?? Path.Combine(Path.GetTempPath(), "jellytrim-bench.txt"), report);
    }
}
