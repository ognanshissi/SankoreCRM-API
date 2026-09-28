namespace Sankore.Modules.Customers.Features.Clients.Shared;

using Sankore.Modules.Administration.PublicApi;

/// <summary>
/// The two facts module M01 needs about the agency tree, which lives in the
/// <c>administration</c> schema and is therefore unreachable by query from here.
///
/// Deliberately narrower than <see cref="IAdministrationModule"/>: the Customers
/// handlers depend on this two-method contract, so a test substitutes it with two
/// lambdas instead of a whole cross-module facade, and a future change to the
/// Administration contract lands in exactly one adapter.
/// </summary>
public interface IAgencyDirectory
{
    /// <summary>
    /// The agency's business code (used as a segment of the client number), or
    /// <c>null</c> when the agency does not exist, was soft-deleted, or is inactive.
    /// Callers translate <c>null</c> into <c>AGENCY_OUT_OF_SCOPE</c>.
    /// </summary>
    Task<string?> GetAgencyCodeAsync(Guid tenantId, Guid agencyId, CancellationToken ct);

    /// <summary>
    /// True when the user may be set as the advisor of a client owned by
    /// <paramref name="agencyId"/> — i.e. the user exists and belongs to that agency.
    /// Callers translate <c>false</c> into <c>ADVISOR_NOT_ELIGIBLE</c>.
    /// </summary>
    Task<bool> IsAdvisorEligibleAsync(
        Guid tenantId, Guid advisorUserId, Guid agencyId, CancellationToken ct);
}

/// <summary>
/// Implements <see cref="IAgencyDirectory"/> over the Administration module's public
/// contract. No reference to Administration's domain, DbContext or schema.
/// </summary>
internal sealed class AdministrationAgencyDirectory(IAdministrationModule administration) : IAgencyDirectory
{
    public async Task<string?> GetAgencyCodeAsync(Guid tenantId, Guid agencyId, CancellationToken ct)
    {
        var agency = await administration.GetAgencyAsync(tenantId, agencyId, ct);

        // An inactive agency is treated exactly like a missing one: a client may not
        // be booked into an agency that has been closed, and the caller must not be
        // able to tell "closed" from "never existed" (that would leak the tree shape).
        return agency is { IsActive: true } ? agency.Code : null;
    }

    public async Task<bool> IsAdvisorEligibleAsync(
        Guid tenantId, Guid advisorUserId, Guid agencyId, CancellationToken ct)
    {
        // GetAgentAsync takes no tenantId — the Administration contract keys agents by
        // their (globally unique) id alone. Tenant isolation therefore rests on the
        // agency comparison below: an agent of another tenant can never match an
        // agency id that this tenant owns.
        var agent = await administration.GetAgentAsync(advisorUserId, ct);

        return agent is not null && agent.AgencyId == agencyId;
    }
}
