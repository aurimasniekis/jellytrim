using Jellytrim.Core.Abstractions;
using Jellytrim.Core.Cleanup;
using Jellytrim.Core.Domain;
using Jellytrim.Core.Inventory;
using Jellytrim.Core.Persistence;
using Jellytrim.Core.Policies;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellytrim.Core.Tests;

public class InventoryScanTests : IDisposable
{
    private readonly TestRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task Scan_indexes_items_files_and_rollups()
    {
        _rig.AddMovie("Alpha", 4000);
        _rig.AddShow("Show", 1, 3, 1000);
        await _rig.ScanAsync();
        var libs = _rig.Inventory.ListLibraries();
        var movies = libs.Single(l => l.Id == TestRig.MoviesLib);
        var tv = libs.Single(l => l.Id == TestRig.TvLib);
        Assert.Equal(4000, movies.LogicalBytes);
        Assert.Equal(1, movies.ItemCount);
        Assert.Equal(3000, tv.LogicalBytes);
        var series = _rig.Explorer.Query(new ExplorerQuery { Kinds = [MediaKind.Series] }).Rows.Single();
        Assert.Equal(3000, series.LogicalBytes);
        Assert.Equal(3000, series.UniqueBytes);
        Assert.Equal(1, series.Children);
    }

    [Fact]
    public async Task Hard_links_across_libraries_are_each_counted_but_not_within_a_library()
    {
        var a = _rig.AddMovie("Alpha", 5000);
        var linked = Path.Combine(_rig.MoviesRoot, "Beta", "Beta.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(linked)!);
        System.Diagnostics.Process.Start("ln", [a.Path!, linked]).WaitForExit();
        var id = Guid.NewGuid();
        _rig.Host.Items[id] = new HostItem { Id = id, Kind = MediaKind.Movie, Name = "Beta", LibraryId = TestRig.MoviesLib, IsFileBacked = true, Files = [new HostItemFile(linked, FileRole.Main, id)], VersionIds = [id] };
        await _rig.ScanAsync();
        var movies = _rig.Inventory.ListLibraries().Single(l => l.Id == TestRig.MoviesLib);
        Assert.Equal(10000, movies.LogicalBytes);
        Assert.Equal(5000, movies.UniqueBytes);
    }

    [Fact]
    public async Task Missing_file_with_root_online_is_marked_stale_not_eligible()
    {
        var a = _rig.AddMovie("Alpha");
        var keep = _rig.AddMovie("Keep");
        File.Delete(a.Path!);
        await _rig.ScanAsync();
        Assert.Equal(ItemHealth.Missing, _rig.Explorer.GetRow(a.Id, null)!.Health);
        Assert.Equal(ItemHealth.Ok, _rig.Explorer.GetRow(keep.Id, null)!.Health);
    }

    [Fact]
    public async Task An_offline_root_never_removes_items_or_claims_they_are_gone()
    {
        var a = _rig.AddMovie("Alpha");
        await _rig.ScanAsync();
        Assert.Equal(1, _rig.Inventory.ListLibraries().Single(l => l.Id == TestRig.MoviesLib).ItemCount);

        var hidden = _rig.MoviesRoot + ".unmounted";
        Directory.Move(_rig.MoviesRoot, hidden);
        var outcome = await _rig.Scanner.RunAsync("test", null, CancellationToken.None);
        Assert.Equal("CompletedWithWarnings", outcome.Status);
        Assert.Contains(outcome.Warnings, w => w.Contains("unavailable"));

        var lib = _rig.Inventory.ListLibraries().Single(l => l.Id == TestRig.MoviesLib);
        Assert.False(lib.Online);
        Assert.Equal(1, lib.ItemCount);
        Assert.NotNull(_rig.Explorer.GetRow(a.Id, null));
        Directory.Move(hidden, _rig.MoviesRoot);
    }

    [Fact]
    public async Task An_empty_mount_point_is_treated_as_offline_when_content_was_indexed()
    {
        var a = _rig.AddMovie("Alpha");
        await _rig.ScanAsync();

        // An unmounted mount point looks like an existing empty directory.
        var hidden = _rig.MoviesRoot + ".real";
        Directory.Move(_rig.MoviesRoot, hidden);
        Directory.CreateDirectory(_rig.MoviesRoot);
        await _rig.ScanAsync();

        var lib = _rig.Inventory.ListLibraries().Single(l => l.Id == TestRig.MoviesLib);
        Assert.False(lib.Online);
        Assert.Contains("mount", lib.OfflineDetail);
        Assert.NotNull(_rig.Explorer.GetRow(a.Id, null));
    }

    [Fact]
    public async Task One_offline_root_of_several_blocks_the_sweep_and_marks_its_files_unavailable()
    {
        var second = Path.Combine(_rig.Sandbox, "media", "movies2");
        Directory.CreateDirectory(second);
        _rig.Host.Libraries[0] = new HostLibrary(TestRig.MoviesLib, "Movies", "movies", [_rig.MoviesRoot, second]);
        var onOffline = TestRig.Write(Path.Combine(second, "Far", "Far.mkv"));
        var farId = Guid.NewGuid();
        _rig.Host.Items[farId] = new HostItem { Id = farId, Kind = MediaKind.Movie, Name = "Far", LibraryId = TestRig.MoviesLib, IsFileBacked = true, Files = [new HostItemFile(onOffline, FileRole.Main, farId)], VersionIds = [farId] };
        var gone = _rig.AddMovie("Gone");
        var near = _rig.AddMovie("Near");
        await _rig.ScanAsync();

        Directory.Move(second, second + ".offline");
        _rig.Host.Items.Remove(gone.Id); // Unseen this round, but the library was not fully read, so it must survive.
        await _rig.ScanAsync();

        Assert.False(_rig.Inventory.ListLibraries().Single(l => l.Id == TestRig.MoviesLib).Online);
        Assert.Equal(ItemHealth.Unavailable, _rig.Explorer.GetRow(farId, null)!.Health);
        Assert.Equal(ItemHealth.Ok, _rig.Explorer.GetRow(near.Id, null)!.Health);
        Assert.NotNull(_rig.Explorer.GetRow(gone.Id, null));
    }

    [Fact]
    public async Task An_empty_new_library_is_online()
    {
        await _rig.ScanAsync();
        Assert.True(_rig.Inventory.ListLibraries().All(l => l.Online));
    }

    [Fact]
    public async Task Files_removed_from_the_server_are_swept_only_after_a_complete_scan()
    {
        var a = _rig.AddMovie("Alpha");
        var b = _rig.AddMovie("Beta");
        await _rig.ScanAsync();
        _rig.Host.Items.Remove(a.Id);
        await _rig.ScanAsync();
        Assert.Null(_rig.Explorer.GetRow(a.Id, null));
        Assert.NotNull(_rig.Explorer.GetRow(b.Id, null));
    }

    [Fact]
    public async Task Cancelled_scan_keeps_previous_data_and_can_be_rerun()
    {
        var a = _rig.AddMovie("Alpha");
        await _rig.ScanAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => _rig.Scanner.RunAsync("test", null, cts.Token));
        Assert.NotNull(_rig.Explorer.GetRow(a.Id, null));
        Assert.Equal("Cancelled", _rig.Inventory.LatestScan()!.Status);
        Assert.Null(_rig.Gate.Holder);
        await _rig.ScanAsync();
    }

