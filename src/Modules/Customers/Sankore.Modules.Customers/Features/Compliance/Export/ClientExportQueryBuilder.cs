namespace Sankore.Modules.Customers.Features.Compliance.Export;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.SearchClients;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// Replays a <see cref="SearchClientsQuery"/> as an EF query for the export job.
///
/// ── Why the job does not simply re-send the MediatR query ─────────────────────
/// The job runs as SYSTEM (that is the only identity a Hangfire worker has), and the search
/// handler resolves the agency perimeter from the CURRENT user. Sending the query from the job
/// would therefore hand the requester a tenant-wide export — a privilege escalation, silent and
/// permanent, through a CSV. So the perimeter is resolved for
/// <c>ClientExportJob.RequestedBy</c> and passed in explicitly, and the filters are rebuilt here.
///
/// ── Searching without decrypting ─────────────────────────────────────────────
/// Phone and identity-document filters are equality tests on the blind index, exactly as the
/// interactive search does: the export never decrypts anything in order to FIND a client, only
/// to mask the two protected columns of the rows it has already selected.
/// </summary>
internal static class ClientExportQueryBuilder
{
    /// <summary>
    /// Hard ceiling on one export. A filterless export of a 100 000-client tenant would build a
    /// CSV nobody asked for and pin a Hangfire worker for minutes; the requester is told to
    /// narrow the filters instead.
    /// </summary>
    internal const int MaxRows = 50_000;

    /// <summary>Minimum length of the free-text name filter, as in the interactive search.</summary>
    private const int MinNameLength = 3;

    internal static IQueryable<Client> Build(
        CustomersDbContext db,
        Guid tenantId,
        SearchClientsQuery filters,
        IReadOnlySet<Guid>? accessibleAgencyIds,
        IBlindIndexer indexer)
    {
        // IgnoreQueryFilters + explicit TenantId: the job runs outside an HTTP request, so the
        // ambient tenant must never be the thing this query relies on.
        var query = db.Clients
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(filters.ClientNumber))
        {
            var clientNumber = filters.ClientNumber.Trim();
            query = query.Where(c => c.ClientNumber == clientNumber);
        }

        if (!string.IsNullOrWhiteSpace(filters.IdentityDocumentNumber))
        {
            var docIndex = indexer.Compute(
                BlindIndexPurpose.IdentityDocument,
                SensitiveValueNormalizer.NormalizeDocumentNumber(filters.IdentityDocumentNumber));

            query = query.Where(c => c.IdentityDocumentNumberBlindIndex == docIndex);
        }

        if (!string.IsNullOrWhiteSpace(filters.Phone))
        {
            var phoneIndex = indexer.Compute(
                BlindIndexPurpose.Phone,
                SensitiveValueNormalizer.NormalizePhone(filters.Phone));

            query = query.Where(c => db.ClientContactPoints
                .IgnoreQueryFilters()
                .Any(cp => cp.TenantId == tenantId
                           && cp.ClientId == c.Id
                           && cp.Type == ContactPointType.Phone
                           && cp.ValidTo == null
                           && cp.BlindIndex == phoneIndex));
        }

        if (!string.IsNullOrWhiteSpace(filters.Name) && filters.Name.Trim().Length >= MinNameLength)
        {
            // Accent- and case-insensitive containment on the denormalized display name. The
            // interactive endpoint owns the index-backed prefix search; an export runs once, in
            // the background, so correctness matters more than the plan here.
            var name = SensitiveValueNormalizer.RemoveDiacritics(filters.Name.Trim()).ToUpperInvariant();
            query = query.Where(c => c.DisplayName.ToUpper().Contains(name));
        }

        if (filters.Status.HasValue)
            query = query.Where(c => c.Status == filters.Status.Value);

        if (filters.Type.HasValue)
            query = query.Where(c => c.Type == filters.Type.Value);

        if (filters.AgencyId.HasValue)
            query = query.Where(c => c.AgencyId == filters.AgencyId.Value);

        if (filters.AdvisorUserId.HasValue)
            query = query.Where(c => c.AdvisorUserId == filters.AdvisorUserId.Value);

        if (!string.IsNullOrWhiteSpace(filters.SegmentCode))
        {
            var segment = filters.SegmentCode.Trim();
            query = query.Where(c => c.SegmentCode == segment);
        }

        // The requester's perimeter, applied last and never skipped: null means unrestricted
        // (super-user), an empty set means the requester sees nothing — which correctly produces
        // an empty export rather than the whole tenant.
        if (accessibleAgencyIds is not null)
        {
            var accessible = accessibleAgencyIds.ToList();
            query = query.Where(c => accessible.Contains(c.AgencyId));
        }

        return query.OrderBy(c => c.DisplayName).ThenBy(c => c.ClientNumber);
    }
}
