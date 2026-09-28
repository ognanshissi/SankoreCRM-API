namespace Sankore.Modules.Customers.Features.Clients.SearchClients;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Domain.Matching;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

/// <summary>
/// US-M01-BE-12.
///
/// Performance is a requirement of this slice (p95 &lt; 300 ms at 100 000 clients), and the
/// way it is met is structural rather than incidental:
/// <list type="bullet">
/// <item>every criterion maps onto an index — <c>ux_clients_number</c>,
/// <c>ux_clients_identity_doc</c>, <c>(TenantId, SearchKey)</c>,
/// <c>(TenantId, Status | AgencyId | AdvisorUserId | SegmentCode)</c>;</item>
/// <item>nothing is materialised before the final <c>Skip/Take</c>: the count and the page
/// are two server-side queries, never a <c>ToList()</c> followed by in-memory filtering;</item>
/// <item>no protected column is decrypted to FILTER. Phone and document searches compare
/// blind indexes. The only decryption happens after paging, on the primary phone of the
/// rows actually returned — bounded by <c>PageSize</c>, never by the table size.</item>
/// </list>
/// </summary>
internal sealed class SearchClientsHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IFieldEncryptor encryptor,
    IBlindIndexer indexer
) : IRequestHandler<SearchClientsQuery, Result<PagedResult<ClientSearchItemDto>>>
{
    /// <summary>Below this length a name prefix matches too much to be useful, so it is ignored.</summary>
    private const int MinimumNameLength = 3;

    private const int MaxPageSize = 200;

    public async Task<Result<PagedResult<ClientSearchItemDto>>> Handle(
        SearchClientsQuery request, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;
        var actor = currentUser.Id;

        var query = db.Clients.AsQueryable();

        // ── Agency perimeter ─────────────────────────────────────────────────
        // null means UNRESTRICTED (super-user / tenant-wide account) — not "no access".
        var accessibleAgencies = await agencyScope.GetAccessibleAgencyIdsAsync(tenantId, actor, ct);
        if (accessibleAgencies is not null)
            query = query.Where(c => accessibleAgencies.Contains(c.AgencyId));

        // ── Exact identifiers ────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(request.ClientNumber))
        {
            // Client numbers are minted upper-case ({AgencyCode}-{YYYY}-{Seq}), so an exact
            // comparison hits ux_clients_number directly; lowering both sides would defeat it.
            var clientNumber = request.ClientNumber.Trim().ToUpperInvariant();
            query = query.Where(c => c.ClientNumber == clientNumber);
        }

        if (!string.IsNullOrWhiteSpace(request.IdentityDocumentNumber))
        {
            // Equality on the blind index: the encrypted column itself is never read.
            var documentBlindIndex = indexer.Compute(
                BlindIndexPurpose.IdentityDocument, request.IdentityDocumentNumber);

            query = query.Where(c => c.IdentityDocumentNumberBlindIndex == documentBlindIndex);
        }

        if (!string.IsNullOrWhiteSpace(request.Phone))
        {
            var phoneBlindIndex = indexer.Compute(BlindIndexPurpose.Phone, request.Phone);

            // EXISTS on the child table rather than a join, so a client holding the same
            // number twice is still returned once.
            query = query.Where(c => db.ClientContactPoints.Any(cp =>
                cp.ClientId == c.Id
                && cp.Type == ContactPointType.Phone
                && cp.ValidTo == null
                && cp.BlindIndex == phoneBlindIndex));
        }

        // ── Name prefix ──────────────────────────────────────────────────────
        var normalizedName = SearchKeyBuilder.NormalizeTerm(request.Name);
        if (normalizedName.Length >= MinimumNameLength)
        {
            // SearchKey is "LASTNAME FIRSTNAME" (or "LEGALNAME"), so the LIKE prefix catches
            // a surname and the " " + term containment catches a given name. Both sides go
            // through the SAME normalization as the stored column — otherwise a client whose
            // name carries an accent would be unreachable.
            var prefix = normalizedName + "%";
            var innerTerm = " " + normalizedName;

            query = query.Where(c =>
                EF.Functions.Like(c.SearchKey, prefix)
                || c.SearchKey.Contains(innerTerm));
        }

        // ── Enumerated and foreign-key filters ───────────────────────────────
        if (request.Status.HasValue)
            query = query.Where(c => c.Status == request.Status.Value);

        if (request.Type.HasValue)
            query = query.Where(c => c.Type == request.Type.Value);

        if (request.AgencyId.HasValue)
            query = query.Where(c => c.AgencyId == request.AgencyId.Value);

        if (request.AdvisorUserId.HasValue)
            query = query.Where(c => c.AdvisorUserId == request.AdvisorUserId.Value);

        if (!string.IsNullOrWhiteSpace(request.SegmentCode))
        {
            var segmentCode = request.SegmentCode.Trim();
            query = query.Where(c => c.SegmentCode == segmentCode);
        }

        var totalCount = await query.CountAsync(ct);

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize <= 0 ? 20 : request.PageSize, 1, MaxPageSize);

        // Projected server-side, including the primary phone's CIPHERTEXT: the row set that
        // crosses the wire is exactly the page, and the ciphertext is decrypted below.
        var rows = await query
            .OrderBy(c => c.DisplayName)
            .ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new SearchRow(
                c.Id,
                c.ClientNumber,
                c.DisplayName,
                c.Type,
                c.Status,
                c.AgencyId,
                c.AdvisorUserId,
                c.KycStatus,
                c.RiskLevel,
                c.SegmentCode,
                db.ClientContactPoints
                    .Where(cp => cp.ClientId == c.Id
                              && cp.Type == ContactPointType.Phone
                              && cp.ValidTo == null)
                    .OrderByDescending(cp => cp.IsPrimary)
                    .ThenByDescending(cp => cp.ValidFrom)
                    .Select(cp => cp.EncryptedValue)
                    .FirstOrDefault()))
            .ToListAsync(ct);

        var items = rows
            .Select(r => new ClientSearchItemDto(
                Id: r.Id,
                ClientNumber: r.ClientNumber,
                DisplayName: r.DisplayName,
                ClientType: r.Type.ToString(),
                Status: r.Status.ToString(),
                AgencyId: r.AgencyId,
                AdvisorUserId: r.AdvisorUserId,
                KycStatus: r.KycStatus.ToString(),
                RiskLevel: r.RiskLevel.ToString(),
                SegmentCode: r.SegmentCode,
                // At most one decrypt per returned row — the list must show a recognisable
                // masked number, and the masker needs the clear value to build it.
                PrimaryPhoneMasked: ClientDtoMapper.MaskEncryptedContactValue(
                    ContactPointType.Phone, r.PrimaryPhoneEncrypted, encryptor)))
            .ToList();

        return Result.Ok(new PagedResult<ClientSearchItemDto>(items, totalCount, page, pageSize));
    }

    /// <summary>
    /// Flat server-side projection of one search row. Keeps the enums typed (so
    /// <c>ToString()</c> happens in memory, not in SQL) and carries the primary phone as
    /// ciphertext, which is decrypted exactly once per row afterwards.
    /// </summary>
    private sealed record SearchRow(
        Guid Id,
        string ClientNumber,
        string DisplayName,
        ClientType Type,
        ClientStatus Status,
        Guid AgencyId,
        Guid? AdvisorUserId,
        Sankore.Modules.Kyc.PublicApi.KycStatus KycStatus,
        RiskLevel RiskLevel,
        string? SegmentCode,
        string? PrimaryPhoneEncrypted);
}
