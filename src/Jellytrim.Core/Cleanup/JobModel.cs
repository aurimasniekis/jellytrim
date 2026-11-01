namespace Jellytrim.Core.Cleanup;

public static class JobStatus
{
    public const string Queued = "Queued";
    public const string Preflight = "Preflight";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string CompletedWithIssues = "CompletedWithIssues";
    public const string Cancelled = "Cancelled";
    public const string Rejected = "Rejected";
    public const string Failed = "Failed";

    /// <summary>The server stopped during this job. It is final and is never resumed automatically.</summary>
    public const string Interrupted = "Interrupted";

    private static readonly Dictionary<string, string[]> Transitions = new()
    {
        [Queued] = [Preflight, Failed, Interrupted, Cancelled],
        [Preflight] = [Running, Rejected, Failed, Interrupted, Cancelled],
        [Running] = [Completed, CompletedWithIssues, Cancelled, Failed, Interrupted],
    };

    public static bool IsActive(string status) => status is Queued or Preflight or Running;

    public static bool CanMove(string from, string to) => Transitions.TryGetValue(from, out var next) && next.Contains(to);
}

public static class ItemStatus
{
    public const string Pending = "Pending";
    public const string InProgress = "InProgress";
    public const string Deleted = "Deleted";
    public const string Failed = "Failed";

    /// <summary>Revalidation refused this item at the moment of deletion; nothing was touched.</summary>
    public const string Skipped = "Skipped";

    public const string Cancelled = "Cancelled";

    /// <summary>The job ended before reaching this item.</summary>
    public const string NotRun = "NotRun";

    /// <summary>A crash happened mid-item. What happened to its files is reported from a re-check at recovery.</summary>
    public const string Uncertain = "Uncertain";

    private static readonly Dictionary<string, string[]> Transitions = new()
    {
        [Pending] = [InProgress, Skipped, Cancelled, NotRun],
        [InProgress] = [Deleted, Failed, Uncertain],
    };

    public static bool CanMove(string from, string to) => Transitions.TryGetValue(from, out var next) && next.Contains(to);
}

public static class FileStatus
{
    public const string Pending = "Pending";
    public const string Deleting = "Deleting";
    public const string Deleted = "Deleted";
    public const string AlreadyGone = "AlreadyGone";
    public const string Refused = "Refused";
    public const string Failed = "Failed";
    public const string NotAttempted = "NotAttempted";
    public const string Uncertain = "Uncertain";
}

public sealed record JobProblem(Guid ItemId, string Name, string Code, string Message);

public sealed record VolumeFree(string VolumeId, string SamplePath, long? FreeBytes);

public sealed class JobRow
{
    public required Guid Id { get; init; }

    public required Guid PlanId { get; init; }

    public required string CorrelationId { get; init; }

    public required string Status { get; init; }

    public string? CreatedBy { get; init; }

    public required DateTime CreatedAtUtc { get; init; }

    public DateTime? StartedAtUtc { get; init; }

    public DateTime? FinishedAtUtc { get; init; }

    public bool CancelRequested { get; init; }

    public int TotalItems { get; init; }

    public int CompletedItems { get; init; }

    public long DeletedBytes { get; init; }

    public long ReclaimEstimateBytes { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<VolumeFree>? FreeBefore { get; init; }

    public IReadOnlyList<VolumeFree>? FreeAfter { get; init; }

    public IReadOnlyList<JobProblem>? Problems { get; init; }
}

public sealed record JobItemRow(int Seq, Guid ItemId, string Name, string Status, string? Code, string? Message, DateTime? StartedUtc, DateTime? FinishedUtc, long FreedBytes, string? Reconcile, IReadOnlyList<JobFileRow> Files);

public sealed record JobFileRow(int FileSeq, string Path, Domain.FileRole Role, string Status, string? Code, long Bytes, DateTime? AtUtc);
