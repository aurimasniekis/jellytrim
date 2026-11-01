// Mirrors the API contract in src/Jellyfin.Plugin.Jellytrim/Api. Dates are ISO strings in UTC.
export type MediaKind = 'Movie' | 'Series' | 'Season' | 'Episode';
export type WatchState = 'Unplayed' | 'Played' | 'Partial' | 'Unknown';
export type ItemHealth = 'Ok' | 'Missing' | 'Unavailable' | 'NotFileBacked' | 'Container';
export type FileRole = 'Main' | 'Part' | 'AlternateVersion' | 'Sidecar';
export type RuleOutcome = 'Pass' | 'Block' | 'Unknown' | 'NotApplicable';
export type RuleKind = 'Criterion' | 'Protection' | 'Availability';
export type UserMatch = 'All' | 'Any';
export type ExplorerSort = 'Size' | 'Name' | 'DateAdded' | 'LastPlayed' | 'Reclaimable';

export interface ScanRow { id: number; generation: number; trigger: string; status: string; startedUtc: string; finishedUtc: string | null; itemsSeen: number; filesSeen: number; message: string | null; warnings: string[] }
export interface ScanStatus { running: boolean; itemsSeen: number; latest: ScanRow | null; lastSuccessful: ScanRow | null; stale: boolean }
export interface Status { version: string; jellyfinTarget: string; schemaVersion: number; deletionEnabled: boolean; approvedRootCount: number; scan: ScanStatus; activeJobId: string | null; serverTimeUtc: string }

export interface LibraryRow { id: string; name: string; collectionType: string | null; locations: string[]; online: boolean; offlineDetail: string | null; lastScanUtc: string | null; itemCount: number; fileCount: number; logicalBytes: number; uniqueBytes: number; allocatedBytes: number; reclaimableBytes: number }
export interface BudgetStatus { libraryId: string; budgetBytes: number; usedBytes: number; usedFraction: number; exceeded: boolean; overBytes: number }
export interface LibraryDto { library: LibraryRow; budget: BudgetStatus | null }
export interface VolumeRow { volumeId: string; samplePath: string; totalBytes: number | null; freeBytes: number | null; observedUtc: string | null; available: boolean; libraryIds: string[] }
export interface VolumeDto { volume: VolumeRow; minimumFreeBytes: number | null; belowGoal: boolean; shortfallBytes: number }
export interface LargestFile { path: string; size: number; allocatedBytes: number; linkCount: number; itemId: string; itemName: string; kind: MediaKind }
export interface WatchSummary { libraryId: string; items: number; playedByAllBytes: number; playedByAllItems: number; playedBySomeBytes: number; playedBySomeItems: number; unplayedBytes: number; unplayedItems: number; unknownBytes: number; unknownItems: number }
export interface PolicyRun { policyId: string; revision: number; scanGeneration: number; protectionRevision: number; evaluatedAtUtc: string; eligibleCount: number; consideredCount: number; eligibleLogicalBytes: number; eligibleReclaimableBytes: number; blockedByRule: Record<string, number> }
export interface Warning { severity: 'error' | 'warning'; code: string; message: string }
export interface Overview { libraries: LibraryDto[]; volumes: VolumeDto[]; largest: { movies: ExplorerRow[]; shows: ExplorerRow[]; seasons: ExplorerRow[]; episodes: ExplorerRow[]; files: LargestFile[] }; watchSummaries: WatchSummary[]; potential: PolicyRun | null; potentialStale: boolean; warnings: Warning[]; scan: ScanStatus; generatedAtUtc: string }
export interface SnapshotPoint { takenUtc: string; scopeId: string; scopeName: string; logicalBytes: number; uniqueBytes: number; allocatedBytes: number; freeBytes: number | null; totalBytes: number | null }
export interface Trends { libraries: SnapshotPoint[]; volumes: SnapshotPoint[]; sinceUtc: string }

