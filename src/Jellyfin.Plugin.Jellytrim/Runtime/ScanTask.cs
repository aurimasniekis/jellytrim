using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.Jellytrim.Runtime;

/// <summary>Scheduled scan. Scans only read. There is no scheduled deletion in jellytrim; see ADR-0005.</summary>
public sealed class ScanTask(JellytrimRuntime runtime) : IScheduledTask
{
    public string Name => "jellytrim: scan storage";

    public string Key => "JellytrimScan";

    public string Description => "Reconciles jellytrim's inventory with the library and records a storage snapshot. Reads only.";

    public string Category => "jellytrim";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        runtime.StartupError is null ? runtime.RunScheduledScanAsync(progress, cancellationToken) : Task.CompletedTask;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks },
    ];
}
