using Jellytrim.Core.Abstractions;
using Jellytrim.Core.Domain;
using Jellytrim.Core.FileSystem;
using Jellytrim.Core.Inventory;
using Jellytrim.Core.Persistence;
using Jellytrim.Core.Policies;
using Jellytrim.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellytrim.Core.Cleanup;

public interface IFileDeleter
{
    /// <summary>Unlinks one regular file. Never recursive.</summary>
    void DeleteFile(string path);

    /// <summary>Removes a directory only if empty. Returns false if it holds anything.</summary>
    bool DeleteDirectoryIfEmpty(string path);
}

public sealed class FileDeleter : IFileDeleter
{
    public void DeleteFile(string path) => File.Delete(path);

    public bool DeleteDirectoryIfEmpty(string path)
    {
        try
        {
            // recursive:false fails on any non-empty directory. There is no recursive delete anywhere in jellytrim.
            Directory.Delete(path, recursive: false);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public sealed record ExecuteRequest(string PlanHash, string IdempotencyKey, bool Acknowledged, string? ConfirmationPhrase);

/// <summary>
/// Owns plan execution. A job runs: preflight of every item, where any material change rejects the whole plan with nothing
/// deleted; then per item a re-check at the moment of deletion, a committed intent, the unlinks, and server reconciliation.
/// Status is written before each destructive step, so a crash leaves an accurate record, and jobs are never resumed.
/// </summary>
public sealed class CleanupCoordinator(
    IMediaHost host,
    InventoryStore inventory,
    PolicyStore policies,
    SettingsStore settings,
    CleanupStore cleanup,
    AuditStore audit,
    PolicyEvaluationService evaluations,
    IFileProbe probe,
    IFileDeleter deleter,
    IVolumeProbe volumes,
    MaintenanceGate gate,
    TimeProvider clock,
    ILogger<CleanupCoordinator> logger) : IAsyncDisposable
{
    public const string GateName = "cleanup";
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lock = new();
    private Task _running = Task.CompletedTask;
    private int _disposed;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private IMediaHost Host => host;

    private SettingsStore Settings => settings;

    private PolicyStore Policies => policies;

    private IFileProbe Probe => probe;

    private InventoryStore Inventory => inventory;

    /// <summary>Validates the request and starts the job in the background. Safe to retry with the same idempotency key.</summary>
    public JobRow Start(Guid planId, ExecuteRequest request, string? actor, string correlationId)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 100)
        {
            throw JellytrimException.Validation(["An idempotency key of up to 100 characters is required."]);
        }

        if (cleanup.FindJobByIdempotencyKey(request.IdempotencyKey) is { } previous)
        {
            if (previous.PlanId != planId)
            {
                throw new JellytrimException(ErrorCodes.PlanAlreadyExecuted, "This idempotency key was already used for a different plan.", 409);
            }

            return previous;
        }

        var config = settings.Get().Value;
        if (!config.DeletionEnabled)
        {
            throw new JellytrimException(ErrorCodes.DeletionDisabled, "Deletion is disabled. Enable it in Settings and generate a new plan.", 409);
        }

        var summary = cleanup.GetPlanSummary(planId) ?? throw JellytrimException.NotFound("Plan");
        var document = cleanup.GetPlanDocument(planId)!;
        if (!string.Equals(summary.Hash, request.PlanHash, StringComparison.Ordinal) || !string.Equals(CleanupPlan.ComputeHash(document), summary.Hash, StringComparison.Ordinal))
        {
            throw new JellytrimException(ErrorCodes.PlanHashMismatch, "This confirmation does not match the stored plan. Review the plan again.", 409);
        }

        var plan = Json.Read<CleanupPlan>(document);
        if (plan.PreviewOnly)
        {
            throw new JellytrimException(ErrorCodes.PlanPreviewOnly, "This plan was generated while deletion was disabled. Generate a new plan.", 409);
        }

        if (cleanup.FindJobByPlan(planId) is not null || summary.Status != CleanupStore.PlanReady)
        {
            throw new JellytrimException(ErrorCodes.PlanAlreadyExecuted, "This plan was already used. Generate a new plan.", 409);
        }

        if (Now >= summary.ExpiresAtUtc)
        {
            throw new JellytrimException(ErrorCodes.PlanExpired, "This plan expired. Generate a new plan.", 410);
        }

        if (plan.Items.Count == 0)
        {
            throw new JellytrimException(ErrorCodes.PlanEmpty, "This plan has nothing to delete.", 409);
        }

        if (!request.Acknowledged)
        {
            throw new JellytrimException(ErrorCodes.ConfirmationRequired, "Confirm that this deletion is permanent.", 400);
        }

        if (plan.HighImpact && !string.Equals(request.ConfirmationPhrase, plan.ConfirmationPhrase, StringComparison.Ordinal))
        {
            throw new JellytrimException(ErrorCodes.ConfirmationRequired, $"This is a high-impact plan. Type exactly: {plan.ConfirmationPhrase}", 400);
        }

        if (!gate.TryEnter(GateName))
        {
            throw new JellytrimException(gate.Holder == GateName ? ErrorCodes.JobActive : ErrorCodes.ScanRunning, gate.Holder == GateName ? "Another cleanup job is running." : "A scan is running. Try again when it finishes.", 409);
        }

        var job = new JobRow
        {
            Id = Guid.NewGuid(),
            PlanId = planId,
            CorrelationId = correlationId,
            Status = JobStatus.Queued,
            CreatedBy = actor,
            CreatedAtUtc = Now,
            TotalItems = plan.Items.Count + plan.Containers.Count,
            ReclaimEstimateBytes = plan.Totals.EstimatedReclaimableBytes,
        };

        try
        {
            var entries = plan.Items.Select(i => (new PlanJobEntry(i.ItemId, i.Name), i.Files))
                .Concat(plan.Containers.Select(c => (new PlanJobEntry(c.ItemId, $"{c.Name} (folder artwork and record)"), c.Files)))
                .ToList();
            cleanup.InsertJob(job, request.IdempotencyKey, entries);
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19)
        {
            gate.Exit(GateName);
            if (cleanup.FindJobByIdempotencyKey(request.IdempotencyKey) is { } raced && raced.PlanId == planId)
            {
                return raced;
            }

            throw new JellytrimException(ErrorCodes.JobActive, "Another cleanup job is active, or this plan was already used.", 409);
        }
        catch
        {
            gate.Exit(GateName);
            throw;
        }

        audit.Record(Now, actor, "job.started", job.Id.ToString("N"), correlationId, "ok", Json.Write(new { planId, plan.Totals }));
        lock (_lock)
        {
            _running = Task.Run(() => RunGuardedAsync(job, plan), CancellationToken.None);
        }

        return job;
    }

    public void RequestCancel(Guid jobId, string? actor, string correlationId)
    {
        if (cleanup.GetJob(jobId) is null)
        {
            throw JellytrimException.NotFound("Job");
        }

        if (!cleanup.RequestCancel(jobId))
        {
            throw new JellytrimException(ErrorCodes.JobNotActive, "This job already finished.", 409);
        }

        audit.Record(Now, actor, "job.cancel_requested", jobId.ToString("N"), correlationId, "ok");
    }

    /// <summary>Finalizes jobs that were active when the server stopped. Nothing is resumed; what happened is established and reported.</summary>
    public void RecoverInterruptedJobs()
    {
        foreach (var id in cleanup.ActiveJobIds())
        {
            var now = Now;
            foreach (var (seq, fileSeq, path) in cleanup.FilesInStatus(id, FileStatus.Deleting))
            {
                // Only observe here. Unknown remains unknown: an unreadable path does not prove deletion.
                var observed = probe.Probe(path);
                cleanup.SetFile(id, seq, fileSeq, observed.Status == ProbeStatus.NotFound ? FileStatus.Deleted : observed.Status == ProbeStatus.Ok ? FileStatus.NotAttempted : FileStatus.Uncertain, now, "RECOVERED_AFTER_CRASH");
            }

            foreach (var seq in cleanup.ItemsInStatus(id, ItemStatus.InProgress))
            {
                cleanup.SetItem(id, seq, ItemStatus.Uncertain, now, "INTERRUPTED", "The server stopped during this item. See its file statuses. A scan will reconcile the server's record.", finished: true);
            }

            cleanup.MarkRemainingItems(id, ItemStatus.NotRun, now);
            cleanup.SetJobStatus(id, JobStatus.Interrupted, now, "The server stopped during this job. It was not resumed. Run a scan and generate a new plan for anything remaining.", finished: true);
            if (cleanup.GetJob(id) is { } job)
            {
                cleanup.SetPlanStatus(job.PlanId, CleanupStore.PlanRejected);
                audit.Record(now, null, "job.interrupted", id.ToString("N"), job.CorrelationId, "interrupted");
                logger.LogWarning("jellytrim job interrupted by shutdown, not resuming {JobId} {CorrelationId}", id, job.CorrelationId);
            }
        }
    }

    public Task WaitForCurrentAsync()
    {
        lock (_lock)
        {
            return _running;
        }
    }

    /// <summary>Safe to call twice: the host's StopAsync and then the DI container both dispose this.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await WaitForCurrentAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("jellytrim cleanup job did not stop within the shutdown window; recovery will finalize it on next start");
        }

