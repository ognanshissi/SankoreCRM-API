namespace Sankore.Modules.Kyc.Features.Files;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Whether the CALLER may act on one KYC file, by id.
///
/// <para>
/// It exists because three agent-facing slices had each grown their own copy of the same twelve
/// lines — and one of them, the one that mails a single-use collection link, had been written
/// without them. That is the failure this service is the answer to: a perimeter check that every
/// slice re-implements is a perimeter check one slice will be missing, and the missing one is
/// invisible in review precisely because its neighbours look right.
/// </para>
///
/// <para>
/// <b>It is for the single-file-by-id case only.</b> <c>GetKycFileHandler</c> and
/// <c>ListKycFilesHandler</c> apply the perimeter as a SQL PREDICATE instead, and that is not an
/// oversight: a list has to filter before it counts, or <c>TotalCount</c> and the page boundaries
/// are computed over rows the caller may not see. This service answers a boolean about one row and
/// must not be used to post-filter a page.
/// </para>
///
/// <para>
/// <b>Deliberately not used by the SYSTEM path.</b> A conversion issues a collection link under
/// the SYSTEM account, and the ambient <c>ICurrentUser</c> in that context is the agent who
/// converted the lead — so scoping it here would silently refuse a link whenever a head-office
/// user converts a lead for a branch customer, which is exactly the shape
/// <c>KycFile.AgencyId</c> documents as ordinary. The two authorities differ, and they are kept in
/// two commands rather than behind a flag on one.
/// </para>
/// </summary>
internal sealed class KycFileAccess(
    KycDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    KycVisibility? visibility = null)
{
    /// <summary>
    /// True when the caller may act on <paramref name="kycFileId"/>: it exists in their tenant and
    /// falls inside their agency perimeter.
    ///
    /// <para>
    /// A single boolean for "does not exist" and "not yours", because every caller must answer both
    /// with <c>KYC_FILE_NOT_FOUND</c>. Telling them apart is what a 403 does, and a 403 confirms
    /// that another branch's file exists — the one thing the perimeter is there to hide.
    /// </para>
    ///
    /// <para>
    /// A file whose <c>AgencyId</c> is null is visible only to an unrestricted caller. Failing
    /// closed: an agency M01 could not resolve must not become everyone's.
    /// </para>
    /// </summary>
    internal async Task<bool> CanActOnAsync(Guid kycFileId, CancellationToken ct)
    {
        // The tenant comes from the context's global query filter, which is correct here: every
        // caller of this service is inside an HTTP request.
        // Assigned-only first: an agent acts on the files they are responsible for.
        var agencyId = await (visibility?.Apply(db.KycFiles) ?? db.KycFiles)
            .Where(f => f.Id == kycFileId)
            .Select(f => new { f.AgencyId })
            .FirstOrDefaultAsync(ct);

        if (agencyId is null)
            return false;

        var perimeter = await agencyScope.GetAccessibleAgencyIdsAsync(
            currentUser.TenantId, currentUser.Id, ct);

        // null means unrestricted — a super-user.
        if (perimeter is null)
            return true;

        return agencyId.AgencyId is { } agency && perimeter.Contains(agency);
    }
}