    [Fact]
    public async Task Interrupted_scan_row_is_closed_by_the_next_scan()
    {
        _rig.Inventory.BeginScan("crashed", _rig.Clock.Now);
        await _rig.ScanAsync();
        Assert.Equal("Completed", _rig.Inventory.LatestScan()!.Status);
        Assert.Equal(1, _rig.Db.Read(c => c.Cmd("SELECT COUNT(*) FROM scans WHERE status='Interrupted'").Scalar()));
    }

    [Fact]
    public async Task Overlapping_scans_are_refused()
    {
        Assert.True(_rig.Gate.TryEnter(InventoryScanner.GateName));
        var e = await Assert.ThrowsAsync<Jellytrim.Core.Services.JellytrimException>(_rig.ScanAsync);
        Assert.Equal(Jellytrim.Core.Services.ErrorCodes.ScanRunning, e.Code);
        _rig.Gate.Exit(InventoryScanner.GateName);
    }

    [Fact]
    public async Task User_state_failure_produces_unknown_never_unplayed()
    {
        var a = _rig.AddMovie("Alpha");
        _rig.MarkPlayed(a.Id, TestRig.Ann);
        _rig.Host.FailUserRead.Add(TestRig.Bob);
        var policy = _rig.SavePolicy();
        var outcome = await _rig.Scanner.RunAsync("test", null, CancellationToken.None);
        Assert.Contains(outcome.Warnings, w => w.Contains("could not be read"));

        var states = _rig.Inventory.GetItemStates(a.Id);
        Assert.Equal(WatchState.Unknown, states.Single(s => s.UserId == TestRig.Bob).State);
        var evaluation = _rig.Evaluations.Explain(policy, a.Id, ActivePlayback.None)!;
        Assert.False(evaluation.Eligible);
        Assert.Contains(evaluation.Rules, r => r.Outcome == RuleOutcome.Unknown);
    }