export interface UserDto { id: string; name: string; isAdministrator: boolean; isDisabled: boolean; stateReadable: boolean }
export interface ExplorerUserState { userId: string; state: WatchState; positionTicks: number; playCount: number; lastPlayedUtc: string | null; isFavorite: boolean }
export interface ExplorerRow { id: string; kind: MediaKind; name: string; seriesName: string | null; seasonNumber: number | null; episodeNumber: number | null; libraryId: string; libraryName: string | null; seriesId: string | null; seasonId: string | null; logicalBytes: number; uniqueBytes: number; reclaimableBytes: number; fileCount: number; versionCount: number; dateAddedUtc: string | null; lastPlayedUtc: string | null; runtimeTicks: number | null; health: ItemHealth; protected: boolean; eligible: boolean | null; eligibleDescendants: number | null; totalDescendants: number | null; children: number | null; path: string | null; userStates: ExplorerUserState[] }
export interface ExplorerPage { rows: ExplorerRow[]; total: number; offset: number; limit: number }
export interface ExplorerQuery { libraryIds: string[]; kinds: MediaKind[]; parentId: string | null; search: string | null; minBytes: number | null; maxBytes: number | null; userIds: string[]; userMatch: UserMatch; watchState: WatchState | null; lastPlayedBeforeUtc: string | null; lastPlayedAfterUtc: string | null; addedBeforeUtc: string | null; addedAfterUtc: string | null; protected: boolean | null; policyId: string | null; eligible: boolean | null; sort: ExplorerSort; descending: boolean; offset: number; limit: number }

export interface ItemFileRow { path: string; role: FileRole; versionId: string; identity: string | null; volumeId: string | null; size: number; allocated: number; linkCount: number; mtimeNs: number; status: string; observedUtc: string; otherItemReferences: number }
export interface ItemStateRow { userId: string; userName: string; state: WatchState; positionTicks: number; playCount: number; lastPlayedUtc: string | null; isFavorite: boolean }
export interface Protection { targetId: string; targetType: 'Item' | 'Library'; name: string; note: string | null; createdAtUtc: string; createdBy: string | null }
export interface RuleEvaluation { ruleId: string; kind: RuleKind; outcome: RuleOutcome; message: string }
export interface ItemEvaluation { itemId: string; eligible: boolean; rules: RuleEvaluation[] }
export interface ItemDetail { item: ExplorerRow; files: ItemFileRow[]; userStates: ItemStateRow[]; protections: Protection[]; evaluation: ItemEvaluation | null; policyId: string | null; policyEvaluatedAtUtc: string | null }

export type WatchedRequirement = 'AllSelected' | 'AnySelected';
export type UserProtectionScope = 'None' | 'SelectedUsers' | 'AllKnownUsers';
export type CleanupUnit = 'Item' | 'CompleteSeason' | 'CompleteShow';
export interface CleanupPolicy { id: string; name: string; revision: number; participatingUserIds: string[]; excludedUserIds: string[]; watched: WatchedRequirement; minDaysSinceLastWatched: number | null; minDaysSinceAdded: number | null; minSizeBytes: number | null; unit: CleanupUnit; mediaKinds: MediaKind[]; includedLibraryIds: string[]; excludedLibraryIds: string[]; excludedPathPrefixes: string[]; excludedTags: string[]; favoriteProtection: UserProtectionScope; inProgressProtection: UserProtectionScope; keepAddedWithinDays: number | null; keepNewestEpisodesPerShow: number | null }
export interface PolicyDto { policy: CleanupPolicy; run: PolicyRun | null; runStale: boolean; unavailableUsers: string[] }
export interface Recommendation { rank: number; itemId: string; name: string; reclaimableBytes: number; cumulativeBytes: number; lastPlayedUtc: string | null; dateAddedUtc: string | null; reason: string }
export interface RecommendationSet { requestedBytes: number; selectedBytes: number; targetMet: boolean; items: Recommendation[]; eligibleConsidered: number; skippedNoReclaim: number }

