using Jellytrim.Core.Abstractions;
using Jellytrim.Core.Cleanup;
using Jellytrim.Core.Domain;
using Jellytrim.Core.Persistence;
using Jellytrim.Core.Policies;
using Jellytrim.Core.Storage;

namespace Jellyfin.Plugin.Jellytrim.Api;

public sealed record ErrorResponse(string Code, string Message, string CorrelationId, object? Details = null);

public sealed record PageResponse<T>(IReadOnlyList<T> Items, long Total, int Offset, int Limit);

public sealed record ScanStatusDto(bool Running, int ItemsSeen, ScanRow? Latest, ScanRow? LastSuccessful, bool Stale);

public sealed record StatusDto(string Version, string JellyfinTarget, int SchemaVersion, bool DeletionEnabled, int ApprovedRootCount, ScanStatusDto Scan, Guid? ActiveJobId, DateTime ServerTimeUtc);

public sealed record LibraryDto(LibraryRow Library, BudgetStatus? Budget);

public sealed record VolumeDto(VolumeRow Volume, long? MinimumFreeBytes, bool BelowGoal, long ShortfallBytes);

public sealed record OverviewLargest(IReadOnlyList<ExplorerRow> Movies, IReadOnlyList<ExplorerRow> Shows, IReadOnlyList<ExplorerRow> Seasons, IReadOnlyList<ExplorerRow> Episodes, IReadOnlyList<LargestFile> Files);

public sealed record WarningDto(string Severity, string Code, string Message);

public sealed record OverviewDto(
    IReadOnlyList<LibraryDto> Libraries,
    IReadOnlyList<VolumeDto> Volumes,
    OverviewLargest Largest,
    IReadOnlyList<WatchSummary> WatchSummaries,
    PolicyRun? Potential,
    bool PotentialStale,
    IReadOnlyList<WarningDto> Warnings,
    ScanStatusDto Scan,
    DateTime GeneratedAtUtc);

public sealed record TrendsDto(IReadOnlyList<SnapshotPoint> Libraries, IReadOnlyList<SnapshotPoint> Volumes, DateTime SinceUtc);

public sealed record ItemDetailDto(ExplorerRow Item, IReadOnlyList<ItemFileRow> Files, IReadOnlyList<ItemStateRow> UserStates, IReadOnlyList<Protection> Protections, ItemEvaluation? Evaluation, Guid? PolicyId, DateTime? PolicyEvaluatedAtUtc);

public sealed record UserDto(Guid Id, string Name, bool IsAdministrator, bool IsDisabled, bool StateReadable);

public sealed record PolicyDto(CleanupPolicy Policy, PolicyRun? Run, bool RunStale, IReadOnlyList<string> UnavailableUsers);

public sealed record PolicyUpdateRequest(CleanupPolicy Policy, int ExpectedRevision);

public sealed record ProtectionRequest(Guid TargetId, ProtectionTarget TargetType, string? Note);

public sealed record RecommendationRequest(Guid PolicyId, long TargetBytes);

public sealed record SettingsDto(JellytrimSettings Settings, int Revision, IReadOnlyList<string> LibraryRoots, string EnablePhrase);

public sealed record SettingsUpdateRequest(JellytrimSettings Settings, int ExpectedRevision, string? EnableConfirmation);

public sealed record PlanDto(PlanSummary Summary, CleanupPlanHeader Plan, PageResponse<PlanItem> Items, PageResponse<PlanExclusion> Exclusions);

/// <summary>A plan without its potentially large item lists, which are paged.</summary>
public sealed record CleanupPlanHeader(
    Guid Id,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    string? CreatedBy,
    string PolicyName,
    bool PreviewOnly,
    IReadOnlyList<PlanContainer> Containers,
    IReadOnlyList<string> Warnings,
    PlanTotals Totals,
    IReadOnlyList<VolumeProjection> Volumes,
    bool HighImpact,
    string? ConfirmationPhrase);

public sealed record JobDetailDto(JobRow Job, PageResponse<JobItemRow> Items);
