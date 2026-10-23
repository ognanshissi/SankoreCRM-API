using MediatR;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;

namespace Sankore.Modules.Customers.Features.Clients.SearchClients;

/// <param name="DuplicatesToReview">Paires de doublons à examiner, quel que soit le filtre.</param>
public sealed record ClientQuickCountsDto(
    int All, int KycToStart, int KycInProgress, int HighRisk, int WithoutAdvisor, int DuplicatesToReview);

/// <summary>
/// Les compteurs des raccourcis de la liste, sous les mêmes critères que la recherche (agence,
/// conseiller, type, texte…) et par le même filtre : chaque nombre est celui que la liste afficherait.
/// </summary>
public sealed record GetClientQuickCountsQuery(ClientSearchCriteria Criteria) : IRequest<Result<ClientQuickCountsDto>>;

internal sealed class GetClientQuickCountsHandler(
    CustomersDbContext db,
    ICurrentUser currentUser,
    IAgencyScopeProvider agencyScope,
    IBlindIndexer indexer,
    ClientVisibility? visibility = null)
    : IRequestHandler<GetClientQuickCountsQuery, Result<ClientQuickCountsDto>>
{
    public async Task<Result<ClientQuickCountsDto>> Handle(GetClientQuickCountsQuery request, CancellationToken ct)
    {
        var query = await ClientSearchFilter.ApplyAsync(
            db, visibility?.Apply(db.Clients) ?? db.Clients, request.Criteria, currentUser.TenantId, currentUser.Id, agencyScope, indexer, ct);

        async Task<int> Count(ClientQuickFilter quick) => await ClientSearchFilter.Quick(query, quick).CountAsync(ct);

        return Result.Ok(new ClientQuickCountsDto(
            await Count(ClientQuickFilter.All),
            await Count(ClientQuickFilter.KycToStart),
            await Count(ClientQuickFilter.KycInProgress),
            await Count(ClientQuickFilter.HighRisk),
            await Count(ClientQuickFilter.WithoutAdvisor),
            await db.DuplicateCandidates.CountAsync(d => d.Status == DuplicateCandidateStatus.ToReview, ct)));
    }
}

public static class GetClientQuickCountsEndpoint
{
    public static IEndpointRouteBuilder MapGetClientQuickCounts(this IEndpointRouteBuilder app)
    {
        app.MapGet("quick-counts", async (
                ISender sender,
                string? clientNumber, string? phone, string? identityDocumentNumber, string? name,
                ClientStatus? status, Guid? agencyId, Guid? advisorUserId, ClientType? type, string? segmentCode,
                RiskLevel? riskLevel, bool? hasPepExposure,
                CancellationToken ct) =>
            {
                var result = await sender.Send(new GetClientQuickCountsQuery(new ClientSearchCriteria(
                    clientNumber, phone, identityDocumentNumber, name, status, agencyId, advisorUserId, type,
                    segmentCode, riskLevel, hasPepExposure)), ct);
                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
            })
            .WithName("GetClientQuickCounts")
            .WithSummary("Compteurs des raccourcis de la liste des clients (tous, KYC à démarrer, KYC en cours, risque élevé, sans conseiller) et doublons à examiner. Mêmes critères que SearchClients. Requires permission: customers:read.")
            .WithTags("Clients")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<ClientQuickCountsDto>()
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }
}
