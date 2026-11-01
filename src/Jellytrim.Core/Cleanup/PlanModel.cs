using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Jellytrim.Core.Domain;
using Jellytrim.Core.Persistence;
using Jellytrim.Core.Policies;

namespace Jellytrim.Core.Cleanup;

[JsonConverter(typeof(JsonStringEnumConverter<SelectionType>))]
public enum SelectionType
{
    /// <summary>Explicit ids. Shows and seasons expand to their episodes, which are evaluated individually.</summary>
    Items,

    /// <summary>"All matching": re-resolves the query and refuses if the count differs from what the admin saw.</summary>
    Query,

    /// <summary>All items eligible under a policy, optionally ranked and limited to a target amount.</summary>
    Policy,
}

public sealed class PlanRequest
{
    public SelectionType Type { get; set; }

    public List<Guid> ItemIds { get; set; } = [];

    public ExplorerQuery? Query { get; set; }

    public int? ExpectedCount { get; set; }

    public Guid? PolicyId { get; set; }

    public long? TargetBytes { get; set; }

    public bool IncludeSidecars { get; set; } = true;
}

/// <summary>One exact physical target with the observation it must still match at execution.</summary>
public sealed record PlanFile(string Path, FileRole Role, string Identity, string? VolumeId, long Size, long AllocatedBytes, int LinkCount, long ModifiedUnixNanos, long EstimatedFreedBytes);

public sealed record PlanItem(
    Guid ItemId,
    MediaKind Kind,
    string Name,
    string? SeriesName,
    int? SeasonNumber,
    int? EpisodeNumber,
    Guid LibraryId,
    Guid? SeriesId,
    Guid? SeasonId,
    IReadOnlyList<Guid> VersionIds,
    IReadOnlyList<PlanFile> Files,
    IReadOnlyList<string> DirectoriesIfEmpty,
    IReadOnlyList<string> UnplannedNeighbors,
    IReadOnlyList<RuleEvaluation> Reasons,
    IReadOnlyList<Guid> SharedRecordIds);

/// <summary>A season or show whose every file-backed episode is in the plan, so its folder artwork can go too.</summary>
public sealed record PlanContainer(
    Guid ItemId,
    MediaKind Kind,
    string Name,
    IReadOnlyList<Guid> ChildItemIds,
    IReadOnlyList<PlanFile> Files,
    IReadOnlyList<string> DirectoriesIfEmpty,
    IReadOnlyList<string> UnplannedNeighbors);

public sealed record PlanExclusion(Guid ItemId, string Name, string Code, string Message, IReadOnlyList<RuleEvaluation> Rules);

public sealed record PlanTotals(int Items, int Files, long LogicalBytes, long UniqueBytes, long EstimatedReclaimableBytes, int HardLinkedRetained);

public sealed record VolumeProjection(string VolumeId, string SamplePath, long? FreeBytesNow, long EstimatedFreed, long? MinimumFreeGoal);

public sealed class CleanupPlan
{
    public required Guid Id { get; init; }

    public required DateTime CreatedAtUtc { get; init; }

    public required DateTime ExpiresAtUtc { get; init; }

    public string? CreatedBy { get; init; }

    public required PlanRequest Request { get; init; }

    /// <summary>The exact policy evaluated, embedded so that execution re-checks the same rules.</summary>
    public required CleanupPolicy Policy { get; init; }

    /// <summary>True when generated while deletion was off. Such a plan can never execute, even if deletion is enabled later.</summary>
    public required bool PreviewOnly { get; init; }

    public required long ScanGeneration { get; init; }

    public required IReadOnlyList<PlanItem> Items { get; init; }

    public required IReadOnlyList<PlanContainer> Containers { get; init; }

    public required IReadOnlyList<PlanExclusion> Exclusions { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public required PlanTotals Totals { get; init; }

    public required IReadOnlyList<VolumeProjection> Volumes { get; init; }

    public required bool HighImpact { get; init; }

    /// <summary>The exact text to type for a high-impact plan. Null when the standard confirmation is enough.</summary>
    public string? ConfirmationPhrase { get; init; }

    public static string ComputeHash(string document) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document))).ToLowerInvariant();
}

public sealed record PlanSummary(Guid Id, DateTime CreatedAtUtc, DateTime ExpiresAtUtc, string? CreatedBy, string Status, string Hash, int ItemCount, int FileCount, long ReclaimBytes);
