using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.Security;

public sealed record SecurityOverviewDto(
    /// <summary>Dernier changement connu, null si aucun n'a été historisé.</summary>
    DateTimeOffset? PasswordChangedAt,
    DateTimeOffset PasswordExpiresAt,
    /// <summary>
    /// Toujours faux pour l'instant : aucun second facteur n'est vérifié à la connexion.
    /// <c>AppUser.MfaEnabled</c> n'est qu'une intention enregistrée, l'écran ne doit pas la
    /// présenter comme une protection active.
    /// </summary>
    bool TwoFactorConfigured);

public sealed record UserSessionDto(
    Guid Id,
    string? Browser,
    string Platform,
    string ClientKind,
    string? IpAddress,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    bool IsCurrent);

public sealed record GetMySecurityOverviewQuery : IRequest<Result<SecurityOverviewDto>>;

internal sealed class GetMySecurityOverviewHandler(AdministrationDbContext db, ICurrentUser currentUser)
    : IRequestHandler<GetMySecurityOverviewQuery, Result<SecurityOverviewDto>>
{
    public async Task<Result<SecurityOverviewDto>> Handle(GetMySecurityOverviewQuery request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == currentUser.Id, ct);
        if (user is null)
            return Result.Fail<SecurityOverviewDto>("USER_NOT_FOUND");

        var lastSet = await db.PasswordHistories
            .Where(p => p.UserId == user.Id)
            .OrderByDescending(p => p.SetAt)
            .Select(p => (DateTime?)p.SetAt)
            .FirstOrDefaultAsync(ct);

        return Result.Ok(new SecurityOverviewDto(
            lastSet is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null,
            user.PasswordExpiresAt,
            TwoFactorConfigured: false));
    }
}

public sealed record ListMySessionsQuery(Guid? CurrentSessionId) : IRequest<Result<List<UserSessionDto>>>;

internal sealed class ListMySessionsHandler(AdministrationDbContext db, ICurrentUser currentUser)
    : IRequestHandler<ListMySessionsQuery, Result<List<UserSessionDto>>>
{
    public async Task<Result<List<UserSessionDto>>> Handle(ListMySessionsQuery request, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        // Ouverte = non fermée ET encore prolongeable. Une session dont le dernier jeton a expiré
        // ne reviendra pas sans mot de passe : l'afficher « active » serait faux.
        var sessions = await db.UserSessions
            .Where(s => s.UserId == currentUser.Id && s.RevokedAt == null)
            .Where(s => db.RefreshTokens.Any(t => t.SessionId == s.Id && t.RevokedAt == null && t.ExpiresAt > now))
            .OrderByDescending(s => s.LastSeenAt)
            .ToListAsync(ct);

        return Result.Ok(sessions
            .Select(s => new UserSessionDto(
                s.Id, s.Browser, s.Platform.ToString(), s.ClientKind.ToString(), s.IpAddress,
                s.CreatedAt, s.LastSeenAt, s.Id == request.CurrentSessionId))
            // La session courante en tête, quel que soit son dernier passage enregistré.
            .OrderByDescending(s => s.IsCurrent)
            .ToList());
    }
}

internal static class MySecurityEndpoints
{
    internal static IEndpointRouteBuilder MapMySecurity(this IEndpointRouteBuilder app)
    {
        app.MapGet("me/security", async (ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new GetMySecurityOverviewQuery(), ct);
                return result.IsSuccess ? Results.Ok(result.Value) : Results.NotFound();
            })
            .WithName("GetMySecurityOverview")
            .WithSummary("Mot de passe (dernier changement, expiration) et état de la validation en deux étapes de l'utilisateur connecté.")
            .WithTags("Users")
            .RequireAuthorization()
            .Produces<SecurityOverviewDto>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("me/sessions", async (ClaimsPrincipal user, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new ListMySessionsQuery(SessionClaim.Read(user)), ct);
                return Results.Ok(result.Value);
            })
            .WithName("ListMySessions")
            .WithSummary("Sessions ouvertes de l'utilisateur connecté, la session courante en tête.")
            .WithTags("Users")
            .RequireAuthorization()
            .Produces<List<UserSessionDto>>();

        app.MapDelete("me/sessions/{sessionId:guid}", async (Guid sessionId, ClaimsPrincipal user, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new EndMySessionsCommand(SessionClaim.Read(user), sessionId), ct);
                return result.IsSuccess
                    ? Results.NoContent()
                    : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
            })
            .WithName("EndMySession")
            .WithSummary("Déconnecte à distance une autre session de l'utilisateur connecté.")
            .WithTags("Users")
            .RequireAuthorization()
            .DisallowImpersonation()
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest);

        app.MapPost("me/sessions/end-others", async (ClaimsPrincipal user, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new EndMySessionsCommand(SessionClaim.Read(user), null), ct);
                return result.IsSuccess
                    ? Results.Ok(new EndSessionsResult(result.Value))
                    : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
            })
            .WithName("EndMyOtherSessions")
            .WithSummary("Déconnecte toutes les sessions de l'utilisateur connecté sauf celle-ci.")
            .WithTags("Users")
            .RequireAuthorization()
            .DisallowImpersonation()
            .Produces<EndSessionsResult>()
            .Produces(StatusCodes.Status400BadRequest);

        app.MapPost("me/login-history/report", async (ReportSuspiciousLoginRequest req, ClaimsPrincipal user, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new ReportSuspiciousLoginCommand(SessionClaim.Read(user), req.LoginId, req.Comment), ct);
                return result.IsSuccess
                    ? Results.Ok(new EndSessionsResult(result.Value))
                    : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
            })
            .WithName("ReportSuspiciousLogin")
            .WithSummary("Signale une connexion qui ne vient pas de l'utilisateur : ses autres sessions sont fermées et son superviseur est prévenu.")
            .WithTags("Users")
            .RequireAuthorization()
            .DisallowImpersonation()
            .Produces<EndSessionsResult>()
            .Produces(StatusCodes.Status400BadRequest);

        return app;
    }
}

public sealed record EndSessionsResult(int EndedSessions);
