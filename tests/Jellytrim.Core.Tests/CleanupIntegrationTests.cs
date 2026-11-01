using Jellytrim.Core.Abstractions;
using Jellytrim.Core.Cleanup;
using Jellytrim.Core.Domain;
using Jellytrim.Core.Persistence;
using Jellytrim.Core.Policies;
using Jellytrim.Core.Services;

namespace Jellytrim.Core.Tests;

/// <summary>Real files, real SQLite, real deletion inside a disposable sandbox. Only the media server is faked.</summary>
public class CleanupIntegrationTests : IDisposable
{
    private readonly TestRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private async Task<(HostItem Movie, CleanupPlan Plan)> OneMoviePlanAsync(string name = "Alpha")
    {
        var movie = _rig.AddMovie(name);
        await _rig.ScanAsync();
        return (movie, _rig.PlanFor(movie.Id));
    }

    [Fact]
    public async Task Executes_exactly_the_planned_scope_and_leaves_everything_else()
    {
        var movie = _rig.AddMovie("Alpha");
        var dir = Path.GetDirectoryName(movie.Path)!;
        var srt = TestRig.Write(Path.Combine(dir, "Alpha.en.srt"));
        var poster = TestRig.Write(Path.Combine(dir, "poster.jpg"));
        var extra = TestRig.Write(Path.Combine(dir, "extras", "behind.mkv"));
        var notes = TestRig.Write(Path.Combine(dir, "notes.txt"));
        await _rig.ScanAsync();

        var plan = _rig.PlanFor(movie.Id);
        var planned = plan.Items.Single().Files.Select(f => f.Path).ToHashSet();
        Assert.Equal(new HashSet<string> { movie.Path!, srt, poster }, planned);

        var job = await _rig.ExecuteAsync(plan);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal(planned, _rig.Deleter.Deleted.ToHashSet());
        Assert.False(File.Exists(movie.Path));
        Assert.True(File.Exists(extra));
        Assert.True(File.Exists(notes));
        Assert.True(Directory.Exists(dir));
        Assert.Contains(movie.Id, _rig.Host.RemovedRecords);
    }

    [Fact]
    public async Task Empty_directories_are_removed_but_never_the_root()
    {
        var movie = _rig.AddMovie("Alpha");
        var flat = _rig.AddMovie("Beta", ownFolder: false);
        await _rig.ScanAsync();
        await _rig.ExecuteAsync(_rig.PlanFor(movie.Id, flat.Id));
        Assert.False(Directory.Exists(Path.GetDirectoryName(movie.Path)));
        Assert.True(Directory.Exists(_rig.MoviesRoot));
    }

    [Fact]
    public async Task Without_sidecars_only_media_is_touched()
    {
        var movie = _rig.AddMovie("Alpha");
        var srt = TestRig.Write(Path.Combine(Path.GetDirectoryName(movie.Path)!, "Alpha.srt"));
        await _rig.ScanAsync();
        var plan = _rig.Planner.CreatePlan(new PlanRequest { Type = SelectionType.Items, ItemIds = [movie.Id], IncludeSidecars = false }, "a", "c").Plan;
        Assert.Single(plan.Items.Single().Files);
        await _rig.ExecuteAsync(plan);
        Assert.True(File.Exists(srt));
    }

    [Fact]
    public async Task All_versions_and_parts_are_planned_together()
    {
        var movie = _rig.AddMovie("Alpha");
        var dir = Path.GetDirectoryName(movie.Path)!;
        var altId = Guid.NewGuid();
        var alt = TestRig.Write(Path.Combine(dir, "Alpha - 4K.mkv"));
        var part = TestRig.Write(Path.Combine(dir, "Alpha-cd2.mkv"));
        _rig.Host.Items[movie.Id] = new HostItem
        {
            Id = movie.Id,
            Kind = MediaKind.Movie,
            Name = movie.Name,
            LibraryId = movie.LibraryId,
            DateAddedUtc = movie.DateAddedUtc,
            IsFileBacked = true,
            Path = movie.Path,
            Files = [new HostItemFile(movie.Path!, FileRole.Main, movie.Id), new HostItemFile(alt, FileRole.AlternateVersion, altId), new HostItemFile(part, FileRole.Part, movie.Id)],
            VersionIds = [movie.Id, altId],
        };
        await _rig.ScanAsync();
        var plan = _rig.PlanFor(movie.Id);
        Assert.Equal(3, plan.Items.Single().Files.Count(f => f.Role != FileRole.Sidecar));
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.False(File.Exists(alt));
        Assert.False(File.Exists(part));
    }

