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

public sealed class FakeHost : IMediaHost
{
    public List<HostLibrary> Libraries { get; } = [];

    public List<HostUser> Users { get; } = [];

    public Dictionary<Guid, HostItem> Items { get; } = [];

    public Dictionary<(Guid, Guid), UserItemState> States { get; } = [];

    public ActivePlayback Playback { get; set; } = ActivePlayback.None;

    public List<Guid> RemovedRecords { get; } = [];

    public HashSet<Guid> FailUserRead { get; } = [];

    public Func<Guid, bool>? FailRemove { get; set; }

    public Action? OnReadLive { get; set; }

    public IReadOnlyList<HostLibrary> GetLibraries() => Libraries;

    public IReadOnlyList<HostUser> GetUsers() => Users;

    public IReadOnlyList<Guid> ListItemIds(Guid libraryId) => Items.Values.Where(i => i.LibraryId == libraryId).OrderBy(i => i.Id).Select(i => i.Id).ToList();

    public HostItemPage ReadItems(Guid libraryId, IReadOnlyList<Guid> itemIds, IReadOnlyCollection<Guid> userIds)
    {
        var page = itemIds.Select(i => Items.GetValueOrDefault(i)).OfType<HostItem>().ToList();
        var states = new Dictionary<(Guid, Guid), UserItemState>();
        foreach (var item in page)
        {
            foreach (var user in userIds.Where(u => !FailUserRead.Contains(u)))
            {
                if (States.TryGetValue((item.Id, user), out var s))
                {
                    states[(item.Id, user)] = s;
                }
            }
        }

        return new HostItemPage { Items = page, States = states, FailedUsers = userIds.Where(FailUserRead.Contains).ToList() };
    }

    public HostItem? GetItem(Guid itemId) => Items.GetValueOrDefault(itemId);

    public IReadOnlyDictionary<(Guid ItemId, Guid UserId), UserItemState> ReadLiveStates(IReadOnlyCollection<Guid> itemIds, IReadOnlyCollection<Guid> userIds)
    {
        OnReadLive?.Invoke();
        return States.Where(s => itemIds.Contains(s.Key.Item1) && userIds.Contains(s.Key.Item2)).ToDictionary(s => s.Key, s => s.Value);
    }

    public ActivePlayback GetActivePlayback() => Playback;

    public void RemoveItemRecord(Guid itemId)
    {
        if (FailRemove?.Invoke(itemId) == true)
        {
            throw new InvalidOperationException("record removal failed");
        }

        RemovedRecords.Add(itemId);
        Items.Remove(itemId);
    }

    public bool HasChildren(Guid containerId) => Items.Values.Any(i => i.SeasonId == containerId || i.SeriesId == containerId);
}

/// <summary>Refuses anything outside the dedicated test root, so no test can reach real files, and allows injected failures.</summary>
public sealed class SandboxDeleter(string sandbox) : IFileDeleter
{
    private readonly FileDeleter _real = new();

    public Func<string, Exception?>? Fail { get; set; }

    public Action<string>? Before { get; set; }

    public List<string> Deleted { get; } = [];

    public void DeleteFile(string path)
    {
        Assert.StartsWith(sandbox + Path.DirectorySeparatorChar, path);
        Before?.Invoke(path);
        if (Fail?.Invoke(path) is { } error)
        {
            throw error;
        }

        _real.DeleteFile(path);
        lock (Deleted)
        {
            Deleted.Add(path);
        }
    }

    public bool DeleteDirectoryIfEmpty(string path)
    {
        Assert.StartsWith(sandbox + Path.DirectorySeparatorChar, path);
        return _real.DeleteDirectoryIfEmpty(path);
    }
}

public sealed class StubVolumes : IVolumeProbe
{
    public long Free { get; set; } = 1_000_000;

    public (long Total, long Free)? Measure(string path) => (10_000_000, Free);
}

public sealed class TestClock(DateTime start) : TimeProvider
{
    public DateTime Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
}

