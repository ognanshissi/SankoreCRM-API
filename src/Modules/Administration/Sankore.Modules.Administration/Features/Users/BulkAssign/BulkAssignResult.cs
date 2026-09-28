namespace Sankore.Modules.Administration.Features.Users.BulkAssign;

/// <summary>
/// What happened to one selected user. A bulk action over a grid selection is almost never
/// all-or-nothing in practice: one disabled account in a list of forty should not force the
/// operator to deselect it and start again. Every user therefore gets its own outcome, and the
/// ones that did apply are committed.
/// </summary>
public sealed record BulkUserOutcome(Guid UserId, bool Applied, string? Reason);

public sealed record BulkAssignResult(
    int Requested,
    int Applied,
    int Skipped,
    IReadOnlyList<BulkUserOutcome> Outcomes);

/// <summary>Per-user reasons. The request itself succeeds; these explain each line.</summary>
public static class BulkAssignReasons
{
    public const string UserNotFound = "USER_NOT_FOUND";
    public const string SystemAccountImmutable = "SYSTEM_ACCOUNT_IMMUTABLE";
    public const string AlreadyInAgency = "ALREADY_IN_AGENCY";
    public const string AlreadyHasRole = "ALREADY_HAS_ROLE";

    /// <summary>
    /// Moving the manager of an agency out of it would break the rule that a manager belongs to
    /// the agency they run. The operator has to vacate that post first — silently vacating it
    /// here would be an invisible side effect of a bulk move.
    /// </summary>
    public const string UserManagesAnAgency = "USER_MANAGES_AN_AGENCY";
}

/// <summary>Failures that stop the whole request, because the target is common to every user.</summary>
public static class BulkAssignErrors
{
    public const string AgencyNotFound = "AGENCY_NOT_FOUND";
    public const string AgencyDeleted = "AGENCY_DELETED";
    public const string RoleNotFound = "ROLE_NOT_FOUND";
    public const string RoleNotAssignable = "ROLE_NOT_ASSIGNABLE";
}