    [Fact]
    public async Task A_user_deleted_from_the_server_becomes_unknown_in_policies()
    {
        var a = _rig.AddMovie("Alpha");
        _rig.MarkPlayedByAll(a.Id);
        var policy = _rig.SavePolicy();
        await _rig.ScanAsync();
        Assert.True(_rig.Evaluations.Explain(policy, a.Id, ActivePlayback.None)!.Eligible);

        _rig.Host.Users.RemoveAll(u => u.Id == TestRig.Bob);
        await _rig.ScanAsync();
        var evaluation = _rig.Evaluations.Explain(policy, a.Id, ActivePlayback.None)!;
        Assert.False(evaluation.Eligible);
        Assert.DoesNotContain(_rig.Inventory.ListUsers(), u => u.Id == TestRig.Bob);
    }

    [Fact]
    public async Task Default_state_stores_no_rows_to_minimize_viewing_data()
    {
        var a = _rig.AddMovie("Alpha");
        _rig.AddMovie("Beta");
        _rig.MarkPlayed(a.Id, TestRig.Ann);
        await _rig.ScanAsync();
        Assert.Equal(1, _rig.Db.Read(c => c.Cmd("SELECT COUNT(*) FROM item_user_state").Scalar()));
    }

    [Fact]
    public async Task Snapshots_only_record_what_the_plugin_observed()
    {
        _rig.AddMovie("Alpha", 1000);
        Assert.Empty(_rig.Inventory.ReadSnapshots(DateTime.MinValue).Libraries);
        await _rig.ScanAsync();
        var (libs, vols) = _rig.Inventory.ReadSnapshots(DateTime.MinValue);
        Assert.Equal(2, libs.Count);
        Assert.NotEmpty(vols);
        Assert.All(libs, p => Assert.Equal(_rig.Clock.Now, p.TakenUtc));
    }

    [Fact]
    public async Task Two_libraries_on_one_volume_report_a_single_volume()
    {
        _rig.AddMovie("Alpha");
        _rig.AddShow("Show", 1, 1);
        await _rig.ScanAsync();
        var volume = Assert.Single(_rig.Inventory.ListVolumes());
        Assert.Equal(2, volume.LibraryIds.Count);
    }

    [Fact]
    public async Task Removed_library_disappears_with_its_items()
    {
        var a = _rig.AddMovie("Alpha");
        await _rig.ScanAsync();
        _rig.Host.Libraries.RemoveAll(l => l.Id == TestRig.MoviesLib);
        _rig.Host.Items.Remove(a.Id);
        await _rig.ScanAsync();
        Assert.DoesNotContain(_rig.Inventory.ListLibraries(), l => l.Id == TestRig.MoviesLib);
        Assert.Null(_rig.Explorer.GetRow(a.Id, null));
    }
}

