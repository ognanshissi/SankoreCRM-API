using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Domain.Matching;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel.Authorization;

namespace Sankore.Modules.Customers.Features.Clients.SearchClients;

/// <summary>
/// Raccourcis de la liste des clients. Chacun est un filtre comme un autre, cumulé avec le reste :
/// il existe pour que l'écran et les compteurs disent exactement la même chose.
/// </summary>
public enum ClientQuickFilter
{
    All,

    /// <summary>Aucun KYC commencé.</summary>
    KycToStart,

    /// <summary>KYC ouvert et pas encore tranché.</summary>
    KycInProgress,

    HighRisk,

    WithoutAdvisor,
}

/// <summary>Les critères de recherche, hors pagination et hors raccourci.</summary>
public sealed record ClientSearchCriteria(
    string? ClientNumber,
    string? Phone,
    string? IdentityDocumentNumber,
    string? Name,
    ClientStatus? Status,
    Guid? AgencyId,
    Guid? AdvisorUserId,
    ClientType? Type,
    string? SegmentCode,
    RiskLevel? RiskLevel = null,
    bool? HasPepExposure = null);

/// <summary>
/// Le filtrage de la recherche, partagé par la liste et ses compteurs. Une seule définition : un
/// compteur « KYC en cours · 23 » qui ne compterait pas comme la liste afficherait 23 et en
/// listerait 21.
/// </summary>
internal static class ClientSearchFilter
{
    /// <summary>Below this length a name prefix matches too much to be useful, so it is ignored.</summary>
    public const int MinimumNameLength = 3;

    public static async Task<IQueryable<Client>> ApplyAsync(
        CustomersDbContext db,
        IQueryable<Client> query,
        ClientSearchCriteria c,
        Guid tenantId,
        Guid actor,
        IAgencyScopeProvider agencyScope,
        IBlindIndexer indexer,
        CancellationToken ct)
    {
        // ── Agency perimeter ─────────────────────────────────────────────────
        // null means UNRESTRICTED (super-user / tenant-wide account) — not "no access".
        var accessibleAgencies = await agencyScope.GetAccessibleAgencyIdsAsync(tenantId, actor, ct);
        if (accessibleAgencies is not null)
            query = query.Where(x => accessibleAgencies.Contains(x.AgencyId));

        // ── Exact identifiers ────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(c.ClientNumber))
        {
            // Client numbers are minted upper-case ({AgencyCode}-{YYYY}-{Seq}), so an exact
            // comparison hits ux_clients_number directly; lowering both sides would defeat it.
            var clientNumber = c.ClientNumber.Trim().ToUpperInvariant();
            query = query.Where(x => x.ClientNumber == clientNumber);
        }

        if (!string.IsNullOrWhiteSpace(c.IdentityDocumentNumber))
        {
            // Equality on the blind index: the encrypted column itself is never read.
            var documentBlindIndex = indexer.Compute(BlindIndexPurpose.IdentityDocument, c.IdentityDocumentNumber);
            query = query.Where(x => x.IdentityDocumentNumberBlindIndex == documentBlindIndex);
        }

        if (!string.IsNullOrWhiteSpace(c.Phone))
        {
            var phoneBlindIndex = indexer.Compute(BlindIndexPurpose.Phone, c.Phone);

            // EXISTS on the child table rather than a join, so a client holding the same
            // number twice is still returned once.
            query = query.Where(x => db.ClientContactPoints.Any(cp =>
                cp.ClientId == x.Id
                && cp.Type == ContactPointType.Phone
                && cp.ValidTo == null
                && cp.BlindIndex == phoneBlindIndex));
        }

        // ── Name prefix ──────────────────────────────────────────────────────
        var normalizedName = SearchKeyBuilder.NormalizeTerm(c.Name);
        if (normalizedName.Length >= MinimumNameLength)
        {
            // SearchKey is "LASTNAME FIRSTNAME" (or "LEGALNAME"), so the LIKE prefix catches
            // a surname and the " " + term containment catches a given name. Both sides go
            // through the SAME normalization as the stored column — otherwise a client whose
            // name carries an accent would be unreachable.
            var prefix = normalizedName + "%";
            var innerTerm = " " + normalizedName;

            query = query.Where(x => EF.Functions.Like(x.SearchKey, prefix) || x.SearchKey.Contains(innerTerm));
        }

        // ── Enumerated and foreign-key filters ───────────────────────────────
        if (c.Status.HasValue)
            query = query.Where(x => x.Status == c.Status.Value);

        if (c.Type.HasValue)
            query = query.Where(x => x.Type == c.Type.Value);

        if (c.AgencyId.HasValue)
            query = query.Where(x => x.AgencyId == c.AgencyId.Value);

        if (c.AdvisorUserId.HasValue)
            query = query.Where(x => x.AdvisorUserId == c.AdvisorUserId.Value);

        if (!string.IsNullOrWhiteSpace(c.SegmentCode))
        {
            var segmentCode = c.SegmentCode.Trim();
            query = query.Where(x => x.SegmentCode == segmentCode);
        }

        if (c.RiskLevel.HasValue)
            query = query.Where(x => x.RiskLevel == c.RiskLevel.Value);

        if (c.HasPepExposure.HasValue)
            query = query.Where(x => x.HasPepExposure == c.HasPepExposure.Value);

        return query;
    }

    public static IQueryable<Client> Quick(IQueryable<Client> query, ClientQuickFilter quick) => quick switch
    {
        ClientQuickFilter.KycToStart => query.Where(x => x.KycStatus == Kyc.PublicApi.KycStatus.NotStarted),
        ClientQuickFilter.KycInProgress => query.Where(x =>
            x.KycStatus == Kyc.PublicApi.KycStatus.Pending || x.KycStatus == Kyc.PublicApi.KycStatus.InProgress),
        ClientQuickFilter.HighRisk => query.Where(x => x.RiskLevel == RiskLevel.High),
        // Un client archivé ou fusionné n'a plus à être suivi : il n'est pas « sans conseiller ».
        ClientQuickFilter.WithoutAdvisor => query.Where(x => x.AdvisorUserId == null
            && x.Status != ClientStatus.Archived && x.Status != ClientStatus.Merged),
        _ => query,
    };
}