    [Fact]
    public async Task Symlinked_media_is_excluded_at_planning()
    {
        var movie = _rig.AddMovie("Alpha");
        var outside = TestRig.Write(Path.Combine(_rig.Sandbox, "elsewhere", "real.mkv"));
        File.Delete(movie.Path!);
        File.CreateSymbolicLink(movie.Path!, outside);
        await _rig.ScanAsync();
        var plan = _rig.PlanFor(movie.Id);
        Assert.Empty(plan.Items);
        Assert.Equal("PATH_TargetIsLink", plan.Exclusions.Single().Code);
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task A_directory_swapped_to_a_symlink_after_planning_is_refused()
    {
        var (movie, plan) = await OneMoviePlanAsync();
        var dir = Path.GetDirectoryName(movie.Path)!;
        var moved = Path.Combine(_rig.Sandbox, "moved");
        Directory.Move(dir, moved);
        Directory.CreateSymbolicLink(dir, moved);
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal(JobStatus.Rejected, job.Status);
        Assert.Equal("PATH_SymlinkInPath", job.Problems!.Single().Code);
        Assert.True(File.Exists(Path.Combine(moved, "Alpha.mkv")));
        Assert.Empty(_rig.Deleter.Deleted);
    }

    [Fact]
    public async Task Outside_approved_root_is_excluded_even_though_the_server_knows_it()
    {
        _rig.Settings.Save(new JellytrimSettings { DeletionEnabled = true, ApprovedRoots = [_rig.TvRoot] }, 1, _rig.Clock.Now);
        var (_, plan) = await OneMoviePlanAsync();
        Assert.Empty(plan.Items);
        Assert.Equal("PATH_OutsideApprovedRoots", plan.Exclusions.Single().Code);
    }

    [Fact]
    public async Task An_approved_root_the_server_no_longer_lists_grants_nothing()
    {
        var outside = Path.Combine(_rig.Sandbox, "not-a-library");
        _rig.Settings.Save(new JellytrimSettings { DeletionEnabled = true, ApprovedRoots = [outside] }, 1, _rig.Clock.Now);
        var (_, plan) = await OneMoviePlanAsync();
        Assert.Equal("PATH_NoApprovedRoot", plan.Exclusions.Single().Code);
    }

    [Fact]
    public async Task Hard_linked_file_is_deleted_but_reports_no_reclaim()
    {
        var movie = _rig.AddMovie("Alpha", 8192);
        var seed = Path.Combine(_rig.Sandbox, "seeding.mkv");
        System.Diagnostics.Process.Start("ln", [movie.Path!, seed]).WaitForExit();
        await _rig.ScanAsync();
        var plan = _rig.PlanFor(movie.Id);
        Assert.Equal(0, plan.Totals.EstimatedReclaimableBytes);
        Assert.Equal(1, plan.Totals.HardLinkedRetained);
        Assert.Contains(plan.Warnings, w => w.Contains("hard links"));
        await _rig.ExecuteAsync(plan);
        Assert.True(File.Exists(seed));
        Assert.Equal(8192, new FileInfo(seed).Length);
    }

    [Fact]
    public async Task Both_hard_links_in_one_plan_reclaim_once()
    {
        var a = _rig.AddMovie("Alpha", 8192);
        var bPath = Path.Combine(_rig.MoviesRoot, "Beta", "Beta.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(bPath)!);
        System.Diagnostics.Process.Start("ln", [a.Path!, bPath]).WaitForExit();
        var id = Guid.NewGuid();
        _rig.Host.Items[id] = new HostItem { Id = id, Kind = MediaKind.Movie, Name = "Beta", LibraryId = TestRig.MoviesLib, DateAddedUtc = _rig.Clock.Now.AddDays(-300), IsFileBacked = true, Path = bPath, Files = [new HostItemFile(bPath, FileRole.Main, id)], VersionIds = [id] };
        await _rig.ScanAsync();
        var plan = _rig.PlanFor(a.Id, id);
        var allocated = new Jellytrim.Core.FileSystem.NativeFileProbe().Probe(a.Path!).AllocatedBytes;
        Assert.Equal(allocated, plan.Totals.EstimatedReclaimableBytes);
        Assert.Equal(8192, plan.Totals.UniqueBytes);
        Assert.Equal(16384, plan.Totals.LogicalBytes);
    }

    [Fact]
    public async Task A_path_shared_with_an_unplanned_item_is_refused()
    {
        var a = _rig.AddMovie("Alpha");
        var id = Guid.NewGuid();
        _rig.Host.Items[id] = new HostItem { Id = id, Kind = MediaKind.Movie, Name = "Dup", LibraryId = TestRig.MoviesLib, DateAddedUtc = a.DateAddedUtc, IsFileBacked = true, Path = a.Path, Files = [new HostItemFile(a.Path!, FileRole.Main, id)], VersionIds = [id] };
        await _rig.ScanAsync();
        var plan = _rig.PlanFor(a.Id);
        Assert.Equal("SHARED_FILE", plan.Exclusions.Single().Code);
        Assert.Empty(plan.Items);
    }

    [Fact]
    public async Task Two_records_for_one_path_in_one_plan_delete_once_and_reconcile_both()
    {
        var a = _rig.AddMovie("Alpha");
        var id = Guid.NewGuid();
        _rig.Host.Items[id] = new HostItem { Id = id, Kind = MediaKind.Movie, Name = "Dup", LibraryId = TestRig.MoviesLib, DateAddedUtc = a.DateAddedUtc, IsFileBacked = true, Path = a.Path, Files = [new HostItemFile(a.Path!, FileRole.Main, id)], VersionIds = [id] };
        await _rig.ScanAsync();
        var plan = _rig.PlanFor(a.Id, id);
        Assert.Single(plan.Items);
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Single(_rig.Deleter.Deleted, a.Path!);
        Assert.Contains(a.Id, _rig.Host.RemovedRecords);
        Assert.Contains(id, _rig.Host.RemovedRecords);
    }

    [Fact]
    public async Task Show_selection_never_deletes_unwatched_or_favorited_episodes()
    {
        var (series, _, eps) = _rig.AddShow("Show", 1, 3);
        var policy = _rig.SavePolicy();
        _rig.MarkPlayedByAll(eps[0].Id);
        _rig.MarkPlayedByAll(eps[1].Id);
        _rig.Host.States[(eps[1].Id, TestRig.Bob)] = new UserItemState(WatchState.Played, 0, 1, _rig.Clock.Now.AddDays(-50), true);
        await _rig.ScanAsync();
        var plan = _rig.Planner.CreatePlan(new PlanRequest { Type = SelectionType.Items, ItemIds = [series.Id], PolicyId = policy.Id }, "a", "c").Plan;
        Assert.Equal([eps[0].Id], plan.Items.Select(i => i.ItemId));
        Assert.Equal(2, plan.Exclusions.Count);
        Assert.Empty(plan.Containers); // The show is not fully in the plan, so its records and artwork stay.
        await _rig.ExecuteAsync(plan);
        Assert.True(File.Exists(eps[1].Path));
        Assert.True(File.Exists(eps[2].Path));
        Assert.DoesNotContain(series.Id, _rig.Host.RemovedRecords);
    }

    [Fact]
    public async Task Complete_show_removes_artwork_folders_and_empty_records()
    {
        var (series, season, eps) = _rig.AddShow("Show", 1, 2);
        var art = TestRig.Write(Path.Combine(series.Path!, "tvshow.nfo"));
        var other = TestRig.Write(Path.Combine(series.Path!, "readme.txt"));
        var policy = _rig.SavePolicy(p => p.Unit = CleanupUnit.CompleteShow);
        eps.ForEach(e => _rig.MarkPlayedByAll(e.Id));
        await _rig.ScanAsync();
        var plan = _rig.Planner.CreatePlan(new PlanRequest { Type = SelectionType.Policy, PolicyId = policy.Id }, "a", "c").Plan;
        Assert.True(plan.HighImpact);
        Assert.Equal("DELETE 2 ITEMS", plan.ConfirmationPhrase);
        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(2, plan.Containers.Count);
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.False(File.Exists(art));
        Assert.False(Directory.Exists(season.Path));
        Assert.True(File.Exists(other)); // Not attributable, so it stays, along with its directory.
        Assert.True(Directory.Exists(series.Path));
        Assert.Contains(series.Id, _rig.Host.RemovedRecords);
        Assert.Contains(season.Id, _rig.Host.RemovedRecords);
    }

    [Fact]
    public async Task A_season_without_its_own_folder_never_claims_the_shows_artwork()
    {
        var seriesId = Guid.NewGuid();
        var seasonOne = Guid.NewGuid();
        var seasonTwo = Guid.NewGuid();
        var showDir = Path.Combine(_rig.TvRoot, "Flat");
        var art = TestRig.Write(Path.Combine(showDir, "tvshow.nfo"));
        _rig.Host.Items[seriesId] = new HostItem { Id = seriesId, Kind = MediaKind.Series, Name = "Flat", LibraryId = TestRig.TvLib, Path = showDir, VersionIds = [seriesId] };
        _rig.Host.Items[seasonOne] = new HostItem { Id = seasonOne, Kind = MediaKind.Season, Name = "Season 1", LibraryId = TestRig.TvLib, SeriesId = seriesId, SeasonNumber = 1, Path = showDir, VersionIds = [seasonOne] };
        _rig.Host.Items[seasonTwo] = new HostItem { Id = seasonTwo, Kind = MediaKind.Season, Name = "Season 2", LibraryId = TestRig.TvLib, SeriesId = seriesId, SeasonNumber = 2, Path = showDir, VersionIds = [seasonTwo] };
        HostItem Episode(Guid season, int s)
        {
            var id = Guid.NewGuid();
            var path = TestRig.Write(Path.Combine(showDir, $"Flat S{s:00}E01.mkv"));
            var item = new HostItem { Id = id, Kind = MediaKind.Episode, Name = $"S{s}E1", LibraryId = TestRig.TvLib, SeriesId = seriesId, SeasonId = season, SeriesName = "Flat", SeasonNumber = s, EpisodeNumber = 1, DateAddedUtc = _rig.Clock.Now.AddDays(-99), IsFileBacked = true, Path = path, Files = [new HostItemFile(path, FileRole.Main, id)], VersionIds = [id] };
            _rig.Host.Items[id] = item;
            return item;
        }

        var first = Episode(seasonOne, 1);
        var second = Episode(seasonTwo, 2);
        await _rig.ScanAsync();
        var plan = _rig.PlanFor(first.Id);
        Assert.Empty(plan.Containers.SelectMany(c => c.Files));
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.True(File.Exists(art));
        Assert.True(File.Exists(second.Path));
        Assert.Contains(seasonOne, _rig.Host.RemovedRecords);
        Assert.DoesNotContain(seriesId, _rig.Host.RemovedRecords);
    }

    [Fact]
    public async Task Explaining_one_item_matches_a_full_evaluation()
    {
        var (_, _, eps) = _rig.AddShow("Show", 1, 3);
        eps.ForEach(e => _rig.MarkPlayedByAll(e.Id));
        var policy = _rig.SavePolicy(p => p.KeepNewestEpisodesPerShow = 1);
        await _rig.ScanAsync();
        var full = PolicyEvaluator.Evaluate(policy, _rig.Evaluations.LoadInput());
        foreach (var episode in eps)
        {
            Assert.Equal(full.Items[episode.Id].Rules, _rig.Evaluations.Explain(policy, episode.Id, ActivePlayback.None)!.Rules);
        }
    }

    private HostItem DuplicateRecordOf(HostItem a)
    {
        var id = Guid.NewGuid();
        var dup = new HostItem { Id = id, Kind = MediaKind.Movie, Name = "Dup", LibraryId = TestRig.MoviesLib, DateAddedUtc = a.DateAddedUtc, IsFileBacked = true, Path = a.Path, Files = [new HostItemFile(a.Path!, FileRole.Main, id)], VersionIds = [id] };
        _rig.Host.Items[id] = dup;
        return dup;
    }

    private async Task<(HostItem Owner, HostItem Other, CleanupPlan Plan)> SharedPlanAsync()
    {
        var a = _rig.AddMovie("Alpha");
        var b = DuplicateRecordOf(a);
        await _rig.ScanAsync();
        var plan = _rig.PlanFor(a.Id, b.Id);
        var owner = plan.Items.Single().ItemId == a.Id ? a : b;
        return (owner, owner == a ? b : a, plan);
    }

    [Fact]
    public async Task A_protection_on_a_shared_record_stops_deletion_of_the_shared_file()
    {
        var (owner, other, plan) = await SharedPlanAsync();
        _rig.Policies.AddProtection(new Protection(other.Id, ProtectionTarget.Item, "Dup", null, _rig.Clock.Now, "a"));
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal(JobStatus.Rejected, job.Status);
        Assert.Equal(RuleIds.Explicit, job.Problems!.Single().Code);
        Assert.True(File.Exists(owner.Path));
    }

    [Fact]
    public async Task A_new_favorite_or_resume_on_a_shared_record_stops_deletion()
    {
        var (owner, other, plan) = await SharedPlanAsync();
        _rig.Host.States[(other.Id, TestRig.Bob)] = new UserItemState(WatchState.Partial, 500);
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal(RuleIds.InProgress, job.Problems!.Single().Code);
        Assert.True(File.Exists(owner.Path));
    }

    [Fact]
    public async Task A_shared_record_whose_files_changed_stops_deletion()
    {
        var (owner, other, plan) = await SharedPlanAsync();
        _rig.Host.Items.Remove(other.Id);
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal("ITEM_CHANGED", job.Problems!.Single().Code);
        Assert.True(File.Exists(owner.Path));
    }

    [Fact]
    public async Task An_episode_added_after_planning_keeps_the_shows_artwork()
    {
        var (series, season, eps) = _rig.AddShow("Show", 1, 1);
        var art = TestRig.Write(Path.Combine(series.Path!, "tvshow.nfo"));
        var seasonArt = TestRig.Write(Path.Combine(season.Path!, "poster.jpg"));
        await _rig.ScanAsync();
        var plan = _rig.PlanFor(eps[0].Id);
        Assert.Contains(plan.Containers.SelectMany(c => c.Files), f => f.Path == art);

        var late = Guid.NewGuid();
        var latePath = TestRig.Write(Path.Combine(season.Path!, "Show S01E02.mkv"));
        _rig.Host.Items[late] = new HostItem { Id = late, Kind = MediaKind.Episode, Name = "Late", LibraryId = TestRig.TvLib, SeriesId = series.Id, SeasonId = season.Id, IsFileBacked = true, Path = latePath, Files = [new HostItemFile(latePath, FileRole.Main, late)], VersionIds = [late] };

        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.True(File.Exists(art));
        Assert.True(File.Exists(seasonArt));
        Assert.True(File.Exists(latePath));
        Assert.Contains(_rig.Cleanup.ListJobItems(job.Id, 0, 10, null).Rows, r => r.Code == "CONTAINER_NOT_EMPTY");
    }

    [Fact]
    public async Task A_virtual_episode_keeps_artwork_out_of_the_plan()
    {
        var (series, season, eps) = _rig.AddShow("Show", 1, 1);
        var art = TestRig.Write(Path.Combine(series.Path!, "tvshow.nfo"));
        var missing = Guid.NewGuid();
        _rig.Host.Items[missing] = new HostItem { Id = missing, Kind = MediaKind.Episode, Name = "Unaired", LibraryId = TestRig.TvLib, SeriesId = series.Id, SeasonId = season.Id, IsFileBacked = false, VersionIds = [missing] };
        await _rig.ScanAsync();
        var plan = _rig.PlanFor(eps[0].Id);
        Assert.DoesNotContain(plan.Containers.SelectMany(c => c.Files), f => f.Path == art);
        await _rig.ExecuteAsync(plan);
        Assert.True(File.Exists(art));
    }

    [Fact]
    public async Task Container_artwork_has_durable_per_file_records()
    {
        var (series, _, eps) = _rig.AddShow("Show", 1, 1);
        var art = TestRig.Write(Path.Combine(series.Path!, "tvshow.nfo"));
        await _rig.ScanAsync();
        var job = await _rig.ExecuteAsync(_rig.PlanFor(eps[0].Id));
        var files = _rig.Cleanup.ListJobItems(job.Id, 0, 10, null).Rows.SelectMany(r => r.Files).ToList();
        Assert.Equal(FileStatus.Deleted, files.Single(f => f.Path == art).Status);
        Assert.Contains("1 item(s) permanently deleted", job.Message);
    }

    [Fact]
    public async Task Changed_container_artwork_is_refused_and_reported()
    {
        var (series, _, eps) = _rig.AddShow("Show", 1, 1);
        var art = TestRig.Write(Path.Combine(series.Path!, "tvshow.nfo"));
        await _rig.ScanAsync();
        var plan = _rig.PlanFor(eps[0].Id);
        await File.AppendAllTextAsync(art, "edited", TestContext.Current.CancellationToken);
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal(JobStatus.CompletedWithIssues, job.Status);
        Assert.True(File.Exists(art));
        Assert.DoesNotContain(series.Id, _rig.Host.RemovedRecords);
    }

    [Fact]
    public async Task Removing_a_root_during_a_job_stops_deletion_under_it()
    {
        var a = _rig.AddMovie("Alpha");
        var b = _rig.AddMovie("Beta");
        await _rig.ScanAsync();
        var plan = _rig.PlanFor(a.Id, b.Id);
        var done = false;
        _rig.Deleter.Before = _ =>
        {
            if (!done)
            {
                done = true;
                var config = _rig.Settings.Get();
                config.Value.ApprovedRoots = [_rig.TvRoot];
                _rig.Settings.Save(config.Value, config.Revision, _rig.Clock.Now);
            }
        };
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal(JobStatus.CompletedWithIssues, job.Status);
        Assert.Single(_rig.Deleter.Deleted);
        var rows = _rig.Cleanup.ListJobItems(job.Id, 0, 10, null).Rows;
        Assert.Contains(rows, r => r.Status == ItemStatus.Skipped && r.Code == "PATH_OutsideApprovedRoots");
    }

    [Fact]
    public async Task Editing_the_policy_during_a_job_stops_the_rest()
    {
        var a = _rig.AddMovie("Alpha");
        var b = _rig.AddMovie("Beta");
        _rig.MarkPlayedByAll(a.Id);
        _rig.MarkPlayedByAll(b.Id);
        var policy = _rig.SavePolicy();
        await _rig.ScanAsync();
        var plan = _rig.Planner.CreatePlan(new PlanRequest { Type = SelectionType.Policy, PolicyId = policy.Id }, "a", "c").Plan;
        var done = false;
        _rig.Deleter.Before = _ =>
        {
            if (!done)
            {
                done = true;
                _rig.Policies.Update(_rig.Policies.Get(policy.Id)!, 1, _rig.Clock.Now);
            }
        };
        var job = await _rig.ExecuteAsync(plan);
        Assert.Single(_rig.Deleter.Deleted);
        Assert.Contains(_rig.Cleanup.ListJobItems(job.Id, 0, 10, null).Rows, r => r.Code == "POLICY_CHANGED");
    }

    [Fact]
    public async Task Disposing_the_coordinator_twice_is_safe()
    {
        var coordinator = _rig.NewCoordinator();
        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Nested_library_root_is_never_removed_and_its_artwork_is_not_claimed()
    {
        var outer = Path.Combine(_rig.Sandbox, "media");
        _rig.Host.Libraries.Add(new HostLibrary(Guid.NewGuid(), "Outer", "movies", [outer]));
        _rig.Settings.Save(new JellytrimSettings { DeletionEnabled = true, ApprovedRoots = [outer] }, 1, _rig.Clock.Now);
        var movie = _rig.AddMovie("Alpha", ownFolder: false);
        var poster = TestRig.Write(Path.Combine(_rig.MoviesRoot, "poster.jpg"));
        await _rig.ScanAsync();
        var plan = _rig.PlanFor(movie.Id);
        Assert.Single(plan.Items);
        Assert.DoesNotContain(plan.Items.Single().Files, f => f.Path == poster);
        Assert.DoesNotContain(_rig.MoviesRoot, plan.Items.Single().DirectoriesIfEmpty);
        await _rig.ExecuteAsync(plan);
        Assert.True(File.Exists(poster));
        Assert.True(Directory.Exists(_rig.MoviesRoot));
    }

    [Fact]
    public async Task Virtual_children_keep_their_container_record()
    {
        var (series, season, eps) = _rig.AddShow("Show", 1, 1);
        var missing = Guid.NewGuid();
        _rig.Host.Items[missing] = new HostItem { Id = missing, Kind = MediaKind.Episode, Name = "Unaired", LibraryId = TestRig.TvLib, SeriesId = series.Id, SeasonId = season.Id, IsFileBacked = false, VersionIds = [missing] };
        await _rig.ScanAsync();
        var job = await _rig.ExecuteAsync(_rig.PlanFor(eps[0].Id));
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.DoesNotContain(series.Id, _rig.Host.RemovedRecords);
        Assert.Contains(eps[0].Id, _rig.Host.RemovedRecords);
    }

    [Fact]
    public async Task Virtual_and_remote_items_are_refused()
    {
        var id = Guid.NewGuid();
        _rig.Host.Items[id] = new HostItem { Id = id, Kind = MediaKind.Movie, Name = "Stream", LibraryId = TestRig.MoviesLib, DateAddedUtc = _rig.Clock.Now.AddDays(-100), IsFileBacked = false, VersionIds = [id] };
        await _rig.ScanAsync();
        var plan = _rig.PlanFor(id);
        Assert.Empty(plan.Items);
        Assert.Equal(RuleIds.HealthAvailable, plan.Exclusions.Single().Code);
    }

    // Stale plans

    [Fact]
    public async Task Expired_plan_is_rejected()
    {
        var (_, plan) = await OneMoviePlanAsync();
        _rig.Clock.Now = _rig.Clock.Now.AddMinutes(31);
        var hash = _rig.Cleanup.GetPlanSummary(plan.Id)!.Hash;
        var e = Assert.Throws<JellytrimException>(() => _rig.Coordinator.Start(plan.Id, new ExecuteRequest(hash, "k", true, null), "a", "c"));
        Assert.Equal(ErrorCodes.PlanExpired, e.Code);
        Assert.Equal(410, e.Status);
    }

    [Fact]
    public async Task Wrong_hash_is_rejected()
    {
        var (_, plan) = await OneMoviePlanAsync();
        var e = Assert.Throws<JellytrimException>(() => _rig.Coordinator.Start(plan.Id, new ExecuteRequest("00", "k", true, null), "a", "c"));
        Assert.Equal(ErrorCodes.PlanHashMismatch, e.Code);
    }

    [Fact]
    public async Task A_tampered_stored_document_is_rejected()
    {
        var (_, plan) = await OneMoviePlanAsync();
        var hash = _rig.Cleanup.GetPlanSummary(plan.Id)!.Hash;
        _rig.Db.Write((c, tx) => c.Cmd("UPDATE plans SET document=replace(document,'Alpha','Gamma')", tx).Exec());
        var e = Assert.Throws<JellytrimException>(() => _rig.Coordinator.Start(plan.Id, new ExecuteRequest(hash, "k", true, null), "a", "c"));
        Assert.Equal(ErrorCodes.PlanHashMismatch, e.Code);
    }

    [Fact]
    public async Task Acknowledgement_is_required()
    {
        var (_, plan) = await OneMoviePlanAsync();
        var hash = _rig.Cleanup.GetPlanSummary(plan.Id)!.Hash;
        var e = Assert.Throws<JellytrimException>(() => _rig.Coordinator.Start(plan.Id, new ExecuteRequest(hash, "k", false, null), "a", "c"));
        Assert.Equal(ErrorCodes.ConfirmationRequired, e.Code);
    }

    [Fact]
    public async Task High_impact_needs_the_exact_phrase()
    {
        var config = _rig.Settings.Get();
        config.Value.HighImpactItems = 1;
        _rig.Settings.Save(config.Value, config.Revision, _rig.Clock.Now);
        var (_, plan) = await OneMoviePlanAsync();
        Assert.Equal("DELETE 1 ITEMS", plan.ConfirmationPhrase);
        var hash = _rig.Cleanup.GetPlanSummary(plan.Id)!.Hash;
        var e = Assert.Throws<JellytrimException>(() => _rig.Coordinator.Start(plan.Id, new ExecuteRequest(hash, "k", true, "delete 1 items"), "a", "c"));
        Assert.Equal(ErrorCodes.ConfirmationRequired, e.Code);
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal(JobStatus.Completed, job.Status);
    }

    [Fact]
    public async Task File_modified_after_planning_rejects_the_whole_plan_with_nothing_deleted()
    {
        var other = _rig.AddMovie("Beta");
        var movie = _rig.AddMovie("Alpha");
        await _rig.ScanAsync();
        var plan = _rig.PlanFor(movie.Id, other.Id);
        await File.AppendAllTextAsync(movie.Path!, "changed", TestContext.Current.CancellationToken);
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal(JobStatus.Rejected, job.Status);
        Assert.Equal("FILE_SIZE_CHANGED", job.Problems!.Single().Code);
        Assert.True(File.Exists(other.Path));
        Assert.Empty(_rig.Deleter.Deleted);
        Assert.Equal(CleanupStore.PlanRejected, _rig.Cleanup.GetPlanSummary(plan.Id)!.Status);
    }

    [Fact]
    public async Task File_replaced_with_same_size_is_detected()
    {
        var (movie, plan) = await OneMoviePlanAsync();
        var replacement = TestRig.Write(Path.Combine(_rig.Sandbox, "swap.mkv"), 4096);
        File.Move(replacement, movie.Path!, true);
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal(JobStatus.Rejected, job.Status);
        Assert.Equal("FILE_REPLACED", job.Problems!.Single().Code);
        Assert.True(File.Exists(movie.Path));
    }

    [Fact]
    public async Task Modification_time_alone_counts_as_a_change()
    {
        var (movie, plan) = await OneMoviePlanAsync();
        File.SetLastWriteTimeUtc(movie.Path!, DateTime.UtcNow.AddDays(-3));
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal("FILE_MODIFIED", job.Problems!.Single().Code);
    }

    [Fact]
    public async Task New_protection_after_planning_blocks_execution()
    {
        var (movie, plan) = await OneMoviePlanAsync();
        _rig.Policies.AddProtection(new Protection(movie.Id, ProtectionTarget.Item, "Alpha", null, _rig.Clock.Now, "a"));
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal(RuleIds.Explicit, job.Problems!.Single().Code);
        Assert.True(File.Exists(movie.Path));
    }

    [Fact]
    public async Task New_favorite_or_resume_after_planning_blocks_execution_using_live_state()
    {
        var (movie, plan) = await OneMoviePlanAsync();
        _rig.Host.States[(movie.Id, TestRig.Bob)] = new UserItemState(WatchState.Unplayed, IsFavorite: true);
        Assert.Equal(RuleIds.Favorite, (await _rig.ExecuteAsync(plan)).Problems!.Single().Code);

        _rig.Host.States.Remove((movie.Id, TestRig.Bob));
        var second = _rig.AddMovie("Beta");
        await _rig.ScanAsync();
        var plan2 = _rig.PlanFor(second.Id);
        _rig.Host.States[(second.Id, TestRig.Ann)] = new UserItemState(WatchState.Partial, 999);
        Assert.Equal(RuleIds.InProgress, (await _rig.ExecuteAsync(plan2)).Problems!.Single().Code);
    }

    [Fact]
    public async Task Playback_starting_after_planning_blocks_execution()
    {
        var (movie, plan) = await OneMoviePlanAsync();
        _rig.Host.Playback = new ActivePlayback(new HashSet<Guid> { movie.Id }, new HashSet<string>());
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal(RuleIds.ActivePlayback, job.Problems!.Single().Code);
    }

    [Fact]
    public async Task Policy_change_after_planning_invalidates_the_plan()
    {
        var movie = _rig.AddMovie("Alpha");
        _rig.MarkPlayedByAll(movie.Id);
        var policy = _rig.SavePolicy();
        await _rig.ScanAsync();
        var plan = _rig.Planner.CreatePlan(new PlanRequest { Type = SelectionType.Policy, PolicyId = policy.Id }, "a", "c").Plan;
        Assert.Single(plan.Items);
        _rig.Policies.Update(policy, 1, _rig.Clock.Now);
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal("POLICY_CHANGED", job.Problems!.Single().Code);
    }

    [Fact]
    public async Task User_unplaying_after_planning_invalidates_a_policy_plan()
    {
        var movie = _rig.AddMovie("Alpha");
        _rig.MarkPlayedByAll(movie.Id);
        var policy = _rig.SavePolicy();
        await _rig.ScanAsync();
        var plan = _rig.Planner.CreatePlan(new PlanRequest { Type = SelectionType.Policy, PolicyId = policy.Id }, "a", "c").Plan;
        _rig.Host.States.Remove((movie.Id, TestRig.Ann));
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal(RuleIds.WatchedAll, job.Problems!.Single().Code);
    }

    [Fact]
    public async Task Server_item_changing_invalidates_the_plan()
    {
        var (movie, plan) = await OneMoviePlanAsync();
        _rig.Host.Items.Remove(movie.Id);
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal("ITEM_GONE", job.Problems!.Single().Code);
        Assert.True(File.Exists(movie.Path));
    }

    [Fact]
    public async Task Disabling_deletion_after_planning_blocks_execution()
    {
        var (_, plan) = await OneMoviePlanAsync();
        var hash = _rig.Cleanup.GetPlanSummary(plan.Id)!.Hash;
        var config = _rig.Settings.Get();
        config.Value.DeletionEnabled = false;
        _rig.Settings.Save(config.Value, config.Revision, _rig.Clock.Now);
        var e = Assert.Throws<JellytrimException>(() => _rig.Coordinator.Start(plan.Id, new ExecuteRequest(hash, "k", true, null), "a", "c"));
        Assert.Equal(ErrorCodes.DeletionDisabled, e.Code);
    }

    [Fact]
    public async Task Offline_root_rejects_with_nothing_deleted()
    {
        var (movie, plan) = await OneMoviePlanAsync();
        var hidden = _rig.MoviesRoot + ".offline";
        Directory.Move(_rig.MoviesRoot, hidden);
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal(JobStatus.Rejected, job.Status);
        Assert.Equal("PATH_RootUnavailable", job.Problems!.Single().Code);
        Directory.Move(hidden, _rig.MoviesRoot);
        Assert.True(File.Exists(movie.Path));
    }

    // Plans in read-only mode

    [Fact]
    public async Task Read_only_is_the_default_and_preview_plans_can_never_execute()
    {
        using var rig = new TestRig(enableDeletion: false);
        Assert.False(rig.Settings.Get().Value.DeletionEnabled);
        var movie = rig.AddMovie("Alpha");
        await rig.ScanAsync();
        var plan = rig.PlanFor(movie.Id);
        Assert.True(plan.PreviewOnly);
        Assert.Single(plan.Items);
        var hash = rig.Cleanup.GetPlanSummary(plan.Id)!.Hash;
        Assert.Equal(ErrorCodes.DeletionDisabled, Assert.Throws<JellytrimException>(() => rig.Coordinator.Start(plan.Id, new ExecuteRequest(hash, "k", true, null), "a", "c")).Code);

        // Enabling afterwards does not make that old preview executable.
        rig.Settings.Save(new JellytrimSettings { DeletionEnabled = true, ApprovedRoots = [rig.MoviesRoot] }, 0, rig.Clock.Now);
        Assert.Equal(ErrorCodes.PlanPreviewOnly, Assert.Throws<JellytrimException>(() => rig.Coordinator.Start(plan.Id, new ExecuteRequest(hash, "k", true, null), "a", "c")).Code);
        Assert.True(File.Exists(movie.Path));
    }

    // Selection stability

    [Fact]
    public async Task Select_all_matching_refuses_if_the_result_grew_or_shrank()
    {
        _rig.AddMovie("Alpha");
        _rig.AddMovie("Beta");
        await _rig.ScanAsync();
        var query = new ExplorerQuery { Kinds = [MediaKind.Movie] };
        var shown = _rig.Explorer.Query(query).Total;
        Assert.Equal(2, shown);
        _rig.AddMovie("Gamma");
        await _rig.ScanAsync();
        var e = Assert.Throws<JellytrimException>(() => _rig.Planner.CreatePlan(new PlanRequest { Type = SelectionType.Query, Query = query, ExpectedCount = (int)shown }, "a", "c"));
        Assert.Equal(ErrorCodes.SelectionChanged, e.Code);
    }

    [Fact]
    public async Task Too_large_a_plan_is_refused_rather_than_truncated()
    {
        var config = _rig.Settings.Get();
        config.Value.MaxPlanItems = 1;
        _rig.Settings.Save(config.Value, config.Revision, _rig.Clock.Now);
        var a = _rig.AddMovie("Alpha");
        var b = _rig.AddMovie("Beta");
        _rig.MarkPlayedByAll(a.Id);
        _rig.MarkPlayedByAll(b.Id);
        var policy = _rig.SavePolicy();
        await _rig.ScanAsync();
        var e = Assert.Throws<JellytrimException>(() => _rig.Planner.CreatePlan(new PlanRequest { Type = SelectionType.Policy, PolicyId = policy.Id }, "a", "c"));
        Assert.Equal(ErrorCodes.PlanTooLarge, e.Code);
    }

    [Fact]
    public async Task Reclaim_target_selects_ranked_items_and_keeps_complete_units_whole()
    {
        var (_, _, eps) = _rig.AddShow("Show", 1, 3, bytes: 8192);
        eps.ForEach(e => _rig.MarkPlayedByAll(e.Id));
        var policy = _rig.SavePolicy(p => p.Unit = CleanupUnit.CompleteSeason);
        await _rig.ScanAsync();
        var plan = _rig.Planner.CreatePlan(new PlanRequest { Type = SelectionType.Policy, PolicyId = policy.Id, TargetBytes = 1 }, "a", "c").Plan;
        Assert.Equal(3, plan.Items.Count);
    }

    // Execution outcomes

    [Fact]
    public async Task Partial_failure_is_reported_per_item_and_the_rest_continue()
    {
        var a = _rig.AddMovie("Alpha");
        var b = _rig.AddMovie("Beta");
        await _rig.ScanAsync();
        _rig.Deleter.Fail = p => p == a.Path ? new UnauthorizedAccessException() : null;
        var job = await _rig.ExecuteAsync(_rig.PlanFor(a.Id, b.Id));
        Assert.Equal(JobStatus.CompletedWithIssues, job.Status);
        Assert.True(File.Exists(a.Path));
        Assert.False(File.Exists(b.Path));
        var (rows, _) = _rig.Cleanup.ListJobItems(job.Id, 0, 10, null);
        var failed = rows.Single(r => r.ItemId == a.Id);
        Assert.Equal(ItemStatus.Failed, failed.Status);
        Assert.Equal("PERMISSION_DENIED", failed.Code);
        Assert.Equal(FileStatus.Failed, failed.Files.Single().Status);
        Assert.Equal(ItemStatus.Deleted, rows.Single(r => r.ItemId == b.Id).Status);
        Assert.DoesNotContain(a.Id, _rig.Host.RemovedRecords); // Its file remains, so its record must remain.
    }

    [Fact]
    public async Task Failed_sidecar_stops_that_item_and_records_what_was_already_removed()
    {
        var a = _rig.AddMovie("Alpha");
        var srt = TestRig.Write(Path.Combine(Path.GetDirectoryName(a.Path)!, "Alpha.srt"));
        await _rig.ScanAsync();
        _rig.Deleter.Fail = p => p == srt ? new IOException("busy") : null;
        var job = await _rig.ExecuteAsync(_rig.PlanFor(a.Id));
        var item = _rig.Cleanup.ListJobItems(job.Id, 0, 10, null).Rows.Single();
        Assert.Equal(ItemStatus.Failed, item.Status);
        Assert.Contains(item.Files, f => f.Status == FileStatus.Deleted);
        Assert.Contains(item.Files, f => f.Status == FileStatus.Failed);
        Assert.Contains("cannot be restored", item.Message);
    }

    [Fact]
    public async Task Reconcile_failure_does_not_misreport_the_deletion()
    {
        var (movie, plan) = await OneMoviePlanAsync();
        _rig.Host.FailRemove = _ => true;
        var job = await _rig.ExecuteAsync(plan);
        var item = _rig.Cleanup.ListJobItems(job.Id, 0, 10, null).Rows.Single();
        Assert.Equal(ItemStatus.Deleted, item.Status);
        Assert.Equal("PendingNextLibraryScan", item.Reconcile);
        Assert.False(File.Exists(movie.Path));
    }

    [Fact]
    public async Task A_change_between_preflight_and_item_skips_only_that_item()
    {
        var a = _rig.AddMovie("Alpha");
        var b = _rig.AddMovie("Beta");
        await _rig.ScanAsync();
        var plan = _rig.PlanFor(a.Id, b.Id);
        var calls = 0;
        _rig.Host.OnReadLive = () =>
        {
            // Call 1 is preflight's batch; call 2 is the first item.
            if (++calls == 2)
            {
                File.AppendAllText(plan.Items[0].Files[0].Path, "x");
            }
        };
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal(JobStatus.CompletedWithIssues, job.Status);
        var rows = _rig.Cleanup.ListJobItems(job.Id, 0, 10, null).Rows;
        Assert.Equal(ItemStatus.Skipped, rows[0].Status);
        Assert.Equal("FILE_SIZE_CHANGED", rows[0].Code);
        Assert.True(File.Exists(plan.Items[0].Files[0].Path));
        Assert.Equal(ItemStatus.Deleted, rows[1].Status);
    }

    [Fact]
    public async Task A_file_changing_in_the_final_window_is_refused_at_the_file_level()
    {
        var (movie, plan) = await OneMoviePlanAsync();
        var calls = 0;
        _rig.Probe = new InterceptProbe(_rig.Probe, path =>
        {
            // Probes: planning done. 1 = preflight, 2 = per-item validate, 3 = immediately before unlink.
            if (path == movie.Path && ++calls == 3)
            {
                File.AppendAllText(path, "late write");
            }
        });
        var coordinator = _rig.NewCoordinator();
        coordinator.Start(plan.Id, new ExecuteRequest(_rig.Cleanup.GetPlanSummary(plan.Id)!.Hash, "k", true, null), "a", "c");
        await coordinator.WaitForCurrentAsync();
        var item = _rig.Cleanup.ListJobItems(_rig.Cleanup.FindJobByPlan(plan.Id)!.Id, 0, 10, null).Rows.Single();
        Assert.Equal(ItemStatus.Failed, item.Status);
        Assert.Equal(FileStatus.Refused, item.Files.Single().Status);
        Assert.True(File.Exists(movie.Path));
    }

    // Cancellation

    [Fact]
    public async Task Cancel_stops_new_work_and_never_claims_reversibility()
    {
        var movies = Enumerable.Range(0, 5).Select(i => _rig.AddMovie("M" + i)).ToList();
        await _rig.ScanAsync();
        var plan = _rig.PlanFor(movies.Select(m => m.Id).ToArray());
        var hash = _rig.Cleanup.GetPlanSummary(plan.Id)!.Hash;
        JobRow? started = null;
        _rig.Deleter.Before = _ =>
        {
            if (_rig.Deleter.Deleted.Count == 1 && started is not null)
            {
                _rig.Coordinator.RequestCancel(started.Id, "a", "c");
            }
        };
        var gate = new ManualResetEventSlim();
        var firstDelete = true;
        var inner = _rig.Deleter.Before;
        _rig.Deleter.Before = p =>
        {
            if (firstDelete)
            {
                firstDelete = false;
                gate.Wait(TimeSpan.FromSeconds(10));
            }

            inner(p);
        };
        started = _rig.Coordinator.Start(plan.Id, new ExecuteRequest(hash, "k", true, null), "a", "c");
        gate.Set();
        await _rig.Coordinator.WaitForCurrentAsync();
        var job = _rig.Cleanup.GetJob(started.Id)!;
        Assert.Equal(JobStatus.Cancelled, job.Status);
        var rows = _rig.Cleanup.ListJobItems(job.Id, 0, 10, null).Rows;
        Assert.Contains(rows, r => r.Status == ItemStatus.Deleted);
        Assert.Contains(rows, r => r.Status == ItemStatus.Cancelled);
        Assert.Contains("cannot be restored", job.Message);
        Assert.Equal(5 - rows.Count(r => r.Status == ItemStatus.Deleted), movies.Count(m => File.Exists(m.Path)));
    }

    [Fact]
    public async Task Cancelling_a_finished_job_is_refused()
    {
        var (_, plan) = await OneMoviePlanAsync();
        var job = await _rig.ExecuteAsync(plan);
        Assert.Equal(ErrorCodes.JobNotActive, Assert.Throws<JellytrimException>(() => _rig.Coordinator.RequestCancel(job.Id, "a", "c")).Code);
    }

    // Crash recovery

    [Fact]
    public async Task Crash_recovery_finalizes_without_resuming_and_reports_what_happened()
    {
        var a = _rig.AddMovie("Alpha");
        var b = _rig.AddMovie("Beta");
        var c = _rig.AddMovie("Gamma");
        await _rig.ScanAsync();
        var plan = _rig.PlanFor(a.Id, b.Id, c.Id);
        var job = new JobRow { Id = Guid.NewGuid(), PlanId = plan.Id, CorrelationId = "c", Status = JobStatus.Queued, CreatedAtUtc = _rig.Clock.Now, TotalItems = 3 };

        // Reconstruct the state a crash leaves: item 0 finished, item 1 mid-delete with its file already gone, item 2 untouched.
        _rig.Cleanup.InsertJob(job, "k", plan.Items.Select(i => (new PlanJobEntry(i.ItemId, i.Name), i.Files)).ToList());
        _rig.Cleanup.SetJobStatus(job.Id, JobStatus.Running, _rig.Clock.Now, started: true);
        File.Delete(plan.Items[0].Files[0].Path);
        _rig.Cleanup.SetFile(job.Id, 0, 0, FileStatus.Deleted, _rig.Clock.Now);
        _rig.Cleanup.SetItem(job.Id, 0, ItemStatus.Deleted, _rig.Clock.Now, finished: true);
        _rig.Cleanup.SetItem(job.Id, 1, ItemStatus.InProgress, _rig.Clock.Now, started: true);
        _rig.Cleanup.SetFile(job.Id, 1, 0, FileStatus.Deleting, _rig.Clock.Now);
        File.Delete(plan.Items[1].Files[0].Path);

        var coordinator = _rig.NewCoordinator();
        coordinator.RecoverInterruptedJobs();

        var after = _rig.Cleanup.GetJob(job.Id)!;
        Assert.Equal(JobStatus.Interrupted, after.Status);
        var rows = _rig.Cleanup.ListJobItems(job.Id, 0, 10, null).Rows;
        Assert.Equal(ItemStatus.Deleted, rows[0].Status);
        Assert.Equal(ItemStatus.Uncertain, rows[1].Status);
        Assert.Equal(FileStatus.Deleted, rows[1].Files[0].Status);
        Assert.Equal(ItemStatus.NotRun, rows[2].Status);
        Assert.True(File.Exists(plan.Items[2].Files[0].Path)); // Never resumed.
        Assert.Empty(_rig.Deleter.Deleted);

        // Reusing the plan is refused, and the single-active-job slot is free again.
        var hash = _rig.Cleanup.GetPlanSummary(plan.Id)!.Hash;
        Assert.Equal(ErrorCodes.PlanAlreadyExecuted, Assert.Throws<JellytrimException>(() => coordinator.Start(plan.Id, new ExecuteRequest(hash, "k2", true, null), "a", "c")).Code);
        Assert.Null(_rig.Cleanup.ActiveJob());
    }

    [Fact]
    public async Task Crash_recovery_does_not_claim_deletion_of_an_unreadable_path()
    {
        var a = _rig.AddMovie("Alpha");
        await _rig.ScanAsync();
        var plan = _rig.PlanFor(a.Id);
        var job = new JobRow { Id = Guid.NewGuid(), PlanId = plan.Id, CorrelationId = "c", Status = JobStatus.Queued, CreatedAtUtc = _rig.Clock.Now, TotalItems = 1 };
        _rig.Cleanup.InsertJob(job, "k", plan.Items.Select(i => (new PlanJobEntry(i.ItemId, i.Name), i.Files)).ToList());
        _rig.Cleanup.SetItem(job.Id, 0, ItemStatus.InProgress, _rig.Clock.Now, started: true);
        _rig.Cleanup.SetFile(job.Id, 0, 0, FileStatus.Deleting, _rig.Clock.Now);
        _rig.Probe = new FixedProbe(new Core.FileSystem.FileObservation(ProbeStatus.IoError));
        _rig.NewCoordinator().RecoverInterruptedJobs();
        var file = _rig.Cleanup.ListJobItems(job.Id, 0, 1, null).Rows[0].Files[0];
        Assert.Equal(FileStatus.Uncertain, file.Status);
    }

    // Concurrency and idempotency

    [Fact]
    public async Task Same_idempotency_key_returns_the_same_job()
    {
        var (_, plan) = await OneMoviePlanAsync();
        var hash = _rig.Cleanup.GetPlanSummary(plan.Id)!.Hash;
        var request = new ExecuteRequest(hash, "same", true, null);
        var first = _rig.Coordinator.Start(plan.Id, request, "a", "c");
        await _rig.Coordinator.WaitForCurrentAsync();
        var second = _rig.Coordinator.Start(plan.Id, request, "a", "c");
        Assert.Equal(first.Id, second.Id);
        Assert.Single(_rig.Deleter.Deleted);
    }

    [Fact]
    public async Task A_plan_runs_once()
    {
        var (_, plan) = await OneMoviePlanAsync();
        await _rig.ExecuteAsync(plan);
        var hash = _rig.Cleanup.GetPlanSummary(plan.Id)!.Hash;
        Assert.Equal(ErrorCodes.PlanAlreadyExecuted, Assert.Throws<JellytrimException>(() => _rig.Coordinator.Start(plan.Id, new ExecuteRequest(hash, "new", true, null), "a", "c")).Code);
    }

    [Fact]
    public async Task Concurrent_starts_of_two_plans_yield_one_job()
    {
        var a = _rig.AddMovie("Alpha");
        var b = _rig.AddMovie("Beta");
        await _rig.ScanAsync();
        var planA = _rig.PlanFor(a.Id);
        var planB = _rig.PlanFor(b.Id);
        var release = new ManualResetEventSlim();
        _rig.Deleter.Before = _ => release.Wait(TimeSpan.FromSeconds(10));
        var ha = _rig.Cleanup.GetPlanSummary(planA.Id)!.Hash;
        var hb = _rig.Cleanup.GetPlanSummary(planB.Id)!.Hash;
        var first = _rig.Coordinator.Start(planA.Id, new ExecuteRequest(ha, "a", true, null), "a", "c");
        Assert.Equal(ErrorCodes.JobActive, Assert.Throws<JellytrimException>(() => _rig.Coordinator.Start(planB.Id, new ExecuteRequest(hb, "b", true, null), "a", "c")).Code);
        release.Set();
        await _rig.Coordinator.WaitForCurrentAsync();
        Assert.Equal(JobStatus.Completed, _rig.Cleanup.GetJob(first.Id)!.Status);
        Assert.True(File.Exists(b.Path));
    }

    [Fact]
    public async Task Database_enforces_a_single_active_job_independently_of_the_gate()
    {
        var movie = _rig.AddMovie("Alpha");
        await _rig.ScanAsync();
        var p1 = _rig.PlanFor(movie.Id);
        var p2 = _rig.PlanFor(movie.Id);
        var make = (Guid plan) => new JobRow { Id = Guid.NewGuid(), PlanId = plan, CorrelationId = "c", Status = JobStatus.Running, CreatedAtUtc = _rig.Clock.Now, TotalItems = 0 };
        _rig.Cleanup.InsertJob(make(p1.Id), "k1", []);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _rig.Cleanup.InsertJob(make(p2.Id), "k2", []));
    }

    [Fact]
    public async Task Scans_and_jobs_exclude_each_other()
    {
        var a = _rig.AddMovie("Alpha");
        await _rig.ScanAsync();
        var plan = _rig.PlanFor(a.Id);
        var hash = _rig.Cleanup.GetPlanSummary(plan.Id)!.Hash;
        Assert.True(_rig.Gate.TryEnter(Jellytrim.Core.Inventory.InventoryScanner.GateName));
        Assert.Equal(ErrorCodes.ScanRunning, Assert.Throws<JellytrimException>(() => _rig.Coordinator.Start(plan.Id, new ExecuteRequest(hash, "k", true, null), "a", "c")).Code);
        _rig.Gate.Exit(Jellytrim.Core.Inventory.InventoryScanner.GateName);

        Assert.True(_rig.Gate.TryEnter(CleanupCoordinator.GateName));
        var scan = await Assert.ThrowsAsync<JellytrimException>(_rig.ScanAsync);
        Assert.Equal(ErrorCodes.CleanupRunning, scan.Code);
        _rig.Gate.Exit(CleanupCoordinator.GateName);
    }

    [Fact]
    public async Task Audit_records_plan_and_job_lifecycle_with_correlation()
    {
        var (_, plan) = await OneMoviePlanAsync();
        await _rig.ExecuteAsync(plan);
        var (rows, _) = _rig.Audit.List(0, 50);
        Assert.Contains(rows, r => r.Action == "plan.created");
        Assert.Contains(rows, r => r.Action == "job.started" && r.CorrelationId == "corr");
        Assert.Contains(rows, r => r.Action == "job.finished" && r.Outcome == JobStatus.Completed);
    }

    [Fact]
    public async Task Inventory_reflects_deletion_immediately_and_records_before_after()
    {
        var (movie, plan) = await OneMoviePlanAsync();
        var job = await _rig.ExecuteAsync(plan);
        Assert.Empty(_rig.Inventory.GetItemFiles(movie.Id));
        Assert.Equal(0, _rig.Inventory.ListLibraries().Single(l => l.Id == TestRig.MoviesLib).ItemCount);
        Assert.NotNull(job.FreeBefore);
        Assert.NotNull(job.FreeAfter);
    }

    private sealed class InterceptProbe(Core.FileSystem.IFileProbe inner, Action<string> before) : Core.FileSystem.IFileProbe
    {
        public Core.FileSystem.FileObservation Probe(string path)
        {
            before(path);
            return inner.Probe(path);
        }
    }

    private sealed class FixedProbe(Core.FileSystem.FileObservation fixedResult) : Core.FileSystem.IFileProbe
    {
        public Core.FileSystem.FileObservation Probe(string path) => fixedResult;
    }
}