public class ExplorerTests : IDisposable
{
    private readonly TestRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task Pagination_sorting_and_totals_are_stable()
    {
        foreach (var (name, size) in new[] { ("Small", 100), ("Large", 300), ("Mid", 200) })
        {
            _rig.AddMovie(name, size);
        }

        await _rig.ScanAsync();
        var first = _rig.Explorer.Query(new ExplorerQuery { Kinds = [MediaKind.Movie], Limit = 2 });
        Assert.Equal(3, first.Total);
        Assert.Equal(["Large", "Mid"], first.Rows.Select(r => r.Name));
        var second = _rig.Explorer.Query(new ExplorerQuery { Kinds = [MediaKind.Movie], Limit = 2, Offset = 2 });
        Assert.Equal(["Small"], second.Rows.Select(r => r.Name));
        var byName = _rig.Explorer.Query(new ExplorerQuery { Kinds = [MediaKind.Movie], Sort = ExplorerSort.Name, Descending = false });
        Assert.Equal(["Large", "Mid", "Small"], byName.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task Search_is_parameterized_and_treats_wildcards_literally()
    {
        _rig.AddMovie("Robert'); DROP TABLE items;--");
        _rig.AddMovie("100%");
        _rig.AddMovie("1000");
        await _rig.ScanAsync();
        Assert.Single(_rig.Explorer.Query(new ExplorerQuery { Search = "drop table" }).Rows);
        Assert.Equal(["100%"], _rig.Explorer.Query(new ExplorerQuery { Search = "100%" }).Rows.Select(r => r.Name));
        Assert.Empty(_rig.Explorer.Query(new ExplorerQuery { Search = "'; DELETE FROM items; --" }).Rows);
        Assert.Equal(3, _rig.Explorer.Query(new ExplorerQuery { Kinds = [MediaKind.Movie] }).Total);
    }

    [Fact]
    public async Task Hierarchy_series_to_seasons_to_episodes()
    {
        var (series, season, eps) = _rig.AddShow("Show", 1, 2);
        await _rig.ScanAsync();
        var seasons = _rig.Explorer.Query(new ExplorerQuery { ParentId = series.Id });
        Assert.Equal(season.Id, seasons.Rows.Single().Id);
        var episodes = _rig.Explorer.Query(new ExplorerQuery { ParentId = season.Id });
        Assert.Equal(eps.Select(e => e.Id).ToHashSet(), episodes.Rows.Select(r => r.Id).ToHashSet());
    }

    [Theory]
    [InlineData(WatchState.Played, UserMatch.All, "AB")]
    [InlineData(WatchState.Played, UserMatch.Any, "AB,A")]
    [InlineData(WatchState.Unplayed, UserMatch.All, "none")]
    [InlineData(WatchState.Unplayed, UserMatch.Any, "A,none,P")]
    [InlineData(WatchState.Partial, UserMatch.Any, "P")]
    public async Task Watch_filters_distinguish_someone_from_everyone(WatchState state, UserMatch match, string expected)
    {
        var ab = _rig.AddMovie("AB");
        var a = _rig.AddMovie("A");
        _rig.AddMovie("none");
        var p = _rig.AddMovie("P");
        _rig.MarkPlayedByAll(ab.Id);
        _rig.MarkPlayed(a.Id, TestRig.Ann);
        _rig.Host.States[(p.Id, TestRig.Bob)] = new UserItemState(WatchState.Partial, 500);
        await _rig.ScanAsync();
        var rows = _rig.Explorer.Query(new ExplorerQuery { Kinds = [MediaKind.Movie], UserIds = [TestRig.Ann, TestRig.Bob], UserMatch = match, WatchState = state }).Rows;
        Assert.Equal(expected.Split(',').OrderBy(x => x), rows.Select(r => r.Name).OrderBy(x => x));
    }

    [Fact]
    public async Task An_unknown_user_never_satisfies_played_by_all_and_shows_unknown()
    {
        var a = _rig.AddMovie("A");
        _rig.MarkPlayed(a.Id, TestRig.Ann);
        await _rig.ScanAsync();
        var ghost = Guid.NewGuid();
        var query = new ExplorerQuery { Kinds = [MediaKind.Movie], UserIds = [TestRig.Ann, ghost], UserMatch = UserMatch.All, WatchState = WatchState.Played };
        Assert.Empty(_rig.Explorer.Query(query).Rows);
        var row = _rig.Explorer.Query(new ExplorerQuery { Kinds = [MediaKind.Movie], UserIds = [ghost] }).Rows.Single();
        Assert.Equal(WatchState.Unknown, row.UserStates.Single().State);
    }

    [Fact]
    public async Task Show_rows_report_each_users_episode_progress()
    {
        var (series, season, eps) = _rig.AddShow("Show", 1, 3);
        _rig.MarkPlayedByAll(eps[0].Id);
        _rig.MarkPlayedByAll(eps[1].Id);
        _rig.MarkPlayed(eps[2].Id, TestRig.Ann, daysAgo: 5);
        _rig.Host.States[(series.Id, TestRig.Bob)] = new UserItemState(WatchState.Unplayed, IsFavorite: true);
        await _rig.ScanAsync();

        var row = _rig.Explorer.Query(new ExplorerQuery { Kinds = [MediaKind.Series], UserIds = [TestRig.Ann, TestRig.Bob] }).Rows.Single();
        var ann = row.UserStates.Single(s => s.UserId == TestRig.Ann);
        var bob = row.UserStates.Single(s => s.UserId == TestRig.Bob);
        Assert.Equal((WatchState.Played, 3, 3), (ann.State, ann.Episodes!.Played, ann.Episodes.Total));
        Assert.Equal(_rig.Clock.Now.AddDays(-5), ann.LastPlayedUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal((WatchState.Partial, 2, 3), (bob.State, bob.Episodes!.Played, bob.Episodes.Total));
        Assert.True(bob.IsFavorite);

        // The detail panel agrees, for the series and for its season.
        foreach (var id in new[] { series.Id, season.Id })
        {
            var states = _rig.Inventory.GetItemStates(id);
            Assert.Equal(WatchState.Played, states.Single(s => s.UserId == TestRig.Ann).State);
            Assert.Equal((WatchState.Partial, 2), (states.Single(s => s.UserId == TestRig.Bob).State, states.Single(s => s.UserId == TestRig.Bob).Episodes!.Played));
        }
    }

    [Fact]
    public async Task A_rewatch_in_progress_keeps_a_finished_show_in_progress_and_nothing_watched_stays_unplayed()
    {
        var (_, _, eps) = _rig.AddShow("Show", 1, 2);
        _rig.MarkPlayed(eps[0].Id, TestRig.Ann);
        // A rewatch: the adapter turns Played plus a resume position into Partial (UserStateMerge).
        _rig.Host.States[(eps[1].Id, TestRig.Ann)] = new UserItemState(WatchState.Partial, 500, 1);
        await _rig.ScanAsync();
        var row = _rig.Explorer.Query(new ExplorerQuery { Kinds = [MediaKind.Series], UserIds = [TestRig.Ann, TestRig.Bob] }).Rows.Single();
        var ann = row.UserStates.Single(s => s.UserId == TestRig.Ann);
        Assert.Equal((WatchState.Partial, 1, 1), (ann.State, ann.Episodes!.Played, ann.Episodes.InProgress));
        var bob = row.UserStates.Single(s => s.UserId == TestRig.Bob);
        Assert.Equal((WatchState.Unplayed, 0, 2), (bob.State, bob.Episodes!.Played, bob.Episodes.Total));
        Assert.Null(bob.LastPlayedUtc);
    }

    [Fact]
    public void Show_progress_is_unknown_when_nothing_is_known_watched_but_an_episode_could_not_be_read()
    {
        Assert.Equal(WatchState.Unknown, new EpisodeProgress(0, 0, 1, 3, null, 0).State);
        Assert.Equal(WatchState.Partial, new EpisodeProgress(1, 0, 1, 3, null, 1).State);
        Assert.Equal(WatchState.Played, new EpisodeProgress(3, 0, 0, 3, null, 3).State);
        Assert.Equal(WatchState.Unplayed, new EpisodeProgress(0, 0, 0, 0, null, 0).State);
    }

    [Fact]
    public async Task Season_counts_as_played_only_when_every_episode_is()
    {
        var (_, season, eps) = _rig.AddShow("Show", 1, 2);
        _rig.MarkPlayedByAll(eps[0].Id);
        await _rig.ScanAsync();
        var query = new ExplorerQuery { Kinds = [MediaKind.Season], UserIds = [TestRig.Ann], WatchState = WatchState.Played };
        Assert.Empty(_rig.Explorer.Query(query).Rows);
        _rig.MarkPlayedByAll(eps[1].Id);
        await _rig.ScanAsync();
        Assert.Equal(season.Id, _rig.Explorer.Query(query).Rows.Single().Id);
    }

    [Fact]
    public async Task Filters_for_size_dates_protection_and_eligibility()
    {
        var oldMovie = _rig.AddMovie("Old", 500, added: _rig.Clock.Now.AddDays(-500));
        var newMovie = _rig.AddMovie("New", 2000, added: _rig.Clock.Now.AddDays(-5));
        _rig.MarkPlayedByAll(oldMovie.Id);
        var policy = _rig.SavePolicy();
        await _rig.ScanAsync();
        Assert.Equal(["New"], _rig.Explorer.Query(new ExplorerQuery { MinBytes = 1000 }).Rows.Select(r => r.Name));
        Assert.Equal(["Old"], _rig.Explorer.Query(new ExplorerQuery { MaxBytes = 1000 }).Rows.Select(r => r.Name));
        Assert.Equal(["Old"], _rig.Explorer.Query(new ExplorerQuery { AddedBeforeUtc = _rig.Clock.Now.AddDays(-100) }).Rows.Select(r => r.Name));
        Assert.Equal(["New"], _rig.Explorer.Query(new ExplorerQuery { AddedAfterUtc = _rig.Clock.Now.AddDays(-100) }).Rows.Select(r => r.Name));
        Assert.Equal(["Old"], _rig.Explorer.Query(new ExplorerQuery { PolicyId = policy.Id, Eligible = true }).Rows.Select(r => r.Name));
        Assert.Equal(["New"], _rig.Explorer.Query(new ExplorerQuery { PolicyId = policy.Id, Eligible = false }).Rows.Select(r => r.Name));
        _rig.Policies.AddProtection(new Protection(newMovie.Id, ProtectionTarget.Item, "New", null, _rig.Clock.Now, "a"));
        Assert.Equal(["New"], _rig.Explorer.Query(new ExplorerQuery { Protected = true }).Rows.Select(r => r.Name));
        Assert.Equal(["Old"], _rig.Explorer.Query(new ExplorerQuery { Protected = false }).Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task Protection_on_a_show_shows_as_protected_on_episodes()
    {
        var (series, _, eps) = _rig.AddShow("Show", 1, 1);
        await _rig.ScanAsync();
        _rig.Policies.AddProtection(new Protection(series.Id, ProtectionTarget.Item, "Show", null, _rig.Clock.Now, "a"));
        Assert.True(_rig.Explorer.GetRow(eps[0].Id, null)!.Protected);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(201, 0)]
    [InlineData(50, -1)]
    public void Query_bounds_are_validated(int limit, int offset) =>
        Assert.NotEmpty(new ExplorerQuery { Limit = limit, Offset = offset }.Validate());

    [Fact]
    public void Watch_filter_needs_users() => Assert.NotEmpty(new ExplorerQuery { WatchState = WatchState.Played }.Validate());
}

public class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "jt-db-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "x.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void Migrates_from_empty_and_is_idempotent()
    {
        var db = new JellytrimDb(DbPath, NullLogger.Instance);
        db.Initialize();
        Assert.Equal(Migrations.Latest, db.SchemaVersion());
        db.Initialize();
        Assert.Equal(Migrations.Latest, db.SchemaVersion());
    }

    [Fact]
    public void Refuses_a_newer_database_and_leaves_it_untouched()
    {
        var db = new JellytrimDb(DbPath, NullLogger.Instance);
        db.Initialize();
        db.Write((c, tx) => c.Cmd("CREATE TABLE future(x)", tx).Exec());
        using (var c = db.Open())
        {
            c.Cmd($"PRAGMA user_version={Migrations.Latest + 1}").Exec();
        }

        Assert.Throws<DatabaseTooNewException>(db.Initialize);
        Assert.Equal(Migrations.Latest + 1, db.SchemaVersion());
    }

    [Fact]
    public void Data_survives_reopen()
    {
        var db = new JellytrimDb(DbPath, NullLogger.Instance);
        db.Initialize();
        new SettingsStore(db).Save(new JellytrimSettings { PlanTtlMinutes = 7 }, 0, DateTime.UtcNow);
        db.ReleaseHandles();
        var again = new JellytrimDb(DbPath, NullLogger.Instance);
        again.Initialize();
        Assert.Equal(7, new SettingsStore(again).Get().Value.PlanTtlMinutes);
    }

    [Fact]
    public void Settings_use_compare_and_swap()
    {
        var db = new JellytrimDb(DbPath, NullLogger.Instance);
        db.Initialize();
        var store = new SettingsStore(db);
        Assert.Equal(1, store.Save(new JellytrimSettings(), 0, DateTime.UtcNow));
        Assert.Throws<ConcurrencyConflictException>(() => store.Save(new JellytrimSettings(), 0, DateTime.UtcNow));
        Assert.Equal(2, store.Save(new JellytrimSettings(), 1, DateTime.UtcNow));
    }

    [Fact]
    public void Policy_updates_use_compare_and_swap_and_invalidate_results()
    {
        var db = new JellytrimDb(DbPath, NullLogger.Instance);
        db.Initialize();
        var store = new PolicyStore(db);
        var p = store.Create(new CleanupPolicy { Name = "a", ParticipatingUserIds = [Guid.NewGuid()] }, DateTime.UtcNow);
        store.SaveRun(new PolicyRun(p.Id, 1, 1, 0, DateTime.UtcNow, 0, 0, 0, 0, new Dictionary<string, int>()), []);
        Assert.NotNull(store.GetRun(p.Id));
        store.Update(p, 1, DateTime.UtcNow);
        Assert.Null(store.GetRun(p.Id));
        Assert.Throws<ConcurrencyConflictException>(() => store.Update(p, 1, DateTime.UtcNow));
    }

    [Fact]
    public void Audit_prunes_by_its_own_retention()
    {
        var db = new JellytrimDb(DbPath, NullLogger.Instance);
        db.Initialize();
        var audit = new AuditStore(db);
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        audit.Record(now.AddDays(-400), "a", "old", null, null, "ok");
        audit.Record(now, "a", "new", null, null, "ok");
        Assert.Equal(1, audit.Prune(now.AddDays(-365)));
        Assert.Equal("new", audit.List(0, 10).Rows.Single().Action);
    }

    [Fact]
    public void Concurrent_writers_serialize_without_errors()
    {
        var db = new JellytrimDb(DbPath, NullLogger.Instance);
        db.Initialize();
        var audit = new AuditStore(db);
        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => audit.Record(DateTime.UtcNow, "a", "x" + i, null, null, "ok"));
        Assert.Equal(200, audit.List(0, 1).Total);
    }

    [Theory]
    [InlineData(JobStatus.Queued, JobStatus.Preflight, true)]
    [InlineData(JobStatus.Preflight, JobStatus.Running, true)]
    [InlineData(JobStatus.Preflight, JobStatus.Rejected, true)]
    [InlineData(JobStatus.Running, JobStatus.Completed, true)]
    [InlineData(JobStatus.Running, JobStatus.Interrupted, true)]
    [InlineData(JobStatus.Queued, JobStatus.Completed, false)]
    [InlineData(JobStatus.Completed, JobStatus.Running, false)]
    [InlineData(JobStatus.Interrupted, JobStatus.Running, false)]
    [InlineData(JobStatus.Rejected, JobStatus.Running, false)]
    public void Job_transitions(string from, string to, bool allowed) => Assert.Equal(allowed, JobStatus.CanMove(from, to));

    [Theory]
    [InlineData(ItemStatus.Pending, ItemStatus.InProgress, true)]
    [InlineData(ItemStatus.InProgress, ItemStatus.Deleted, true)]
    [InlineData(ItemStatus.InProgress, ItemStatus.Uncertain, true)]
    [InlineData(ItemStatus.Deleted, ItemStatus.Pending, false)]
    [InlineData(ItemStatus.Uncertain, ItemStatus.InProgress, false)]
    public void Item_transitions(string from, string to, bool allowed) => Assert.Equal(allowed, ItemStatus.CanMove(from, to));
}

public class SettingsTests
{
    [Fact]
    public void Defaults_are_read_only_with_no_roots()
    {
        var s = new JellytrimSettings();
        Assert.False(s.DeletionEnabled);
        Assert.Empty(s.ApprovedRoots);
        Assert.Empty(s.Validate());
    }

    [Fact]
    public void Validation_bounds()
    {
        Assert.NotEmpty(new JellytrimSettings { PlanTtlMinutes = 0 }.Validate());
        Assert.NotEmpty(new JellytrimSettings { MaxPlanItems = 0 }.Validate());
        Assert.NotEmpty(new JellytrimSettings { AuditRetentionDays = 1 }.Validate());
        Assert.NotEmpty(new JellytrimSettings { LibraryBudgets = [new LibraryBudget { BudgetBytes = 0 }] }.Validate());
    }
}
