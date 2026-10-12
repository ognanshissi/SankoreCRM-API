namespace Sankore.Modules.Customers.Features.Clients.SearchClients;

using System.Transactions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Domain.Matching;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Kyc.PublicApi;
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
    IBlindIndexer indexer,
    IAdministrationModule? administration = null,
    IKycModule? kyc = null,
    ILogger<SearchClientsHandler>? logger = null
) : IRequestHandler<SearchClientsQuery, Result<PagedResult<ClientSearchItemDto>>>
{
    private const int MaxPageSize = 200;

    public async Task<Result<PagedResult<ClientSearchItemDto>>> Handle(
        SearchClientsQuery request, CancellationToken ct)
    {
        var tenantId = currentUser.TenantId;
        var actor = currentUser.Id;

        var query = await ClientSearchFilter.ApplyAsync(
            db, db.Clients.AsQueryable(), request.Criteria, tenantId, actor, agencyScope, indexer, ct);
        query = ClientSearchFilter.Quick(query, request.Quick);

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
                c.HasPepExposure,
                c.CreatedAt,
                db.ClientContactPoints
                    .Where(cp => cp.ClientId == c.Id
                              && cp.Type == ContactPointType.Phone
                              && cp.ValidTo == null)
                    .OrderByDescending(cp => cp.IsPrimary)
                    .ThenByDescending(cp => cp.ValidFrom)
                    .Select(cp => cp.EncryptedValue)
                    .FirstOrDefault(),
                db.ClientTimelineEntries
                    .Where(t => t.ClientId == c.Id)
                    .OrderByDescending(t => t.OccurredAt)
                    .Select(t => new LastActivity(t.Summary, t.OccurredAt))
                    .FirstOrDefault()))
            .ToListAsync(ct);

        var clientIds = rows.Select(r => r.Id).ToList();
        var holdings = await db.ProductHoldings
            .Where(h => clientIds.Contains(h.ClientId)
                     && h.Status != HoldingStatus.Draft
                     && h.Status != HoldingStatus.Rejected
                     && h.Status != HoldingStatus.Closed)
            .OrderBy(h => h.CreatedAt)
            .Select(h => new { h.ClientId, h.ProductCode })
            .ToListAsync(ct);

        var lookups = await ResolveLookupsAsync(tenantId, rows, holdings.Select(h => h.ProductCode), ct);
        var productsByClient = holdings
            .GroupBy(h => h.ClientId)
            .ToDictionary(g => g.Key, g => g
                .Select(h => lookups.ProductNames.GetValueOrDefault(h.ProductCode) ?? h.ProductCode)
                .Distinct()
                .ToList());

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
                    ContactPointType.Phone, r.PrimaryPhoneEncrypted, encryptor),
                HasPepExposure: r.HasPepExposure,
                CreatedAt: r.CreatedAt,
                AgencyName: lookups.AgencyNames.GetValueOrDefault(r.AgencyId),
                AdvisorName: r.AdvisorUserId is { } advisor ? lookups.UserNames.GetValueOrDefault(advisor) : null,
                Products: productsByClient.GetValueOrDefault(r.Id) ?? [],
                LastActivitySummary: r.LastActivity?.Summary,
                LastActivityAt: r.LastActivity?.OccurredAt,
                Kyc: lookups.Journeys.TryGetValue(r.Id, out var j)
                    ? new ClientKycJourneyDto(j.KycFileId, j.Stage, j.CompletedSteps, j.IdentityDocumentRead,
                        j.FaceMatched, j.ProofOfAddressProvided, j.AmlCheckCompleted, j.ConfidenceScore)
                    : null))
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
        Kyc.PublicApi.KycStatus KycStatus,
        RiskLevel RiskLevel,
        string? SegmentCode,
        bool HasPepExposure,
        DateTimeOffset CreatedAt,
        string? PrimaryPhoneEncrypted,
        LastActivity? LastActivity);

    private sealed record LastActivity(string Summary, DateTimeOffset OccurredAt);

    private sealed record Lookups(
        IReadOnlyDictionary<Guid, string> AgencyNames,
        IReadOnlyDictionary<Guid, string> UserNames,
        IReadOnlyDictionary<string, string> ProductNames,
        IReadOnlyDictionary<Guid, KycJourneySummary> Journeys);

    /// <summary>
    /// Ce que la page affiche et que ce module ne possède pas : noms d'agence, de conseiller et de
    /// produit, parcours KYC. Un appel groupé par module, borné par la taille de page. Chaque
    /// lecture échoue seule et sans bruit — une panne du module KYC ne doit pas vider la liste des
    /// clients, seulement la colonne qu'il alimente.
    /// </summary>
    private async Task<Lookups> ResolveLookupsAsync(
        Guid tenantId, IReadOnlyList<SearchRow> rows, IEnumerable<string> productCodes, CancellationToken ct)
    {
        IReadOnlyDictionary<Guid, string> agencyNames = new Dictionary<Guid, string>();
        IReadOnlyDictionary<Guid, string> userNames = new Dictionary<Guid, string>();
        IReadOnlyDictionary<string, string> productNames = new Dictionary<string, string>();
        IReadOnlyDictionary<Guid, KycJourneySummary> journeys = new Dictionary<Guid, KycJourneySummary>();
        if (rows.Count == 0) return new Lookups(agencyNames, userNames, productNames, journeys);

        // Hors de toute transaction ambiante : ces lectures passent par d'autres DbContext.
        using var _ = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

        if (administration is not null)
        {
            try
            {
                var names = new Dictionary<Guid, string>();
                foreach (var agencyId in rows.Select(r => r.AgencyId).Distinct())
                {
                    var agency = await administration.GetAgencyAsync(tenantId, agencyId, ct);
                    if (agency is not null) names[agencyId] = agency.Name;
                }
                agencyNames = names;

                var advisorIds = rows.Where(r => r.AdvisorUserId.HasValue).Select(r => r.AdvisorUserId!.Value).Distinct().ToList();
                userNames = await administration.GetUserNamesAsync(tenantId, advisorIds, ct);

                var codes = productCodes.Distinct().ToList();
                if (codes.Count > 0)
                {
                    productNames = (await administration.ListProductsAsync(tenantId, null, ct))
                        .GroupBy(p => p.Code, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Client list: agency, advisor or product names could not be resolved.");
            }
        }

        if (kyc is not null)
        {
            try
            {
                journeys = await kyc.GetJourneySummariesAsync(tenantId, rows.Select(r => r.Id).ToList(), ct);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Client list: KYC journeys could not be read.");
            }
        }

        return new Lookups(agencyNames, userNames, productNames, journeys);
    }
}