/// <summary>A disposable sandbox with real SQLite and real files, wired exactly like the plugin.</summary>
public sealed class TestRig : IDisposable
{
    public static readonly Guid Ann = new("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid Bob = new("bbbbbbbb-0000-0000-0000-000000000002");
    public static readonly Guid MoviesLib = new("11111111-0000-0000-0000-000000000000");
    public static readonly Guid TvLib = new("22222222-0000-0000-0000-000000000000");

    public TestRig(bool enableDeletion = true)
    {
        // Canonicalized because macOS temp paths run through a symlink.
        Sandbox = Directory.ResolveLinkTarget(Path.GetTempPath().TrimEnd('/'), true)?.FullName is { } real
            ? Path.Combine(real, "jellytrim-test-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(Path.GetTempPath(), "jellytrim-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Sandbox);
        MoviesRoot = Path.Combine(Sandbox, "media", "movies");
        TvRoot = Path.Combine(Sandbox, "media", "tv");
        Directory.CreateDirectory(MoviesRoot);
        Directory.CreateDirectory(TvRoot);

        Host.Libraries.Add(new HostLibrary(MoviesLib, "Movies", "movies", [MoviesRoot]));
        Host.Libraries.Add(new HostLibrary(TvLib, "Shows", "tvshows", [TvRoot]));
        Host.Users.Add(new HostUser(Ann, "ann", true, false, true, []));
        Host.Users.Add(new HostUser(Bob, "bob", false, false, true, []));

        Clock = new TestClock(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        Db = new JellytrimDb(Path.Combine(Sandbox, "data", "jellytrim.db"), NullLogger.Instance);
        Db.Initialize();
        Settings = new SettingsStore(Db);
        Audit = new AuditStore(Db);
        Policies = new PolicyStore(Db);
        Inventory = new InventoryStore(Db);
        Explorer = new ExplorerStore(Db);
        Cleanup = new CleanupStore(Db);
        Deleter = new SandboxDeleter(Sandbox);
        Evaluations = new PolicyEvaluationService(Inventory, Policies, Clock);
        Scanner = new InventoryScanner(Host, Inventory, Probe, Volumes, new RootChecker([]), Gate, Evaluations, Clock, NullLogger<InventoryScanner>.Instance);
        Planner = new CleanupPlanner(Host, Inventory, Explorer, Policies, Settings, Cleanup, Audit, Evaluations, Probe, Clock, NullLogger<CleanupPlanner>.Instance);
        Coordinator = NewCoordinator();
        if (enableDeletion)
        {
            Settings.Save(new JellytrimSettings { DeletionEnabled = true, ApprovedRoots = [MoviesRoot, TvRoot] }, 0, Clock.Now);
        }
    }

    public CleanupCoordinator NewCoordinator() => new(Host, Inventory, Policies, Settings, Cleanup, Audit, Evaluations, Probe, Deleter, Volumes, Gate, Clock, NullLogger<CleanupCoordinator>.Instance);

    public string Sandbox { get; }

    public string MoviesRoot { get; }

    public string TvRoot { get; }

    public FakeHost Host { get; } = new();

    public TestClock Clock { get; }

    public JellytrimDb Db { get; }

    public SettingsStore Settings { get; }

    public AuditStore Audit { get; }

    public PolicyStore Policies { get; }

    public InventoryStore Inventory { get; }

    public ExplorerStore Explorer { get; }

    public CleanupStore Cleanup { get; }

    public SandboxDeleter Deleter { get; }

    public IFileProbe Probe { get; set; } = new NativeFileProbe();

    public StubVolumes Volumes { get; } = new();

    public MaintenanceGate Gate { get; } = new();

    public PolicyEvaluationService Evaluations { get; }

    public InventoryScanner Scanner { get; }

    public CleanupPlanner Planner { get; }

    public CleanupCoordinator Coordinator { get; }

    public static string Write(string path, int bytes = 1024)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public HostItem AddMovie(string name, int bytes = 4096, bool ownFolder = true, DateTime? added = null)
    {
        var id = Guid.NewGuid();
        var path = ownFolder ? Path.Combine(MoviesRoot, name, name + ".mkv") : Path.Combine(MoviesRoot, name + ".mkv");
        Write(path, bytes);
        var item = new HostItem
        {
            Id = id,
            Kind = MediaKind.Movie,
            Name = name,
            LibraryId = MoviesLib,
            DateAddedUtc = added ?? Clock.Now.AddDays(-300),
            IsFileBacked = true,
            Path = path,
            Files = [new HostItemFile(path, FileRole.Main, id)],
            VersionIds = [id],
        };
        Host.Items[id] = item;
        return item;
    }

    public (HostItem Series, HostItem Season, List<HostItem> Episodes) AddShow(string name, int season, int episodes, int bytes = 2048)
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var seriesPath = Path.Combine(TvRoot, name);
        var seasonPath = Path.Combine(seriesPath, $"Season {season:00}");
        Directory.CreateDirectory(seasonPath);
        var series = new HostItem { Id = seriesId, Kind = MediaKind.Series, Name = name, LibraryId = TvLib, Path = seriesPath, IsFileBacked = false, VersionIds = [seriesId] };
        var seasonItem = new HostItem { Id = seasonId, Kind = MediaKind.Season, Name = $"Season {season}", LibraryId = TvLib, SeriesId = seriesId, SeasonNumber = season, Path = seasonPath, IsFileBacked = false, VersionIds = [seasonId] };
        Host.Items[seriesId] = series;
        Host.Items[seasonId] = seasonItem;
        var list = new List<HostItem>();
        for (var e = 1; e <= episodes; e++)
        {
            var id = Guid.NewGuid();
            var path = Path.Combine(seasonPath, $"{name} S{season:00}E{e:00}.mkv");
            Write(path, bytes);
            var ep = new HostItem
            {
                Id = id,
                Kind = MediaKind.Episode,
                Name = $"Episode {e}",
                LibraryId = TvLib,
                SeriesId = seriesId,
                SeasonId = seasonId,
                SeriesName = name,
                SeasonNumber = season,
                EpisodeNumber = e,
                DateAddedUtc = Clock.Now.AddDays(-300),
                IsFileBacked = true,
                Path = path,
                Files = [new HostItemFile(path, FileRole.Main, id)],
                VersionIds = [id],
            };
            Host.Items[id] = ep;
            list.Add(ep);
        }

        return (series, seasonItem, list);
    }

    public void MarkPlayed(Guid item, Guid user, int daysAgo = 100) =>
        Host.States[(item, user)] = new UserItemState(WatchState.Played, 0, 1, Clock.Now.AddDays(-daysAgo));

    public void MarkPlayedByAll(Guid item)
    {
        MarkPlayed(item, Ann);
        MarkPlayed(item, Bob);
    }

    public Task ScanAsync() => Scanner.RunAsync("test", null, CancellationToken.None);

    public CleanupPlan PlanFor(params Guid[] ids) =>
        Planner.CreatePlan(new PlanRequest { Type = SelectionType.Items, ItemIds = [.. ids] }, "admin", "corr").Plan;

    public CleanupPolicy SavePolicy(Action<CleanupPolicy>? configure = null)
    {
        var policy = new CleanupPolicy { Name = "test", ParticipatingUserIds = [Ann, Bob] };
        configure?.Invoke(policy);
        return Policies.Create(policy, Clock.Now);
    }

    public async Task<JobRow> ExecuteAsync(CleanupPlan plan)
    {
        var hash = Cleanup.GetPlanSummary(plan.Id)!.Hash;
        Coordinator.Start(plan.Id, new ExecuteRequest(hash, Guid.NewGuid().ToString(), true, plan.ConfirmationPhrase), "admin", "corr");
        await Coordinator.WaitForCurrentAsync();
        return Cleanup.FindJobByPlan(plan.Id)!;
    }

    public void Dispose()
    {
        Db.ReleaseHandles();
        try
        {
            Directory.Delete(Sandbox, true);
        }
        catch (IOException)
        {
        }
    }
}