        _shutdown.Dispose();
    }

    private async Task RunGuardedAsync(JobRow job, CleanupPlan plan)
    {
        await Task.Yield();
        try
        {
            Run(job, plan);
        }
        catch (Exception e)
        {
            logger.LogError(e, "jellytrim cleanup job failed {JobId} {CorrelationId}", job.Id, job.CorrelationId);
            cleanup.MarkRemainingItems(job.Id, ItemStatus.NotRun, Now);
            foreach (var seq in cleanup.ItemsInStatus(job.Id, ItemStatus.InProgress))
            {
                cleanup.SetItem(job.Id, seq, ItemStatus.Uncertain, Now, "JOB_FAILED", "The job failed during this item.", finished: true);
            }

            cleanup.SetJobStatus(job.Id, JobStatus.Failed, Now, "The job failed; see the server log. Completed deletions cannot be undone.", finished: true);
            cleanup.SetPlanStatus(job.PlanId, CleanupStore.PlanRejected);
            audit.Record(Now, job.CreatedBy, "job.failed", job.Id.ToString("N"), job.CorrelationId, "failed");
        }
        finally
        {
            gate.Exit(GateName);
        }
    }

    private void Run(JobRow job, CleanupPlan plan)
    {
        cleanup.SetJobStatus(job.Id, JobStatus.Preflight, Now, started: true);
        var before = MeasureFree(plan);
        cleanup.SetJobVolumes(job.Id, before, null);

        var context = new ValidationContext(this, plan);
        var problems = Preflight(plan, context);
        if (problems.Count > 0)
        {
            // Nothing has been touched. The plan no longer describes reality, so it needs a fresh preview.
            cleanup.MarkRemainingItems(job.Id, ItemStatus.NotRun, Now);
            cleanup.SetJobProblems(job.Id, problems);
            cleanup.SetJobStatus(job.Id, JobStatus.Rejected, Now, $"{problems.Count} item(s) changed since this plan was made. Nothing was deleted. Generate a new plan.", finished: true);
            cleanup.SetPlanStatus(plan.Id, CleanupStore.PlanRejected);
            audit.Record(Now, job.CreatedBy, "job.rejected", job.Id.ToString("N"), job.CorrelationId, "rejected", Json.Write(new { problems = problems.Count, first = problems[0].Code }));
            logger.LogWarning("jellytrim plan rejected at preflight {JobId} {PlanId} {ProblemCount} {CorrelationId}", job.Id, plan.Id, problems.Count, job.CorrelationId);
            return;
        }

        cleanup.SetJobStatus(job.Id, JobStatus.Running, Now);
        var deleted = new List<Guid>();
        var issues = 0;
        var cancelled = false;
        for (var seq = 0; seq < plan.Items.Count; seq++)
        {
            if (_shutdown.IsCancellationRequested || cleanup.IsCancelRequested(job.Id))
            {
                cancelled = true;
                break;
            }

            // An administrator may remove a root or edit the policy during a long job, and that must take effect at once.
            context.Refresh();
            var item = plan.Items[seq];
            var live = host.ReadLiveStates(StateIds(item), context.LiveUserIds);
            var problem = context.Validate(item, live, host.GetActivePlayback());
            if (problem is not null)
            {
                issues++;
                cleanup.SetItem(job.Id, seq, ItemStatus.Skipped, Now, problem.Code, problem.Message, finished: true);
                cleanup.MarkFilesNotAttempted(job.Id, seq, Now);
                continue;
            }

            if (ExecuteItem(job.Id, seq, item, context))
            {
                deleted.Add(item.ItemId);
                deleted.AddRange(item.SharedRecords.Select(r => r.ItemId));
            }
            else
            {
                issues++;
            }
        }

        var deletedItems = plan.Items.Count(i => deleted.Contains(i.ItemId));
        if (cancelled)
        {
            cleanup.MarkRemainingItems(job.Id, ItemStatus.Cancelled, Now);
        }
        else
        {
            issues += ExecuteContainers(job.Id, plan, deleted, context);
            cancelled = cleanup.IsCancelRequested(job.Id) && cleanup.ItemsInStatus(job.Id, ItemStatus.Pending).Count > 0;
            if (cancelled)
            {
                cleanup.MarkRemainingItems(job.Id, ItemStatus.Cancelled, Now);
            }
        }

        inventory.RemoveItems(deleted);
        evaluations.EvaluateAll();
        cleanup.SetJobVolumes(job.Id, null, MeasureFree(plan));
        inventory.WriteSnapshot(Now);
        var status = cancelled ? JobStatus.Cancelled : issues > 0 ? JobStatus.CompletedWithIssues : JobStatus.Completed;
        var message = cancelled
            ? $"Cancelled. {deletedItems} item(s) were already permanently deleted and cannot be restored; the rest were not touched."
            : issues > 0 ? $"{deletedItems} item(s) deleted, {issues} with issues. Review the report." : $"{deletedItems} item(s) permanently deleted.";
        cleanup.SetJobStatus(job.Id, status, Now, message, finished: true);
        cleanup.SetPlanStatus(plan.Id, CleanupStore.PlanExecuted);
        audit.Record(Now, job.CreatedBy, "job.finished", job.Id.ToString("N"), job.CorrelationId, status, Json.Write(new { deleted = deletedItems, issues }));
        logger.LogInformation("jellytrim job finished {JobId} {JobStatus} {DeletedItems} {Issues} {CorrelationId}", job.Id, status, deletedItems, issues, job.CorrelationId);
    }

    private static List<Guid> StateIds(PlanItem item)
    {
        var ids = new List<Guid> { item.ItemId };
        if (item.SeasonId is { } season)
        {
            ids.Add(season);
        }

        if (item.SeriesId is { } series)
        {
            ids.Add(series);
        }

        foreach (var shared in item.SharedRecords)
        {
            ids.Add(shared.ItemId);
            if (shared.SeasonId is { } sharedSeason)
            {
                ids.Add(sharedSeason);
            }

            if (shared.SeriesId is { } sharedSeries)
            {
                ids.Add(sharedSeries);
            }
        }

        return ids;
    }

    private List<JobProblem> Preflight(CleanupPlan plan, ValidationContext context)
    {
        var problems = new List<JobProblem>();

        // A batched read for the whole plan avoids item-by-user round trips during preflight.
        var live = host.ReadLiveStates(plan.Items.SelectMany(StateIds).Distinct().ToList(), context.LiveUserIds);
        var playback = host.GetActivePlayback();
        foreach (var item in plan.Items)
        {
            if (context.Validate(item, live, playback) is { } problem)
            {
                problems.Add(problem);
            }
        }

        return problems;
    }

    private bool ExecuteItem(Guid job, int seq, PlanItem item, ValidationContext context)
    {
        cleanup.SetItem(job, seq, ItemStatus.InProgress, Now, started: true);
        long freed = 0;
        var failed = false;
        string? failure = null;
        for (var f = 0; f < item.Files.Count; f++)
        {
            var file = item.Files[f];
            var (status, code) = DeleteOne(job, seq, f, file, context);
            if (status == FileStatus.Deleted)
            {
                freed += file.EstimatedFreedBytes;
            }
            else if (status != FileStatus.AlreadyGone)
            {
                failed = true;
                failure ??= code;
                for (var rest = f + 1; rest < item.Files.Count; rest++)
                {
                    cleanup.SetFile(job, seq, rest, FileStatus.NotAttempted, Now);
                }

                break;
            }
        }

        if (failed)
        {
            cleanup.SetItem(job, seq, ItemStatus.Failed, Now, failure, "One or more files could not be deleted. Files already removed cannot be restored. The server's record was left in place; a scan will mark it.", freed, finished: true);
            return false;
        }

        foreach (var directory in item.DirectoriesIfEmpty)
        {
            if (context.Guard.CheckLocation(directory).IsSafe)
            {
                deleter.DeleteDirectoryIfEmpty(directory);
            }
        }

        var reconcile = "Removed";
        try
        {
            host.RemoveItemRecord(item.ItemId);
            foreach (var other in item.SharedRecords)
            {
                host.RemoveItemRecord(other.ItemId);
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "jellytrim could not remove server record {ItemId} after deleting its files", item.ItemId);
            reconcile = "PendingNextLibraryScan";
        }

        cleanup.SetItem(job, seq, ItemStatus.Deleted, Now, freed: freed, reconcile: reconcile, finished: true);
        return true;
    }

    private (string Status, string? Code) DeleteOne(Guid job, int seq, int fileSeq, PlanFile file, ValidationContext context)
    {
        // Last check before unlink. The window after this cannot be closed on POSIX; docs/deletion-safety.md records the residual risk.
        var observation = probe.Probe(file.Path);
        if (observation.Status == ProbeStatus.NotFound && file.Role == FileRole.Sidecar)
        {
            cleanup.SetFile(job, seq, fileSeq, FileStatus.AlreadyGone, Now);
            return (FileStatus.AlreadyGone, null);
        }

        var check = context.Guard.CheckDeletableFile(file.Path, observation);
        if (!check.IsSafe)
        {
            cleanup.SetFile(job, seq, fileSeq, FileStatus.Refused, Now, "PATH_" + check.Refusal);
            return (FileStatus.Refused, "PATH_" + check.Refusal);
        }

        if (ValidationContext.ChangeOf(file, observation) is { } change)
        {
            cleanup.SetFile(job, seq, fileSeq, FileStatus.Refused, Now, change);
            return (FileStatus.Refused, change);
        }

        cleanup.SetFile(job, seq, fileSeq, FileStatus.Deleting, Now);
        try
        {
            deleter.DeleteFile(file.Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var code = e is UnauthorizedAccessException ? "PERMISSION_DENIED" : "IO_ERROR";
            cleanup.SetFile(job, seq, fileSeq, FileStatus.Failed, Now, code);
            return (FileStatus.Failed, code);
        }

        cleanup.SetFile(job, seq, fileSeq, FileStatus.Deleted, Now);
        return (FileStatus.Deleted, null);
    }

    /// <summary>Seasons and shows, tracked with the same durable per-file records as items, and run only after every child succeeded.</summary>
    private int ExecuteContainers(Guid job, CleanupPlan plan, List<Guid> deleted, ValidationContext context)
    {
        var done = deleted.ToHashSet();
        var issues = 0;

        // Seasons first, so an emptied season folder is gone by the time its show is checked.
        var ordered = plan.Containers.Select((c, index) => (Container: c, Seq: plan.Items.Count + index)).OrderBy(c => c.Container.Kind == MediaKind.Series).ToList();
        foreach (var (container, seq) in ordered)
        {
            context.Refresh();
            if (_shutdown.IsCancellationRequested || cleanup.IsCancelRequested(job) || !context.DeletionEnabled)
            {
                break; // Whatever remains is marked by the caller.
            }

            void Keep(string code, string message)
            {
                cleanup.SetItem(job, seq, ItemStatus.Skipped, Now, code, message, finished: true);
                cleanup.MarkFilesNotAttempted(job, seq, Now);
            }

            if (!container.ChildItemIds.All(done.Contains))
            {
                Keep("CHILD_NOT_DELETED", "Kept, because not all of its episodes were deleted.");
                continue;
            }

            // Checked before touching anything: artwork must not go from a show that is staying.
            bool hasChildren;
            try
            {
                hasChildren = host.HasChildren(container.ItemId);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "jellytrim could not read children of {ItemId}", container.ItemId);
                Keep("CHILDREN_UNKNOWN", "Kept, because what remains below it could not be read.");
                issues++;
                continue;
            }

            if (hasChildren)
            {
                Keep("CONTAINER_NOT_EMPTY", "Kept, because the server still has other items below it.");
                continue;
            }

            cleanup.SetItem(job, seq, ItemStatus.InProgress, Now, started: true);
            long freed = 0;
            string? failure = null;
            for (var f = 0; f < container.Files.Count; f++)
            {
                var (status, code) = DeleteOne(job, seq, f, container.Files[f], context);
                if (status == FileStatus.Deleted)
                {
                    freed += container.Files[f].EstimatedFreedBytes;
                }
                else if (status != FileStatus.AlreadyGone)
                {
                    failure ??= code;
                }
            }

            if (failure is not null)
            {
                issues++;
                cleanup.SetItem(job, seq, ItemStatus.Failed, Now, failure, "Some folder artwork could not be deleted. See its files. The record was left in place.", freed, finished: true);
                continue;
            }

            foreach (var directory in container.DirectoriesIfEmpty)
            {
                if (context.Guard.CheckLocation(directory).IsSafe)
                {
                    deleter.DeleteDirectoryIfEmpty(directory);
                }
            }

            var reconcile = "Removed";
            try
            {
                host.RemoveItemRecord(container.ItemId);
                done.Add(container.ItemId);
                deleted.Add(container.ItemId);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "jellytrim could not reconcile container {ItemId}", container.ItemId);
                reconcile = "PendingNextLibraryScan";
            }

            cleanup.SetItem(job, seq, ItemStatus.Deleted, Now, freed: freed, reconcile: reconcile, finished: true);
        }

        return issues;
    }

    private List<VolumeFree> MeasureFree(CleanupPlan plan) => plan.Volumes
        .Select(v => new VolumeFree(v.VolumeId, v.SamplePath, volumes.Measure(v.SamplePath)?.Free))
        .ToList();

    /// <summary>The conditions an item must still meet at the moment it is processed.</summary>
    private sealed class ValidationContext
    {
        private readonly CleanupCoordinator _owner;
        private readonly IReadOnlyList<HostUser> _users;
        private readonly HashSet<Guid> _liveUsers;
        private readonly CleanupPolicy _policy;
        private readonly Dictionary<Guid, IReadOnlyList<LeafItem>> _siblings = [];
        private bool _policyChanged;

        public ValidationContext(CleanupCoordinator owner, CleanupPlan plan)
        {
            _owner = owner;
            _users = owner.Host.GetUsers();
            _liveUsers = _users.Select(u => u.Id).ToHashSet();
            _policy = plan.Policy;
            Guard = new PathGuard([]);
            Refresh();
        }

        public PathGuard Guard { get; private set; }

        public bool DeletionEnabled { get; private set; }

        public IReadOnlyCollection<Guid> LiveUserIds => _liveUsers;

        /// <summary>Re-reads what an administrator can change during a job: deletion enabled, approved roots, library locations, the policy.</summary>
        public void Refresh()
        {
            var settings = _owner.Settings.Get().Value;
            var locations = _owner.Host.GetLibraries().SelectMany(l => l.Locations).ToList();
            DeletionEnabled = settings.DeletionEnabled;
            Guard = new PathGuard(settings.ApprovedRoots.Where(r => locations.Contains(r, StringComparer.Ordinal)), otherRoots: locations);
            _policyChanged = _policy.Id != Guid.Empty && _owner.Policies.Get(_policy.Id)?.Revision != _policy.Revision;
        }

        public static string? ChangeOf(PlanFile file, FileObservation observed)
        {
            if (!string.Equals(file.Identity, observed.Identity, StringComparison.Ordinal))
            {
                return "FILE_REPLACED";
            }

            if (file.Size != observed.Size)
            {
                return "FILE_SIZE_CHANGED";
            }

            if (file.ModifiedUnixNanos != observed.ModifiedUnixNanos)
            {
                return "FILE_MODIFIED";
            }

            return null;
        }

        public JobProblem? Validate(PlanItem item, IReadOnlyDictionary<(Guid ItemId, Guid UserId), UserItemState> live, ActivePlayback playback)
        {
            JobProblem Problem(string code, string message) => new(item.ItemId, item.Name, code, message);

            if (!DeletionEnabled)
            {
                return Problem(ErrorCodes.DeletionDisabled, "Deletion was disabled after this plan was made.");
            }

            if (_policyChanged)
            {
                return Problem("POLICY_CHANGED", "The policy behind this plan changed or was removed.");
            }

            var record = _owner.Host.GetItem(item.ItemId);
            if (record is null)
            {
                return Problem("ITEM_GONE", "The server no longer has this item.");
            }

            var media = item.Files.Where(f => f.Role != FileRole.Sidecar).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            if (!media.SetEquals(record.Files.Select(f => f.Path)))
            {
                return Problem("ITEM_CHANGED", "The server's list of files for this item changed.");
            }

            foreach (var file in item.Files)
            {
                var observed = _owner.Probe.Probe(file.Path);
                if (file.Role == FileRole.Sidecar && observed.Status == ProbeStatus.NotFound)
                {
                    continue;
                }

                var check = Guard.CheckDeletableFile(file.Path, observed);
                if (!check.IsSafe)
                {
                    return Problem("PATH_" + check.Refusal, CleanupPlanner.Describe(check.Refusal));
                }

                if (ChangeOf(file, observed) is { } change)
                {
                    return Problem(change, "A file changed after this plan was made.");
                }
            }

            var paths = item.Files.Where(f => f.Role != FileRole.Sidecar).Select(f => f.Path).ToList();
            var self = new SharedRecord(item.ItemId, item.Kind, item.Name, item.LibraryId, item.SeriesId, item.SeasonId, item.SeasonNumber, item.EpisodeNumber);
            foreach (var target in item.SharedRecords.Prepend(self))
            {
                if (target.ItemId != item.ItemId)
                {
                    var other = _owner.Host.GetItem(target.ItemId);
                    if (other is null || !media.SetEquals(other.Files.Select(f => f.Path)))
                    {
                        return Problem("ITEM_CHANGED", "Another record sharing these files changed.");
                    }
                }

                var evaluation = PolicyEvaluator.Evaluate(_policy, new EvaluationInput
                {
                    Leaves = LeavesFor(target, paths, item.Files.Where(f => f.Role != FileRole.Sidecar).Sum(f => f.Size)),
                    NowUtc = _owner.Now,
                    KnownUsers = _users.Select(u => u.Id).ToList(),
                    ReadableUsers = _liveUsers,
                    States = live,
                    ProtectedIds = _owner.Policies.ProtectedIds(),
                    Playback = playback,
                    EvaluateUnit = false,
                    PathComparison = PathGuard.PlatformComparison,
                    UserCanAccessLibrary = (user, library) => _users.FirstOrDefault(u => u.Id == user)?.CanAccess(library) ?? false,
                }).Items.GetValueOrDefault(target.ItemId);

                if (evaluation is null)
                {
                    return Problem("NOT_EVALUATED", "This item could not be evaluated.");
                }

                if (!evaluation.Eligible)
                {
                    var first = evaluation.Blocking.First();
                    return Problem(first.RuleId, first.Message);
                }
            }

            return null;
        }

        /// <summary>The item with current paths, plus indexed siblings because keep-newest ranks within a show.</summary>
        private IReadOnlyList<LeafItem> LeavesFor(SharedRecord target, IReadOnlyList<string> paths, long size)
        {
            var live = _owner.Host.GetItem(target.ItemId);
            var self = new LeafItem
            {
                Id = target.ItemId,
                Kind = target.Kind,
                Name = target.Name,
                LibraryId = target.LibraryId,
                SeriesId = target.SeriesId,
                SeasonId = target.SeasonId,
                SeasonNumber = target.SeasonNumber,
                EpisodeNumber = target.EpisodeNumber,
                DateAddedUtc = live?.DateAddedUtc,
                LogicalBytes = size,
                Health = ItemHealth.Ok,
                Paths = paths,
                Tags = live?.Tags ?? [],
            };

            if (target.SeriesId is not { } series || _policy.KeepNewestEpisodesPerShow is null)
            {
                return [self];
            }

            if (!_siblings.TryGetValue(series, out var siblings))
            {
                _siblings[series] = siblings = _owner.Inventory.LeavesOfSeries(series);
            }

            return [self, .. siblings.Where(s => s.Id != target.ItemId)];
        }
    }
}
