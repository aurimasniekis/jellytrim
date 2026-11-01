using System.Net.Mime;
using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Jellytrim.Runtime;
using Jellytrim.Core.Abstractions;
using Jellytrim.Core.Cleanup;
using Jellytrim.Core.Domain;
using Jellytrim.Core.Persistence;
using Jellytrim.Core.Policies;
using Jellytrim.Core.Services;
using Jellytrim.Core.Storage;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellytrim.Api;

/// <summary>
/// The administrator API. The class-level policy applies to every action, and a test fails if any action opts out.
/// Authentication is by the Authorization header token, never ambient cookies, so cross-site requests carry no credentials.
/// </summary>
[Route("Jellytrim")]
[Authorize(Policy = Policies.RequiresElevation)]
[TypeFilter(typeof(ApiErrorFilter))]
[Produces(MediaTypeNames.Application.Json)]
public sealed partial class JellytrimController(
    JellytrimRuntime runtime,
    IMediaHost host,
    InventoryStore inventory,
    ExplorerStore explorer,
    PolicyStore policies,
    SettingsStore settings,
    CleanupStore cleanup,
    AuditStore audit,
    PolicyEvaluationService evaluations,
    CleanupPlanner planner,
    CleanupCoordinator coordinator,
    JellytrimDb db,
    TimeProvider clock) : ControllerBase
{
    private const int MaxPage = 200;

    /// <summary>Set by Jellyfin's authentication handler, see Jellyfin.Api/Constants/InternalClaimTypes.cs.</summary>
    private const string IsApiKeyClaim = "Jellyfin-IsApiKey";

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private string? Actor => User.Identity?.Name is { Length: > 0 } name ? name : "api-key";

    private string CorrelationId => ApiErrorFilter.CorrelationIdOf(HttpContext);

    private static JsonResult Ok2(object? value, int status = 200) => new(value, Json.Options) { StatusCode = status };

    [HttpGet("Status")]
    [ProducesResponseType<StatusDto>(StatusCodes.Status200OK)]
    public IActionResult GetStatus()
    {
        var config = settings.Get().Value;
        return Ok2(new StatusDto(
            typeof(JellytrimController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "unknown",
            "12.4",
            db.SchemaVersion(),
            config.DeletionEnabled,
            config.ApprovedRoots.Count,
            ScanStatus(config),
            cleanup.ActiveJob()?.Id,
            Now));
    }

    [HttpGet("Overview")]
    [ProducesResponseType<OverviewDto>(StatusCodes.Status200OK)]
    public IActionResult GetOverview([FromQuery] Guid[]? userIds, [FromQuery] Guid? policyId)
    {
        var config = settings.Get().Value;
        var budgets = config.LibraryBudgets.ToDictionary(b => b.LibraryId, b => b.BudgetBytes);
        var libraries = inventory.ListLibraries()
            .Select(l => new LibraryDto(l, budgets.TryGetValue(l.Id, out var budget) ? Budgets.Evaluate(l.Id, budget, l.UniqueBytes) : null))
            .ToList();
        var goals = config.VolumeGoals.ToDictionary(g => g.VolumeId, g => g.MinimumFreeBytes);
        var volumes = inventory.ListVolumes().Select(v =>
        {
            long? goal = goals.TryGetValue(v.VolumeId, out var g) ? g : null;
            var below = goal is not null && v.FreeBytes is { } free && free < goal;
            return new VolumeDto(v, goal, below, below ? goal!.Value - v.FreeBytes!.Value : 0);
        }).ToList();

        var warnings = new List<WarningDto>();
        foreach (var l in libraries)
        {
            if (!l.Library.Online)
            {
                warnings.Add(new WarningDto("error", "LIBRARY_OFFLINE", $"Library '{l.Library.Name}' has unavailable storage. Its figures are from the last successful observation. {l.Library.OfflineDetail}"));
            }

            if (l.Budget is { Exceeded: true } budget)
            {
                warnings.Add(new WarningDto("warning", "BUDGET_EXCEEDED", $"Library '{l.Library.Name}' is over its budget by {budget.OverBytes} bytes. Budgets are advisory and never trigger deletion."));
            }
        }

        foreach (var v in volumes)
        {
            if (!v.Volume.Available)
            {
                warnings.Add(new WarningDto("error", "VOLUME_UNAVAILABLE", $"Free space could not be measured for {v.Volume.SamplePath}."));
            }

            if (v.BelowGoal)
            {
                warnings.Add(new WarningDto("warning", "VOLUME_BELOW_GOAL", $"{v.Volume.SamplePath} is {v.ShortfallBytes} bytes below its free-space goal."));
            }
        }

        var scan = ScanStatus(config);
        foreach (var warning in scan.LastSuccessful?.Warnings ?? [])
        {
            warnings.Add(new WarningDto("warning", "SCAN_WARNING", warning));
        }

        if (scan.Latest is { Status: "Failed" or "Interrupted" } failed)
        {
            warnings.Add(new WarningDto("error", "SCAN_FAILED", failed.Message ?? "The last scan did not finish."));
        }

        PolicyRun? potential = null;
        var stale = false;
        if (policyId is { } id)
        {
            var policy = policies.Get(id) ?? throw JellytrimException.NotFound("Policy");
            potential = policies.GetRun(id) ?? evaluations.Evaluate(policy);
            stale = IsStale(policy, potential);
        }

        var selected = (userIds ?? []).Take(ExplorerQuery.MaxUsers).ToList();
        return Ok2(new OverviewDto(libraries, volumes, new OverviewLargest(Top(MediaKind.Movie), Top(MediaKind.Series), Top(MediaKind.Season), Top(MediaKind.Episode), inventory.LargestFiles(10)), inventory.WatchSummaries(selected), potential, stale, warnings, scan, Now));
    }

    [HttpGet("Trends")]
    [ProducesResponseType<TrendsDto>(StatusCodes.Status200OK)]
    public IActionResult GetTrends([FromQuery] int days = 90)
    {
        var since = Now.AddDays(-Math.Clamp(days, 1, 3650));
        var (libraries, volumes) = inventory.ReadSnapshots(since);
        return Ok2(new TrendsDto(libraries, volumes, since));
    }

    [HttpGet("Users")]
    [ProducesResponseType<IReadOnlyList<UserDto>>(StatusCodes.Status200OK)]
    public IActionResult GetUsers()
    {
        var known = inventory.ListUsers().ToDictionary(u => u.Id);
        return Ok2(host.GetUsers().Select(u => new UserDto(u.Id, u.Name, u.IsAdministrator, u.IsDisabled, known.TryGetValue(u.Id, out var k) && k.ReadableGeneration > 0)).ToList());
    }

    [HttpPost("Items/Query")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType<ExplorerPage>(StatusCodes.Status200OK)]
    public IActionResult QueryItems([FromBody] ExplorerQuery query)
    {
        var problems = query.Validate();
        if (problems.Count > 0)
        {
            throw JellytrimException.Validation(problems);
        }

        return Ok2(explorer.Query(query));
    }

    [HttpGet("Items/{itemId}")]
    [ProducesResponseType<ItemDetailDto>(StatusCodes.Status200OK)]
    public IActionResult GetItem([FromRoute] Guid itemId, [FromQuery] Guid? policyId, [FromQuery] Guid[]? userIds)
    {
        var row = explorer.GetRow(itemId, policyId) ?? throw JellytrimException.NotFound("Item");
        CleanupPolicy? policy = null;
        if (policyId is { } id)
        {
            policy = policies.Get(id) ?? throw JellytrimException.NotFound("Policy");
        }

        var evaluation = policy is null ? null : evaluations.Explain(policy, itemId, host.GetActivePlayback());
        var protectedIds = new[] { row.Id, row.SeasonId, row.SeriesId, row.LibraryId }.OfType<Guid>().ToHashSet();
        var protections = policies.ListProtections().Where(p => protectedIds.Contains(p.TargetId)).ToList();
        return Ok2(new ItemDetailDto(row, inventory.GetItemFiles(itemId), inventory.GetItemStates(itemId), protections, evaluation, policyId, policy is null ? null : Now));
    }

    [HttpGet("Policies")]
    [ProducesResponseType<IReadOnlyList<PolicyDto>>(StatusCodes.Status200OK)]
    public IActionResult ListPolicies() => Ok2(policies.List().Select(ToPolicyDto).ToList());

    [HttpPost("Policies")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType<PolicyDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreatePolicy([FromBody] CleanupPolicy policy)
    {
        var problems = policy.Validate();
        if (problems.Count > 0)
        {
            throw JellytrimException.Validation(problems);
        }

        var created = policies.Create(policy, Now);
        audit.Record(Now, Actor, "policy.created", created.Id.ToString("N"), CorrelationId, "ok", created.Name);
        await EvaluateInBackground(created).ConfigureAwait(false);
        return Ok2(ToPolicyDto(created), StatusCodes.Status201Created);
    }

    [HttpPut("Policies/{policyId}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType<PolicyDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdatePolicy([FromRoute] Guid policyId, [FromBody] PolicyUpdateRequest request)
    {
        request.Policy.Id = policyId;
        var problems = request.Policy.Validate();
        if (problems.Count > 0)
        {
            throw JellytrimException.Validation(problems);
        }

        var updated = policies.Update(request.Policy, request.ExpectedRevision, Now);
        audit.Record(Now, Actor, "policy.updated", policyId.ToString("N"), CorrelationId, "ok", updated.Name);
        await EvaluateInBackground(updated).ConfigureAwait(false);
        return Ok2(ToPolicyDto(updated));
    }

    [HttpDelete("Policies/{policyId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult DeletePolicy([FromRoute] Guid policyId)
    {
        if (!policies.Delete(policyId))
        {
            throw JellytrimException.NotFound("Policy");
        }

        audit.Record(Now, Actor, "policy.deleted", policyId.ToString("N"), CorrelationId, "ok");
        return NoContent();
    }

    [HttpPost("Policies/{policyId}/Evaluate")]
    [ProducesResponseType<PolicyDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> EvaluatePolicy([FromRoute] Guid policyId)
    {
        var policy = policies.Get(policyId) ?? throw JellytrimException.NotFound("Policy");
        await EvaluateInBackground(policy).ConfigureAwait(false);
        return Ok2(ToPolicyDto(policy));
    }

    [HttpPost("Recommendations")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType<RecommendationSet>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Recommend([FromBody] RecommendationRequest request)
    {
        if (request.TargetBytes <= 0)
        {
            throw JellytrimException.Validation(["Enter an amount of space to reclaim."]);
        }

        var policy = policies.Get(request.PolicyId) ?? throw JellytrimException.NotFound("Policy");
        var playback = host.GetActivePlayback();
        var set = await Task.Run(() => evaluations.Recommend(policy, request.TargetBytes, playback)).ConfigureAwait(false);
        return Ok2(set);
    }

    [HttpGet("Protections")]
    [ProducesResponseType<IReadOnlyList<Protection>>(StatusCodes.Status200OK)]
    public IActionResult ListProtections() => Ok2(policies.ListProtections());

    [HttpPost("Protections")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType<Protection>(StatusCodes.Status201Created)]
    public async Task<IActionResult> AddProtection([FromBody] ProtectionRequest request)
    {
        if (request.Note?.Length > 500)
        {
            throw JellytrimException.Validation(["The note is too long."]);
        }

        string name;
        if (request.TargetType == ProtectionTarget.Library)
        {
            name = host.GetLibraries().FirstOrDefault(l => l.Id == request.TargetId)?.Name ?? throw JellytrimException.NotFound("Library");
        }
        else
        {
            name = host.GetItem(request.TargetId)?.Name ?? throw JellytrimException.NotFound("Item");
        }

        var protection = new Protection(request.TargetId, request.TargetType, name, request.Note, Now, Actor);
        policies.AddProtection(protection);
        audit.Record(Now, Actor, "protection.added", request.TargetId.ToString("N"), CorrelationId, "ok", name);
        await ReevaluateAll().ConfigureAwait(false);
        return Ok2(protection, StatusCodes.Status201Created);
    }

    [HttpDelete("Protections/{targetId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveProtection([FromRoute] Guid targetId)
    {
        if (!policies.RemoveProtection(targetId))
        {
            throw JellytrimException.NotFound("Protection");
        }

        audit.Record(Now, Actor, "protection.removed", targetId.ToString("N"), CorrelationId, "ok");
        await ReevaluateAll().ConfigureAwait(false);
        return NoContent();
    }

    [HttpGet("Settings")]
    [ProducesResponseType<SettingsDto>(StatusCodes.Status200OK)]
    public IActionResult GetSettings()
    {
        var current = settings.Get();
        return Ok2(new SettingsDto(current.Value, current.Revision, host.GetLibraries().SelectMany(l => l.Locations).Distinct().ToList(), JellytrimSettings.EnablePhrase));
    }

    [HttpPut("Settings")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType<SettingsDto>(StatusCodes.Status200OK)]
    public IActionResult UpdateSettings([FromBody] SettingsUpdateRequest request)
    {
        RefuseApiKey();
        var next = request.Settings;
        var problems = next.Validate();
        if (problems.Count > 0)
        {
            throw JellytrimException.Validation(problems);
        }

        var locations = host.GetLibraries().SelectMany(l => l.Locations).ToHashSet(StringComparer.Ordinal);
        var unknown = next.ApprovedRoots.Where(r => !locations.Contains(r)).ToList();
        if (unknown.Count > 0)
        {
            throw new JellytrimException(ErrorCodes.RootNotLibraryLocation, "Approved roots must be current library locations.", 400, unknown);
        }

        var current = settings.Get().Value;
        var enabling = next.DeletionEnabled && !current.DeletionEnabled;
        if (enabling && !string.Equals(request.EnableConfirmation, JellytrimSettings.EnablePhrase, StringComparison.Ordinal))
        {
            throw new JellytrimException(ErrorCodes.ConfirmationRequired, $"To enable deletion, type exactly: {JellytrimSettings.EnablePhrase}", 400);
        }

        if (next.DeletionEnabled && next.ApprovedRoots.Count == 0)
        {
            throw JellytrimException.Validation(["Approve at least one library root before enabling deletion."]);
        }

        next.DeletionEnabledAtUtc = enabling ? Now : current.DeletionEnabledAtUtc;
        next.DeletionEnabledBy = enabling ? Actor : current.DeletionEnabledBy;
        var revision = settings.Save(next, request.ExpectedRevision, Now);
        if (next.DeletionEnabled != current.DeletionEnabled)
        {
            audit.Record(Now, Actor, next.DeletionEnabled ? "deletion.enabled" : "deletion.disabled", null, CorrelationId, "ok", Json.Write(next.ApprovedRoots));
        }

        if (!next.ApprovedRoots.SequenceEqual(current.ApprovedRoots))
        {
            audit.Record(Now, Actor, "roots.changed", null, CorrelationId, "ok", Json.Write(next.ApprovedRoots));
        }

        audit.Record(Now, Actor, "settings.updated", null, CorrelationId, "ok");
        return Ok2(new SettingsDto(next, revision, locations.ToList(), JellytrimSettings.EnablePhrase));
    }

    [HttpPost("Scans")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult StartScan()
    {
        runtime.StartScan("manual");
        audit.Record(Now, Actor, "scan.requested", null, CorrelationId, "ok");
        return Ok2(ScanStatus(settings.Get().Value), StatusCodes.Status202Accepted);
    }

    [HttpGet("Scans/Current")]
    [ProducesResponseType<ScanStatusDto>(StatusCodes.Status200OK)]
    public IActionResult GetScan() => Ok2(ScanStatus(settings.Get().Value));

    [HttpPost("Plans")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType<PlanDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreatePlan([FromBody] PlanRequest request)
    {
        var actor = Actor;
        var correlation = CorrelationId;
        var (plan, summary) = await Task.Run(() => planner.CreatePlan(request, actor, correlation)).ConfigureAwait(false);
        return Ok2(ToPlanDto(summary, plan, 0, 50, 0, 50), StatusCodes.Status201Created);
    }

    [HttpGet("Plans/{planId}")]
    [ProducesResponseType<PlanDto>(StatusCodes.Status200OK)]
    public IActionResult GetPlan([FromRoute] Guid planId, [FromQuery] int itemOffset = 0, [FromQuery] int itemLimit = 50, [FromQuery] int exclusionOffset = 0, [FromQuery] int exclusionLimit = 50)
    {
        CheckPage(itemOffset, itemLimit);
        CheckPage(exclusionOffset, exclusionLimit);
        var summary = cleanup.GetPlanSummary(planId) ?? throw JellytrimException.NotFound("Plan");
        var plan = Json.Read<CleanupPlan>(cleanup.GetPlanDocument(planId)!);
        return Ok2(ToPlanDto(summary, plan, itemOffset, itemLimit, exclusionOffset, exclusionLimit));
    }

    [HttpPost("Plans/{planId}/Execute")]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType<JobRow>(StatusCodes.Status202Accepted)]
    public IActionResult ExecutePlan([FromRoute] Guid planId, [FromBody] ExecuteRequest request)
    {
        RefuseApiKey();
        var job = coordinator.Start(planId, request, Actor, CorrelationId);
        return Ok2(job, StatusCodes.Status202Accepted);
    }

    [HttpGet("Jobs")]
    [ProducesResponseType<PageResponse<JobRow>>(StatusCodes.Status200OK)]
    public IActionResult ListJobs([FromQuery] int offset = 0, [FromQuery] int limit = 25)
    {
        CheckPage(offset, limit);
        var (rows, total) = cleanup.ListJobs(offset, limit);
        return Ok2(new PageResponse<JobRow>(rows, total, offset, limit));
    }

    [HttpGet("Jobs/{jobId}")]
    [ProducesResponseType<JobDetailDto>(StatusCodes.Status200OK)]
    public IActionResult GetJob([FromRoute] Guid jobId, [FromQuery] int offset = 0, [FromQuery] int limit = 50, [FromQuery] string? status = null)
    {
        CheckPage(offset, limit);
        if (status is not null && !StatusPattern().IsMatch(status))
        {
            throw JellytrimException.Validation(["Unknown status filter."]);
        }

        var job = cleanup.GetJob(jobId) ?? throw JellytrimException.NotFound("Job");
        var (rows, total) = cleanup.ListJobItems(jobId, offset, limit, status);
        return Ok2(new JobDetailDto(job, new PageResponse<JobItemRow>(rows, total, offset, limit)));
    }

    [HttpPost("Jobs/{jobId}/Cancel")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult CancelJob([FromRoute] Guid jobId)
    {
        coordinator.RequestCancel(jobId, Actor, CorrelationId);
        return Ok2(cleanup.GetJob(jobId), StatusCodes.Status202Accepted);
    }

    [HttpGet("Audit")]
    [ProducesResponseType<PageResponse<AuditEntry>>(StatusCodes.Status200OK)]
    public IActionResult GetAudit([FromQuery] int offset = 0, [FromQuery] int limit = 50)
    {
        CheckPage(offset, limit);
        var (rows, total) = audit.List(offset, limit);
        return Ok2(new PageResponse<AuditEntry>(rows, total, offset, limit));
    }

    /// <summary>
    /// API keys carry the administrator role in Jellyfin and are long-lived. Destructive authority needs an interactive
    /// administrator, so a leaked automation key can read but cannot turn deletion on or run a plan.
    /// </summary>
    private void RefuseApiKey()
    {
        if (string.Equals(User.FindFirstValue(IsApiKeyClaim), bool.TrueString, StringComparison.OrdinalIgnoreCase))
        {
            throw new JellytrimException("API_KEY_NOT_ALLOWED", "This action requires a signed-in administrator; API keys cannot perform it.", 403);
        }
    }

    private static void CheckPage(int offset, int limit)
    {
        if (offset < 0 || limit < 1 || limit > MaxPage)
        {
            throw JellytrimException.Validation([$"Offset must not be negative, and limit must be between 1 and {MaxPage}."]);
        }
    }

    private IReadOnlyList<ExplorerRow> Top(MediaKind kind) => explorer.Query(new ExplorerQuery { Kinds = [kind], Limit = 10 }).Rows;

    private ScanStatusDto ScanStatus(JellytrimSettings config)
    {
        var last = inventory.LatestScan(successfulOnly: true);
        var stale = last?.FinishedUtc is null || Now - last.FinishedUtc.Value > TimeSpan.FromHours(config.StaleScanHours);
        return new ScanStatusDto(runtime.ScanRunning, runtime.ScanItemsSeen, inventory.LatestScan(), last, stale);
    }

    private bool IsStale(CleanupPolicy policy, PolicyRun run) =>
        run.Revision != policy.Revision || run.ScanGeneration != inventory.CurrentGeneration() || run.ProtectionRevision != policies.ProtectionRevision();

    private PolicyDto ToPolicyDto(CleanupPolicy policy)
    {
        var run = policies.GetRun(policy.Id);
        var known = host.GetUsers().Select(u => u.Id).ToHashSet();
        var missing = policy.ParticipatingUserIds.Where(u => !known.Contains(u)).Select(u => u.ToString("N")).ToList();
        return new PolicyDto(policy, run, run is null || IsStale(policy, run), missing);
    }

    private Task EvaluateInBackground(CleanupPolicy policy) => Task.Run(() => evaluations.Evaluate(policy));

    private Task ReevaluateAll() => Task.Run(evaluations.EvaluateAll);

    private static PlanDto ToPlanDto(PlanSummary summary, CleanupPlan plan, int itemOffset, int itemLimit, int exclusionOffset, int exclusionLimit) => new(
        summary,
        new CleanupPlanHeader(plan.Id, plan.CreatedAtUtc, plan.ExpiresAtUtc, plan.CreatedBy, plan.Policy.Name, plan.PreviewOnly, plan.Containers, plan.Warnings, plan.Totals, plan.Volumes, plan.HighImpact, plan.ConfirmationPhrase),
        new PageResponse<PlanItem>(plan.Items.Skip(itemOffset).Take(itemLimit).ToList(), plan.Items.Count, itemOffset, itemLimit),
        new PageResponse<PlanExclusion>(plan.Exclusions.Skip(exclusionOffset).Take(exclusionLimit).ToList(), plan.Exclusions.Count, exclusionOffset, exclusionLimit));

    [GeneratedRegex("^[A-Za-z]{1,20}$")]
    private static partial Regex StatusPattern();
}

/// <summary>Turns failures into the stable error shape, without leaking internals, and gates on database availability.</summary>
public sealed class ApiErrorFilter(JellytrimRuntime runtime, ILogger<ApiErrorFilter> logger) : IAsyncActionFilter, IExceptionFilter
{
    private const string Key = "jellytrim.correlation";

    public static string CorrelationIdOf(HttpContext context)
    {
        if (context.Items[Key] is string existing)
        {
            return existing;
        }

        var supplied = context.Request.Headers["X-Correlation-Id"].ToString();
        var id = supplied.Length is > 0 and <= 64 && supplied.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? supplied : Guid.NewGuid().ToString("N");
        context.Items[Key] = id;
        return id;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        context.HttpContext.Response.Headers["X-Correlation-Id"] = CorrelationIdOf(context.HttpContext);
        if (runtime.StartupError is { } error)
        {
            context.Result = Render(context.HttpContext, 503, ErrorCodes.DatabaseUnavailable, error, null);
            return;
        }

        if (!context.ModelState.IsValid)
        {
            var problems = context.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).Where(m => m.Length > 0).Distinct().Take(10).ToList();
            context.Result = Render(context.HttpContext, 400, ErrorCodes.ValidationFailed, "The request is not valid.", problems);
            return;
        }

        foreach (var (name, value) in context.ActionArguments)
        {
            if (value is null && context.ActionDescriptor.Parameters.FirstOrDefault(p => p.Name == name)?.BindingInfo?.BindingSource == Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource.Body)
            {
                context.Result = Render(context.HttpContext, 400, ErrorCodes.ValidationFailed, "A request body is required.", null);
                return;
            }
        }

        if (context.ActionDescriptor.Parameters.Any(p => p.BindingInfo?.BindingSource == Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource.Body && !context.ActionArguments.ContainsKey(p.Name)))
        {
            context.Result = Render(context.HttpContext, 400, ErrorCodes.ValidationFailed, "A request body is required.", null);
            return;
        }

        await next().ConfigureAwait(false);
    }

    public void OnException(ExceptionContext context)
    {
        switch (context.Exception)
        {
            case JellytrimException e:
                context.Result = Render(context.HttpContext, e.Status, e.Code, e.Message, e.Details);
                break;
            case ConcurrencyConflictException e:
                context.Result = Render(context.HttpContext, 409, ErrorCodes.RevisionConflict, $"{e.Message} Reload and try again.", null);
                break;
            case KeyNotFoundException e:
                context.Result = Render(context.HttpContext, 404, ErrorCodes.NotFound, e.Message, null);
                break;
            default:
                var id = CorrelationIdOf(context.HttpContext);
                logger.LogError(context.Exception, "jellytrim API request failed {CorrelationId}", id);
                context.Result = Render(context.HttpContext, 500, "INTERNAL_ERROR", "Something went wrong. See the server log for this correlation id.", null);
                break;
        }

        context.ExceptionHandled = true;
    }

    private static JsonResult Render(HttpContext http, int status, string code, string message, object? details) =>
        new(new ErrorResponse(code, message, CorrelationIdOf(http), details), Json.Options) { StatusCode = status };
}
