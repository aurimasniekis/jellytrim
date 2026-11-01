using Jellytrim.Core.Abstractions;
using Jellytrim.Core.Domain;
using Jellytrim.Core.FileSystem;
using Jellytrim.Core.Persistence;
using Jellytrim.Core.Services;
using Microsoft.Extensions.Logging;

namespace Jellytrim.Core.Inventory;

public interface IVolumeProbe
{
    /// <summary>Returns observed capacity, or null when the volume cannot be measured.</summary>
    (long Total, long Free)? Measure(string path);
}

public sealed class DriveInfoVolumeProbe : IVolumeProbe
{
    public (long Total, long Free)? Measure(string path)
    {
        try
        {
            var drive = new DriveInfo(path);
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

public sealed record RootStatus(string Path, bool Online, string? Detail);

/// <summary>Library-root availability checks with bounded retry and backoff for storage outages.</summary>
public sealed class RootChecker(TimeSpan[]? backoff = null)
{
    private readonly TimeSpan[] _backoff = backoff ?? [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)];

    public async Task<RootStatus> CheckAsync(string root, bool expectContent, CancellationToken token)
    {
        RootStatus status = Once(root, expectContent);
        foreach (var delay in _backoff)
        {
            if (status.Online)
            {
                break;
            }

            await Task.Delay(delay, token).ConfigureAwait(false);
            status = Once(root, expectContent);
        }

        return status;
    }

    public static RootStatus Once(string root, bool expectContent)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return new RootStatus(root, false, "The directory does not exist or cannot be reached.");
            }

            // An unmounted mount point usually appears as an existing empty directory. Having indexed files here
            // and seeing nothing is treated as an outage rather than as everything having been deleted.
            var empty = !Directory.EnumerateFileSystemEntries(root).Any();
            return empty && expectContent
                ? new RootStatus(root, false, "The root is empty although files were indexed under it. Its mount may be missing.")
                : new RootStatus(root, true, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new RootStatus(root, false, e.GetType().Name);
        }
    }
}

public sealed record ScanOutcome(string Status, int Items, int Files, IReadOnlyList<string> Warnings);

