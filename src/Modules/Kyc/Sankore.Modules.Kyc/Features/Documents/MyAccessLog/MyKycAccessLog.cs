namespace Sankore.Modules.Kyc.Features.Documents.MyAccessLog;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>Une ouverture de pièce KYC par l'utilisateur connecté.</summary>
public sealed record MyKycAccessDto(
    DateTimeOffset AccessedAt,
    Guid KycFileId,
    /// <summary>Résolu auprès du module clients ; null quand il ne connaît plus l'identifiant.</summary>
    string? CustomerName,
    /// <summary>Nature de la pièce (IdentityDocumentFront, Selfie…), ou null si elle n'est plus référencée.</summary>
    string? DocumentKind);

internal sealed record GetMyKycAccessLogQuery(int Limit) : IRequest<Result<List<MyKycAccessDto>>>;

/// <summary>
/// Ce que l'utilisateur a lui-même ouvert, tiré du journal que la lecture d'une pièce écrit déjà.
/// Sans permission particulière : c'est sa propre trace, et la lui montrer est le but — savoir que
/// chaque ouverture est enregistrée. Le motif saisi n'est pas renvoyé : il ne sort pas de l'écran
/// de conformité.
/// </summary>
internal sealed class GetMyKycAccessLogHandler(KycDbContext db, ICurrentUser currentUser, ICustomersModule customers)
    : IRequestHandler<GetMyKycAccessLogQuery, Result<List<MyKycAccessDto>>>
{
    public async Task<Result<List<MyKycAccessDto>>> Handle(GetMyKycAccessLogQuery query, CancellationToken ct)
    {
        var limit = Math.Clamp(query.Limit, 1, 100);

        var rows = await db.KycDocumentAccessLogs
            .Where(l => l.ActorUserId == currentUser.Id)
            .OrderByDescending(l => l.AccessedAt)
            .Take(limit)
            .Select(l => new
            {
                l.AccessedAt,
                l.KycFileId,
                CustomerId = db.KycFiles.Where(f => f.Id == l.KycFileId).Select(f => (Guid?)f.CustomerId).FirstOrDefault(),
                Kind = db.KycDocuments.Where(d => d.StorageRef == l.StorageRef).Select(d => (Infrastructure.Storage.KycDocumentKind?)d.Kind).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var customerIds = rows.Where(r => r.CustomerId.HasValue).Select(r => r.CustomerId!.Value).Distinct().ToList();
        IReadOnlyDictionary<Guid, ClientSummary> names = customerIds.Count == 0
            ? new Dictionary<Guid, ClientSummary>()
            : await customers.GetClientSummariesAsync(currentUser.TenantId, customerIds, ct);

        return Result.Ok(rows.Select(r => new MyKycAccessDto(
            r.AccessedAt,
            r.KycFileId,
            r.CustomerId is { } id && names.TryGetValue(id, out var c) ? c.DisplayName : null,
            r.Kind?.ToString())).ToList());
    }
}

internal static class MyKycAccessLogEndpoint
{
    internal static IEndpointRouteBuilder MapMyKycAccessLog(this IEndpointRouteBuilder app)
    {
        app.MapGet("access-log/me", async (ISender sender, CancellationToken ct, int limit = 30) =>
            {
                var result = await sender.Send(new GetMyKycAccessLogQuery(limit), ct);
                return Results.Ok(result.Value);
            })
            .WithName("GetMyKycAccessLog")
            .WithSummary("Les dernières pièces KYC ouvertes par l'utilisateur connecté (30 par défaut, 100 au plus).")
            .RequireAuthorization()
            .Produces<List<MyKycAccessDto>>();

        return app;
    }
}
