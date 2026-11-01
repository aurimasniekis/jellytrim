using Jellytrim.Core.Cleanup;
using Jellytrim.Core.Inventory;
using Jellytrim.Core.Persistence;
using Jellytrim.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellytrim.Runtime;

/// <summary>Startup and shutdown. Startup only initializes the database and finalizes interrupted jobs: nothing here scans or blocks the server.</summary>
public sealed class JellytrimRuntime(
    JellytrimDb db,
    CleanupCoordinator coordinator,
    InventoryScanner scanner,
    InventoryStore inventory,
    CleanupStore cleanup,
    AuditStore audit,
    SettingsStore settings,
    TimeProvider clock,
    ILogger<JellytrimRuntime> logger) : IHostedService, IDisposable
{
    private readonly object _scanLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task _scan = Task.CompletedTask;

    /// <summary>When set, the database could not be opened. The API answers 503 with this message, and nothing can delete.</summary>
    public string? StartupError { get; private set; }

    public bool ScanRunning
    {
        get
        {
            lock (_scanLock)
            {
                return !_scan.IsCompleted;
            }
        }
    }

    public int ScanItemsSeen => scanner.ItemsSeen;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            db.Initialize();
            coordinator.RecoverInterruptedJobs();
            Prune();
            logger.LogInformation("jellytrim started {SchemaVersion}", db.SchemaVersion());
        }
        catch (DatabaseTooNewException e)
        {
            StartupError = e.Message;
            logger.LogError("jellytrim is disabled: {Reason}", e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            StartupError = "The jellytrim database could not be opened. See the server log.";
            logger.LogError(e, "jellytrim is disabled: its database could not be opened");
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            Task scan;
            lock (_scanLock)
            {
                scan = _scan;
            }

            await scan.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or JellytrimException)
        {
            logger.LogDebug("jellytrim scan ended during shutdown: {Reason}", e.GetType().Name);
        }

        await coordinator.DisposeAsync().ConfigureAwait(false);
        db.ReleaseHandles();
    }

    public void Dispose() => _lifetime.Dispose();

    /// <summary>Starts a background scan on behalf of an API request and returns at once.</summary>
    public void StartScan(string trigger)
    {
        lock (_scanLock)
        {
            if (!_scan.IsCompleted)
            {
                throw new JellytrimException(ErrorCodes.ScanRunning, "A scan is already running.", 409);
            }

            _scan = Task.Run(async () =>
            {
                try
                {
                    await scanner.RunAsync(trigger, null, _lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    logger.LogInformation("jellytrim scan cancelled");
                }
                catch (JellytrimException e)
                {
                    logger.LogInformation("jellytrim scan not started: {Reason}", e.Code);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "jellytrim scan failed");
                }
            });
        }
    }

    /// <summary>Runs a scan from a scheduled task, respecting that task's cancellation.</summary>
    public async Task RunScheduledScanAsync(IProgress<double> progress, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        try
        {
            await scanner.RunAsync("schedule", progress, linked.Token).ConfigureAwait(false);
            Prune();
        }
        catch (JellytrimException e)
        {
            // Another scan or a cleanup is running. A skipped scheduled scan is harmless.
            logger.LogInformation("jellytrim scheduled scan skipped: {Reason}", e.Code);
        }
    }

    private void Prune()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var config = settings.Get().Value;
        audit.Prune(now.AddDays(-config.AuditRetentionDays));
        cleanup.PruneOlderThan(now.AddDays(-config.AuditRetentionDays));
        cleanup.PruneUnexecutedPlans(now.AddDays(-7));
        inventory.PruneSnapshots(now.AddDays(-config.SnapshotRetentionDays));
    }
}
