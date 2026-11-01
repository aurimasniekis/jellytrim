namespace Jellytrim.Core.Services;

/// <summary>Stable error codes returned by the API. Clients may switch on these; do not rename them.</summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string NotFound = "NOT_FOUND";
    public const string RevisionConflict = "REVISION_CONFLICT";
    public const string ScanRunning = "SCAN_RUNNING";
    public const string CleanupRunning = "CLEANUP_RUNNING";
    public const string NoInventory = "NO_INVENTORY";
    public const string DeletionDisabled = "DELETION_DISABLED";
    public const string ConfirmationRequired = "CONFIRMATION_REQUIRED";
    public const string SelectionChanged = "SELECTION_CHANGED";
    public const string PlanTooLarge = "PLAN_TOO_LARGE";
    public const string PlanEmpty = "PLAN_EMPTY";
    public const string PlanExpired = "PLAN_EXPIRED";
    public const string PlanHashMismatch = "PLAN_HASH_MISMATCH";
    public const string PlanPreviewOnly = "PLAN_PREVIEW_ONLY";
    public const string PlanAlreadyExecuted = "PLAN_ALREADY_EXECUTED";
    public const string PlanInvalid = "PLAN_INVALID";
    public const string JobActive = "JOB_ALREADY_ACTIVE";
    public const string JobNotActive = "JOB_NOT_ACTIVE";
    public const string RootNotLibraryLocation = "ROOT_NOT_LIBRARY_LOCATION";
    public const string DatabaseUnavailable = "DATABASE_UNAVAILABLE";
}

public sealed class JellytrimException(string code, string message, int status, object? details = null) : Exception(message)
{
    public string Code { get; } = code;

    public int Status { get; } = status;

    public object? Details { get; } = details;

    public static JellytrimException Validation(IReadOnlyList<string> problems) =>
        new(ErrorCodes.ValidationFailed, string.Join(" ", problems), 400, problems);

    public static JellytrimException NotFound(string what) => new(ErrorCodes.NotFound, $"{what} was not found.", 404);
}
