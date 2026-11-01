namespace Jellytrim.Core.Domain;

public sealed class VolumeGoal
{
    public string VolumeId { get; set; } = string.Empty;

    public long MinimumFreeBytes { get; set; }
}

public sealed class LibraryBudget
{
    public Guid LibraryId { get; set; }

    public long BudgetBytes { get; set; }
}

public sealed class JellytrimSettings
{
    public const string EnablePhrase = "ENABLE PERMANENT DELETION";

    /// <summary>Read-only is the default. Only an explicit, confirmed administrator action turns this on.</summary>
    public bool DeletionEnabled { get; set; }

    public DateTime? DeletionEnabledAtUtc { get; set; }

    public string? DeletionEnabledBy { get; set; }

    /// <summary>Must be a subset of the server's current library locations. Nothing can be deleted outside these.</summary>
    public List<string> ApprovedRoots { get; set; } = [];

    public List<LibraryBudget> LibraryBudgets { get; set; } = [];

    public List<VolumeGoal> VolumeGoals { get; set; } = [];

    public int PlanTtlMinutes { get; set; } = 30;

    public long HighImpactBytes { get; set; } = 100L * 1024 * 1024 * 1024;

    public int HighImpactItems { get; set; } = 25;

    public int MaxPlanItems { get; set; } = 5000;

    public int StaleScanHours { get; set; } = 24;

    public int SnapshotRetentionDays { get; set; } = 730;

    public int AuditRetentionDays { get; set; } = 1095;

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (PlanTtlMinutes is < 1 or > 1440)
        {
            problems.Add("Plan lifetime must be between 1 and 1440 minutes.");
        }

        if (HighImpactBytes < 0 || HighImpactItems < 1)
        {
            problems.Add("High-impact thresholds must be positive.");
        }

        if (MaxPlanItems is < 1 or > 20000)
        {
            problems.Add("Maximum plan items must be between 1 and 20000.");
        }

        if (StaleScanHours is < 1 or > 8760)
        {
            problems.Add("Stale-scan threshold must be between 1 and 8760 hours.");
        }

        if (SnapshotRetentionDays is < 7 or > 36500 || AuditRetentionDays is < 30 or > 36500)
        {
            problems.Add("Retention is out of range: snapshots need at least 7 days and audit at least 30.");
        }

        if (LibraryBudgets.Any(b => b.BudgetBytes <= 0) || VolumeGoals.Any(g => g.MinimumFreeBytes <= 0))
        {
            problems.Add("Budgets and free-space goals must be positive; remove an entry to clear it.");
        }

        return problems;
    }
}

public sealed record Versioned<T>(T Value, int Revision);