/// <summary>
/// Full reconciliation of the server's library into the inventory. Safely restartable: every page is committed under a
/// new generation, and the sweep runs only for libraries read to completion. Cancelling or crashing leaves prior rows in place.
/// </summary>
public sealed class InventoryScanner(
    IMediaHost host,
    InventoryStore inventory,
    IFileProbe probe,
    IVolumeProbe volumes,
    RootChecker roots,
    MaintenanceGate gate,
    PolicyEvaluationService evaluations,
    TimeProvider clock,
    ILogger<InventoryScanner> logger)
{
    public const string GateName = "scan";
    private const int PageSize = 500;
    private const int ProbeConcurrency = 8;
    private static readonly TimeSpan PageProbeTimeout = TimeSpan.FromMinutes(3);

    public int ItemsSeen { get; private set; }

    public async Task<ScanOutcome> RunAsync(string trigger, IProgress<double>? progress, CancellationToken token)
    {
        if (!gate.TryEnter(GateName))
        {
            throw new JellytrimException(gate.Holder == GateName ? ErrorCodes.ScanRunning : ErrorCodes.CleanupRunning, gate.Holder == GateName ? "A scan is already running." : "A cleanup job is running. Scans wait until it finishes.", 409);
        }

        var generation = inventory.BeginScan(trigger, clock.GetUtcNow().UtcDateTime);
        var warnings = new List<string>();
        var items = 0;
        var files = 0;
        ItemsSeen = 0;
        logger.LogInformation("jellytrim scan started {ScanGeneration} {ScanTrigger}", generation, trigger);
        try
        {
            var users = host.GetUsers();
            inventory.UpsertUsers(users);
            var userIds = users.Select(u => u.Id).ToList();
            var failedUsers = new HashSet<Guid>();
            var libraries = host.GetLibraries();
            inventory.RemoveLibrariesExcept(libraries.Select(l => l.Id).ToList());
            var known = inventory.ListLibraries().ToDictionary(l => l.Id);
            var fullyScanned = new List<Guid>();
            var observedVolumes = new Dictionary<string, (string Sample, HashSet<Guid> Libraries)>();

            for (var index = 0; index < libraries.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var library = libraries[index];
                var expectContent = known.TryGetValue(library.Id, out var previous) && previous.FileCount > 0;
                var statuses = new List<RootStatus>();
                foreach (var location in library.Locations)
                {
                    statuses.Add(await roots.CheckAsync(location, expectContent, token).ConfigureAwait(false));
                }

                var offline = statuses.Where(s => !s.Online).ToList();
                foreach (var root in statuses.Where(s => s.Online))
                {
                    var observation = probe.Probe(root.Path);
                    if (observation.VolumeId is { } volume)
                    {
                        if (!observedVolumes.TryGetValue(volume, out var entry))
                        {
                            observedVolumes[volume] = entry = (root.Path, []);
                        }

                        entry.Libraries.Add(library.Id);
                    }
                }

                foreach (var o in offline)
                {
                    warnings.Add($"Library '{library.Name}': root {o.Path} is unavailable. {o.Detail} Its items were kept and not re-verified.");
                }

                var allOffline = library.Locations.Count > 0 && offline.Count == library.Locations.Count;
                inventory.UpsertLibrary(library, offline.Count == 0, offline.FirstOrDefault()?.Detail, generation, clock.GetUtcNow().UtcDateTime);
                if (allOffline)
                {
                    continue;
                }

                var complete = true;
                try
                {
                    var start = 0;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        var page = host.ReadItemPage(library.Id, start, PageSize, userIds);
                        if (page.Items.Count == 0)
                        {
                            break;
                        }

                        failedUsers.UnionWith(page.FailedUsers);
                        var offlineRoots = offline.Select(o => o.Path).ToList();
                        var scanned = await ProbeAsync(page.Items, offlineRoots, token).ConfigureAwait(false);
                        inventory.WritePage(scanned, page, userIds, generation, clock.GetUtcNow().UtcDateTime);
                        items += scanned.Count;
                        files += scanned.Sum(s => s.Files.Count);
                        ItemsSeen = items;
                        start += page.Items.Count;
                        progress?.Report((index + ((double)Math.Min(start, page.TotalCount) / Math.Max(1, page.TotalCount))) / libraries.Count * 100);
                        if (start >= page.TotalCount)
                        {
                            break;
                        }
                    }
                }
                catch (TimeoutException)
                {
                    complete = false;
                    warnings.Add($"Library '{library.Name}': storage stopped responding. Its scan was abandoned and its items were kept.");
                    inventory.UpsertLibrary(library, false, "Storage stopped responding during the scan.", generation, clock.GetUtcNow().UtcDateTime);
                }

                if (complete && offline.Count == 0)
                {
                    fullyScanned.Add(library.Id);
                }
            }

            inventory.CompleteGeneration(generation, fullyScanned);
            inventory.MarkUsersReadable(userIds.Where(u => !failedUsers.Contains(u)), generation);
            foreach (var failed in failedUsers)
            {
                warnings.Add($"State for user {failed:N} could not be read. Their state is Unknown and blocks cleanup decisions.");
            }

            SaveVolumes(observedVolumes);
            inventory.WriteSnapshot(clock.GetUtcNow().UtcDateTime);
            evaluations.EvaluateAll();
            var status = warnings.Count == 0 ? "Completed" : "CompletedWithWarnings";
            inventory.FinishScan(generation, status, clock.GetUtcNow().UtcDateTime, items, files, null, warnings);
            logger.LogInformation("jellytrim scan finished {ScanGeneration} {ScanStatus} {ScanItems} {ScanFiles} {ScanWarnings}", generation, status, items, files, warnings.Count);
            return new ScanOutcome(status, items, files, warnings);
        }
        catch (OperationCanceledException)
        {
            inventory.FinishScan(generation, "Cancelled", clock.GetUtcNow().UtcDateTime, items, files, "Cancelled. Earlier results were kept.", warnings);
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "jellytrim scan failed {ScanGeneration}", generation);
            inventory.FinishScan(generation, "Failed", clock.GetUtcNow().UtcDateTime, items, files, "The scan failed; see the server log. Earlier results were kept.", warnings);
            throw;
        }
        finally
        {
            gate.Exit(GateName);
        }
    }

    public void RefreshVolumes()
    {
        var existing = inventory.ListVolumes();
        inventory.SaveVolumes(existing.Select(v => Measure(v.VolumeId, v.SamplePath, v.LibraryIds)).ToList());
    }

    private void SaveVolumes(Dictionary<string, (string Sample, HashSet<Guid> Libraries)> observed) =>
        inventory.SaveVolumes(observed.Select(kv => Measure(kv.Key, kv.Value.Sample, kv.Value.Libraries.ToList())).ToList());

    private VolumeRow Measure(string id, string sample, IReadOnlyList<Guid> libraries)
    {
        var measured = volumes.Measure(sample);
        return new VolumeRow(id, sample, measured?.Total, measured?.Free, clock.GetUtcNow().UtcDateTime, measured is not null, libraries);
    }

    private async Task<List<ScannedItem>> ProbeAsync(IReadOnlyList<HostItem> items, IReadOnlyList<string> offlineRoots, CancellationToken token)
    {
        var results = new ScannedItem[items.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = ProbeConcurrency, CancellationToken = token };
        var work = Parallel.ForEachAsync(Enumerable.Range(0, items.Count), options, (i, _) =>
        {
            results[i] = ProbeOne(items[i], offlineRoots);
            return ValueTask.CompletedTask;
        });

        // stat on a hung network mount cannot be cancelled. Bound the wait so the scan abandons that library rather than hanging forever.
        try
        {
            await work.WaitAsync(PageProbeTimeout, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw;
        }

        return results.ToList();
    }

    private ScannedItem ProbeOne(HostItem item, IReadOnlyList<string> offlineRoots)
    {
        if (item.Kind is MediaKind.Series or MediaKind.Season)
        {
            return new ScannedItem(item, ItemHealth.Container, []);
        }

        if (!item.IsFileBacked || item.Files.Count == 0)
        {
            return new ScannedItem(item, ItemHealth.NotFileBacked, []);
        }

        var observed = new List<ObservedFile>(item.Files.Count);
        var health = ItemHealth.Ok;
        foreach (var file in item.Files)
        {
            var observation = probe.Probe(file.Path);
            observed.Add(new ObservedFile(file.Path, file.Role, file.VersionItemId, observation));
            var underOffline = offlineRoots.Any(r => file.Path.StartsWith(r.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathGuard.PlatformComparison));
            var fileHealth = observation.Status switch
            {
                ProbeStatus.Ok when underOffline => ItemHealth.Unavailable,
                ProbeStatus.Ok => ItemHealth.Ok,
                ProbeStatus.NotFound when !underOffline => ItemHealth.Missing,
                _ => ItemHealth.Unavailable,
            };

            // Unavailable outranks Missing: if anything is unverifiable, nothing is claimed about the whole item.
            health = (health, fileHealth) switch
            {
                (ItemHealth.Unavailable, _) or (_, ItemHealth.Unavailable) => ItemHealth.Unavailable,
                (ItemHealth.Missing, _) or (_, ItemHealth.Missing) => ItemHealth.Missing,
                _ => ItemHealth.Ok,
            };
        }

        return new ScannedItem(item, health, observed);
    }
}
