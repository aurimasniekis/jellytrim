using Jellytrim.Core.Abstractions;
using Jellytrim.Core.Domain;
using Jellytrim.Core.FileSystem;
using Jellytrim.Core.Persistence;
using Jellytrim.Core.Policies;
using Jellytrim.Core.Services;
using Jellytrim.Core.Storage;
using Microsoft.Extensions.Logging;

namespace Jellytrim.Core.Cleanup;

/// <summary>
/// Turns a selection into an immutable plan listing exact physical targets. Clients supply item ids and filters, never
/// paths: every path comes from the server's item record and the inventory, then passes PathGuard against a live probe.
/// </summary>
public sealed class CleanupPlanner(
    IMediaHost host,
    InventoryStore inventory,
    ExplorerStore explorer,
    PolicyStore policies,
    SettingsStore settings,
    CleanupStore cleanup,
    AuditStore audit,
    PolicyEvaluationService evaluations,
    IFileProbe probe,
    TimeProvider clock,
    ILogger<CleanupPlanner> logger)
{
    public (CleanupPlan Plan, PlanSummary Summary) CreatePlan(PlanRequest request, string? actor, string correlationId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var config = settings.Get().Value;
        if (inventory.LatestScan(successfulOnly: true) is not { } scan)
        {
            throw new JellytrimException(ErrorCodes.NoInventory, "No completed scan exists yet. Run a scan first.", 409);
        }

        var policy = ResolvePolicy(request);
        var libraries = host.GetLibraries();
        var locations = libraries.SelectMany(l => l.Locations).ToList();
        var previewOnly = !config.DeletionEnabled;

        // While read-only, a preview shows what would be targeted across all libraries. Such a plan is marked and can never run.
        var guard = new PathGuard(previewOnly ? locations : config.ApprovedRoots.Where(r => locations.Contains(r, StringComparer.Ordinal)));

        var input = evaluations.LoadInput(host.GetActivePlayback());
        var result = PolicyEvaluator.Evaluate(policy, input);
        var leaves = input.Leaves.ToDictionary(l => l.Id);
        var warnings = new List<string>();
        if (previewOnly)
        {
            warnings.Add("Deletion is disabled, so this is a preview only. Enable deletion and generate a new plan to execute.");
        }

        if (now - (scan.FinishedUtc ?? scan.StartedUtc) > TimeSpan.FromHours(config.StaleScanHours))
        {
            warnings.Add($"The last scan is older than {config.StaleScanHours} hour(s). Eligibility may be out of date; execution re-checks each item.");
        }

        var selected = ResolveSelection(request, policy, input, result, config, warnings);
        var exclusions = new List<PlanExclusion>();
        var eligible = new List<LeafItem>();
        foreach (var id in selected)
        {
            if (!leaves.TryGetValue(id, out var leaf))
            {
                exclusions.Add(new PlanExclusion(id, id.ToString("N"), "NOT_IN_INVENTORY", "This item is not in the inventory. Run a scan.", []));
            }
            else if (!result.Items[id].Eligible)
            {
                var blocking = result.Items[id].Blocking.ToList();
                exclusions.Add(new PlanExclusion(id, leaf.Name, blocking.FirstOrDefault()?.RuleId ?? "RULE", blocking.FirstOrDefault()?.Message ?? "Not eligible.", blocking));
            }
            else
            {
                eligible.Add(leaf);
            }
        }

        if (eligible.Count > config.MaxPlanItems)
        {
            throw new JellytrimException(ErrorCodes.PlanTooLarge, $"This selection has {eligible.Count} eligible items, above the limit of {config.MaxPlanItems}. Narrow it or set a reclaim target.", 409);
        }

        var eligibleIds = eligible.Select(l => l.Id).ToHashSet();
        var items = new List<PlanItem>();
        foreach (var leaf in eligible)
        {
            var built = BuildItem(leaf, result.Items[leaf.Id], eligibleIds, guard, request.IncludeSidecars, out var exclusion);
            if (built is not null)
            {
                items.Add(built);
            }
            else
            {
                exclusions.Add(exclusion!);
            }
        }

        // A sibling that is itself in this plan is not left behind, so it should not be reported as a neighbor.
        var plannedPaths = items.SelectMany(i => i.Files.Select(f => f.Path).Concat(i.DirectoriesIfEmpty)).ToHashSet(StringComparer.Ordinal);
        items = items.Select(i => i with { UnplannedNeighbors = i.UnplannedNeighbors.Where(n => !plannedPaths.Contains(n)).ToList() }).ToList();

        var containers = request.IncludeSidecars ? BuildContainers(items, input, guard) : [];
        var allFiles = items.SelectMany(i => i.Files).Concat(containers.SelectMany(c => c.Files)).ToList();
        var estimate = StorageAccounting.Estimate(allFiles.Select(f => new FileRef(f.Path, f.Identity, f.Size, f.AllocatedBytes, f.LinkCount)));
        var freedByPath = FreedPerFile(allFiles);
        items = items.Select(i => i with { Files = i.Files.Select(f => f with { EstimatedFreedBytes = freedByPath.GetValueOrDefault(f.Path) }).ToList() }).ToList();
        containers = containers.Select(i => i with { Files = i.Files.Select(f => f with { EstimatedFreedBytes = freedByPath.GetValueOrDefault(f.Path) }).ToList() }).ToList();

        if (estimate.HardLinkedRetained > 0)
        {
            warnings.Add($"{estimate.HardLinkedRetained} file(s) have hard links outside this plan. Removing them will not free their space.");
        }

        var highImpact = items.Count >= config.HighImpactItems || estimate.EstimatedReclaimableBytes >= config.HighImpactBytes || containers.Any(c => c.Kind == MediaKind.Series);
        var plan = new CleanupPlan
        {
            Id = Guid.NewGuid(),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(config.PlanTtlMinutes),
            CreatedBy = actor,
            Request = request,
            Policy = policy,
            PreviewOnly = previewOnly,
            ScanGeneration = scan.Generation,
            Items = items,
            Containers = containers,
            Exclusions = exclusions,
            Warnings = warnings,
            Totals = new PlanTotals(items.Count, allFiles.Count, estimate.LogicalBytes, estimate.UniqueBytes, estimate.EstimatedReclaimableBytes, estimate.HardLinkedRetained),
            Volumes = ProjectVolumes(allFiles, freedByPath, config),
            HighImpact = highImpact,
            ConfirmationPhrase = highImpact ? $"DELETE {items.Count} ITEMS" : null,
        };

        var (summary, _) = cleanup.SavePlan(plan);
        audit.Record(now, actor, "plan.created", plan.Id.ToString("N"), correlationId, "ok", Json.Write(new { plan.Totals, exclusions = exclusions.Count, plan.PreviewOnly, source = request.Type }));
        logger.LogInformation("jellytrim plan created {PlanId} {PlanItems} {PlanFiles} {PlanReclaimBytes} {CorrelationId}", plan.Id, items.Count, allFiles.Count, estimate.EstimatedReclaimableBytes, correlationId);
        return (plan, summary);
    }

    private CleanupPolicy ResolvePolicy(PlanRequest request)
    {
        if (request.PolicyId is { } id)
        {
            return policies.Get(id) ?? throw JellytrimException.NotFound("Policy");
        }

        if (request.Type == SelectionType.Policy)
        {
            throw JellytrimException.Validation(["A policy selection needs a policy."]);
        }

        return CleanupPolicy.ManualSelection();
    }

    private IReadOnlyList<Guid> ResolveSelection(PlanRequest request, CleanupPolicy policy, EvaluationInput input, EvaluationResult result, JellytrimSettings config, List<string> warnings)
    {
        switch (request.Type)
        {
            case SelectionType.Items:
                if (request.ItemIds.Count == 0)
                {
                    throw JellytrimException.Validation(["Select at least one item."]);
                }

                if (request.ItemIds.Count > config.MaxPlanItems)
                {
                    throw new JellytrimException(ErrorCodes.PlanTooLarge, $"Select at most {config.MaxPlanItems} items.", 409);
                }

                return explorer.ExpandToLeaves(request.ItemIds);

            case SelectionType.Query:
                if (request.Query is null || request.ExpectedCount is null)
                {
                    throw JellytrimException.Validation(["An all-matching selection needs its query and the count that was shown."]);
                }

                var problems = request.Query.Validate();
                if (problems.Count > 0)
                {
                    throw JellytrimException.Validation(problems);
                }

                // One more than expected is enough to detect growth without unbounded reads.
                var matched = explorer.MatchingIds(request.Query, request.ExpectedCount.Value + 1);
                if (matched.Count != request.ExpectedCount.Value)
                {
                    throw new JellytrimException(ErrorCodes.SelectionChanged, $"The matching results changed since they were shown ({request.ExpectedCount.Value} then, {(matched.Count > request.ExpectedCount.Value ? "more" : matched.Count.ToString())} now). Review them and select again.", 409);
                }

                return explorer.ExpandToLeaves(matched);

            case SelectionType.Policy:
                var ids = result.Items.Values.Where(v => v.Eligible).Select(v => v.ItemId).ToList();
                if (request.TargetBytes is not { } target || target <= 0)
                {
                    return ids.OrderBy(i => i).ToList();
                }

                var users = policy.EffectiveUsers;
                var byId = input.Leaves.ToDictionary(l => l.Id);
                var ranked = RecommendationRanker.Rank(
                    ids.Select(i => new RecommendationCandidate
                    {
                        Item = byId[i],
                        Evaluation = result.Items[i],
                        LastPlayedUtc = users.Select(u => input.States.TryGetValue((i, u), out var s) ? s.LastPlayedUtc : null).Max(),
                    }),
                    target,
                    clock.GetUtcNow().UtcDateTime,
                    config.MaxPlanItems);
                if (!ranked.TargetMet)
                {
                    warnings.Add($"Only {ranked.SelectedBytes} of the requested {target} bytes could be found among eligible items.");
                }

                var picked = ranked.Items.Select(r => r.ItemId).ToHashSet();

                // A complete-unit policy must not be split by the target: pull in the rest of each picked unit.
                if (policy.Unit != CleanupUnit.Item)
                {
                    var groupOf = (LeafItem l) => policy.Unit == CleanupUnit.CompleteSeason ? l.SeasonId : l.SeriesId;
                    var groups = picked.Select(p => groupOf(byId[p])).OfType<Guid>().ToHashSet();
                    picked.UnionWith(ids.Where(i => groupOf(byId[i]) is { } g && groups.Contains(g)));
                }

                return picked.OrderBy(i => i).ToList();

            default:
                throw JellytrimException.Validation(["Unknown selection type."]);
        }
    }

    private PlanItem? BuildItem(LeafItem leaf, ItemEvaluation evaluation, HashSet<Guid> eligibleIds, PathGuard guard, bool includeSidecars, out PlanExclusion? exclusion)
    {
        PlanItem? Refuse(string code, string message, out PlanExclusion? ex)
        {
            ex = new PlanExclusion(leaf.Id, leaf.Name, code, message, evaluation.Rules);
            return null;
        }

        exclusion = null;

        // The inventory is only a cache. The plan must agree with what the server holds right now.
        var live = host.GetItem(leaf.Id);
        if (live is null)
        {
            return Refuse("ITEM_GONE", "The server no longer has this item. Run a scan.", out exclusion);
        }

        var livePaths = live.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var known = inventory.GetItemFiles(leaf.Id);
        if (!livePaths.SetEquals(known.Select(k => k.Path)))
        {
            return Refuse("ITEM_CHANGED", "This item's files changed since the last scan. Run a scan.", out exclusion);
        }

        var files = new List<PlanFile>();
        var shared = new HashSet<Guid>();
        foreach (var media in live.Files)
        {
            var observation = probe.Probe(media.Path);
            var check = guard.CheckDeletableFile(media.Path, observation);
            if (!check.IsSafe)
            {
                return Refuse("PATH_" + check.Refusal, Describe(check.Refusal), out exclusion);
            }

            if (observation.Identity is null)
            {
                return Refuse("IDENTITY_UNKNOWN", "This file's physical identity cannot be verified on this platform or filesystem.", out exclusion);
            }

            foreach (var other in inventory.ItemsReferencing(media.Path).Where(o => o != leaf.Id))
            {
                // Two records may legitimately point at the same path. It may only go if every one of them is going.
                if (!eligibleIds.Contains(other))
                {
                    return Refuse("SHARED_FILE", "This file is also referenced by another item that is not part of this plan.", out exclusion);
                }

                // Exactly the same file set: the lower id owns deletion and reconciles both records. Partial overlap cannot be split safely.
                var otherPaths = inventory.GetItemFiles(other).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
                if (!otherPaths.SetEquals(livePaths))
                {
                    return Refuse("SHARED_FILE", "This file is also referenced by another item with different files, so they cannot be removed together.", out exclusion);
                }

                if (other.CompareTo(leaf.Id) < 0)
                {
                    return Refuse("SAME_FILES_AS_OTHER_ITEM", "Another record in this plan points at the same files and will remove them.", out exclusion);
                }

                shared.Add(other);
            }

            files.Add(ToPlanFile(media.Path, media.Role, observation));
        }

        var directories = new List<string>();
        var neighbors = new List<string>();
        foreach (var group in live.Files.GroupBy(f => Path.GetDirectoryName(f.Path)!, StringComparer.Ordinal))
        {
            directories.Add(group.Key);
            if (!includeSidecars)
            {
                continue;
            }

            try
            {
                var mediaHere = group.Select(g => g.Path).ToList();
                var others = inventory.MediaPathsInDirectory(group.Key).Except(mediaHere, StringComparer.Ordinal).ToList();
                var scan = SidecarDiscovery.Discover(group.Key, mediaHere, others, includeFolderArtwork: leaf.Kind == MediaKind.Movie && others.Count == 0 && !IsLibraryRoot(group.Key, guard));
                neighbors.AddRange(scan.Unplanned);
                foreach (var sidecar in scan.Sidecars)
                {
                    var observation = probe.Probe(sidecar);
                    if (guard.CheckDeletableFile(sidecar, observation).IsSafe && observation.Identity is not null)
                    {
                        files.Add(ToPlanFile(sidecar, FileRole.Sidecar, observation));
                        var parent = Path.GetDirectoryName(sidecar)!;
                        if (!directories.Contains(parent, StringComparer.Ordinal))
                        {
                            directories.Insert(0, parent); // Nested (trickplay) directories come first so that they empty first.
                        }
                    }
                    else
                    {
                        neighbors.Add(sidecar);
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                neighbors.Add(group.Key + Path.DirectorySeparatorChar + "(could not be read)");
            }
        }

        var removable = directories.Where(d => guard.CheckLocation(d) is { IsSafe: true }).OrderByDescending(d => d.Length).ToList();
        return new PlanItem(leaf.Id, leaf.Kind, leaf.Name, live.SeriesName, leaf.SeasonNumber, leaf.EpisodeNumber, leaf.LibraryId, leaf.SeriesId, leaf.SeasonId, live.VersionIds, files, removable, neighbors, evaluation.Rules, shared.ToList());
    }

    private static bool IsLibraryRoot(string directory, PathGuard guard) => guard.CheckLocation(directory).Refusal == PathRefusal.IsRoot;

    private List<PlanContainer> BuildContainers(IReadOnlyList<PlanItem> items, EvaluationInput input, PathGuard guard)
    {
        var inPlan = items.Select(i => i.ItemId).ToHashSet();
        var planned = items.SelectMany(i => i.Files.Select(f => f.Path).Concat(i.DirectoriesIfEmpty)).ToHashSet(StringComparer.Ordinal);
        var episodes = input.Leaves.Where(l => l.Kind == MediaKind.Episode && l.Health != ItemHealth.NotFileBacked).ToList();
        var containers = new List<PlanContainer>();

        void Add(IEnumerable<IGrouping<Guid?, LeafItem>> groups, MediaKind kind)
        {
            foreach (var group in groups)
            {
                if (group.Key is not { } id || !group.All(e => inPlan.Contains(e.Id)))
                {
                    continue;
                }

                var record = host.GetItem(id);
                if (record?.Path is null)
                {
                    continue;
                }

                var files = new List<PlanFile>();
                var directories = new List<string>();
                var neighbors = new List<string>();
                if (guard.CheckLocation(record.Path).IsSafe && Directory.Exists(record.Path))
                {
                    directories.Add(record.Path);
                    try
                    {
                        var scan = SidecarDiscovery.DiscoverContainerArtwork(record.Path);
                        neighbors.AddRange(scan.Unplanned.Where(u => !planned.Contains(u)));
                        foreach (var path in scan.Sidecars)
                        {
                            var observation = probe.Probe(path);
                            if (guard.CheckDeletableFile(path, observation).IsSafe && observation.Identity is not null && !planned.Contains(path))
                            {
                                files.Add(ToPlanFile(path, FileRole.Sidecar, observation));
                            }
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        neighbors.Add(record.Path + Path.DirectorySeparatorChar + "(could not be read)");
                    }
                }

                containers.Add(new PlanContainer(id, kind, record.Name, group.Select(e => e.Id).ToList(), files, directories, neighbors));
            }
        }

        Add(episodes.GroupBy(e => e.SeasonId), MediaKind.Season);
        Add(episodes.GroupBy(e => e.SeriesId), MediaKind.Series);
        return containers;
    }

    private static PlanFile ToPlanFile(string path, FileRole role, FileObservation o) =>
        new(path, role, o.Identity ?? string.Empty, o.VolumeId, o.Size, o.AllocatedBytes, o.LinkCount, o.ModifiedUnixNanos, 0);

    private static Dictionary<string, long> FreedPerFile(IReadOnlyList<PlanFile> files)
    {
        var freed = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var group in files.GroupBy(f => f.Identity, StringComparer.Ordinal))
        {
            var paths = group.Select(g => g.Path).Distinct(StringComparer.Ordinal).ToList();
            var first = group.First();
            freed[paths[0]] = paths.Count >= first.LinkCount ? first.AllocatedBytes : 0;
        }

        return freed;
    }

    private List<VolumeProjection> ProjectVolumes(IReadOnlyList<PlanFile> files, Dictionary<string, long> freed, JellytrimSettings config)
    {
        var known = inventory.ListVolumes().ToDictionary(v => v.VolumeId);
        return files.GroupBy(f => f.VolumeId ?? "?").Select(g =>
        {
            known.TryGetValue(g.Key, out var volume);
            return new VolumeProjection(g.Key, volume?.SamplePath ?? g.First().Path, volume?.FreeBytes, g.Sum(f => freed.GetValueOrDefault(f.Path)), config.VolumeGoals.FirstOrDefault(v => v.VolumeId == g.Key)?.MinimumFreeBytes);
        }).ToList();
    }

    public static string Describe(PathRefusal refusal) => refusal switch
    {
        PathRefusal.Malformed => "The path is not a normalized absolute path.",
        PathRefusal.Traversal => "The path contains traversal segments.",
        PathRefusal.NoApprovedRoot => "No deletion roots are approved. Approve library roots in Settings.",
        PathRefusal.OutsideApprovedRoots => "The file is outside the approved deletion roots.",
        PathRefusal.IsRoot => "The path is an approved root itself.",
        PathRefusal.RootUnavailable => "The library root is unavailable.",
        PathRefusal.SymlinkInPath => "A symlink or junction lies between the root and this file. jellytrim does not follow them.",
        PathRefusal.TargetIsLink => "The target is a symlink, which jellytrim does not delete.",
        PathRefusal.NotARegularFile => "The target is not a regular file.",
        PathRefusal.TargetMissing => "The file is missing.",
        PathRefusal.TargetInaccessible => "The file cannot be accessed.",
        _ => "The path is refused.",
    };
}
