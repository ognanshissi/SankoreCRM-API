namespace Sankore.Modules.Administration.Features.Agencies.AssignAgencyManager;

/// <summary>
/// Stable error codes for agency-manager assignment.
///
/// The older slices in this folder return prose ("Agency {id} not found.") and their endpoints
/// match on <c>Contains("not found")</c>, which breaks the moment a message is reworded or
/// localized. New work in the codebase (module M01) returns codes, so this slice does too;
/// aligning the older Agencies slices is a separate, mechanical change.
/// </summary>
public static class AgencyManagerErrors
{
    public const string AgencyNotFound = "AGENCY_NOT_FOUND";
    public const string AgencyDeleted = "AGENCY_DELETED";
    public const string ManagerNotFound = "MANAGER_NOT_FOUND";
    public const string ManagerNotActive = "MANAGER_NOT_ACTIVE";
    public const string ManagerNotInAgency = "MANAGER_NOT_IN_AGENCY";

    /// <summary>The BranchManager role is absent from the tenant — RoleSeeder has not run.</summary>
    public const string ManagerRoleMissing = "MANAGER_ROLE_MISSING";
}