export interface PlanFile { path: string; role: FileRole; identity: string; volumeId: string | null; size: number; allocatedBytes: number; linkCount: number; modifiedUnixNanos: number; estimatedFreedBytes: number }
export interface PlanItem { itemId: string; kind: MediaKind; name: string; seriesName: string | null; seasonNumber: number | null; episodeNumber: number | null; files: PlanFile[]; directoriesIfEmpty: string[]; unplannedNeighbors: string[]; reasons: RuleEvaluation[]; sharedRecords: { itemId: string }[] }
export interface PlanContainer { itemId: string; kind: MediaKind; name: string; childItemIds: string[]; files: PlanFile[]; directoriesIfEmpty: string[]; unplannedNeighbors: string[] }
export interface PlanExclusion { itemId: string; name: string; code: string; message: string; rules: RuleEvaluation[] }
export interface PlanTotals { items: number; files: number; logicalBytes: number; uniqueBytes: number; estimatedReclaimableBytes: number; hardLinkedRetained: number }
export interface VolumeProjection { volumeId: string; samplePath: string; freeBytesNow: number | null; estimatedFreed: number; minimumFreeGoal: number | null }
export interface PlanHeader { id: string; createdAtUtc: string; expiresAtUtc: string; createdBy: string | null; policyName: string; previewOnly: boolean; containers: PlanContainer[]; warnings: string[]; totals: PlanTotals; volumes: VolumeProjection[]; highImpact: boolean; confirmationPhrase: string | null }
export interface PlanSummary { id: string; status: string; hash: string; expiresAtUtc: string }
export interface Paged<T> { items: T[]; total: number; offset: number; limit: number }
export interface PlanDto { summary: PlanSummary; plan: PlanHeader; items: Paged<PlanItem>; exclusions: Paged<PlanExclusion> }

export interface JobProblem { itemId: string; name: string; code: string; message: string }
export interface VolumeFree { volumeId: string; samplePath: string; freeBytes: number | null }
export interface JobRow { id: string; planId: string; correlationId: string; status: string; createdBy: string | null; createdAtUtc: string; startedAtUtc: string | null; finishedAtUtc: string | null; cancelRequested: boolean; totalItems: number; completedItems: number; deletedBytes: number; reclaimEstimateBytes: number; message: string | null; freeBefore: VolumeFree[] | null; freeAfter: VolumeFree[] | null; problems: JobProblem[] | null }
export interface JobFile { fileSeq: number; path: string; role: FileRole; status: string; code: string | null; bytes: number; atUtc: string | null }
export interface JobItem { seq: number; itemId: string; name: string; status: string; code: string | null; message: string | null; startedUtc: string | null; finishedUtc: string | null; freedBytes: number; reconcile: string | null; files: JobFile[] }
export interface JobDetail { job: JobRow; items: Paged<JobItem> }
export interface AuditEntry { id: number; atUtc: string; actor: string | null; action: string; subject: string | null; correlationId: string | null; outcome: string; details: string | null }

export interface Settings { deletionEnabled: boolean; deletionEnabledAtUtc: string | null; deletionEnabledBy: string | null; approvedRoots: string[]; libraryBudgets: { libraryId: string; budgetBytes: number }[]; volumeGoals: { volumeId: string; minimumFreeBytes: number }[]; planTtlMinutes: number; highImpactBytes: number; highImpactItems: number; maxPlanItems: number; staleScanHours: number; snapshotRetentionDays: number; auditRetentionDays: number }
export interface SettingsDto { settings: Settings; revision: number; libraryRoots: string[]; enablePhrase: string }

export interface PlanRequest { type: 'Items' | 'Query' | 'Policy'; itemIds?: string[]; query?: ExplorerQuery; expectedCount?: number; policyId?: string | null; targetBytes?: number | null; includeSidecars: boolean }
