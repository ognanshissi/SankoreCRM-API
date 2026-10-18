using System.Security.Claims;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;
using Sankore.Admin.Infrastructure.Auth;

namespace Sankore.Admin.Features.Auth.Logout;

public static class LogoutEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/logout", async (LogoutRequest? req, ClaimsPrincipal user, ISender sender, CancellationToken ct) =>
            {
                if (AdminClaims.AdminId(user) is not { } adminId)
                    return Results.Unauthorized();

                await sender.Send(new LogoutCommand(adminId, req?.RefreshToken), ct);
                return Results.NoContent();
            })
            .WithName("Logout")
            .WithSummary("End the session of the refresh token sent, or every session when none is sent")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .RequireAuthorization()
            .WithOpenApi();
    }

    public sealed record LogoutRequest(string? RefreshToken);
}

/// <param name="RefreshToken">
/// The session to close. Sent, only that one ends — signing out of one browser must not sign the
/// operator out of another. Absent, every session ends, which is also the "sign out everywhere".
/// </param>
public sealed record LogoutCommand(Guid AdminId, string? RefreshToken) : IRequest;

internal sealed class LogoutHandler(AdminDbContext db) : IRequestHandler<LogoutCommand>
{
    public async Task Handle(LogoutCommand request, CancellationToken ct)
    {
        var query = db.PlatformAdminRefreshTokens.AsTracking()
            .Where(t => t.AdminId == request.AdminId && t.RevokedAt == null);

        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            // Scoped to the caller: a token belonging to another operator is silently ignored
            // rather than revoked, or a logout would be a way to end somebody else's session.
            var hash = PlatformAdminRefreshToken.Hash(request.RefreshToken);
            query = query.Where(t => t.TokenHash == hash);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var token in await query.ToListAsync(ct))
            token.Revoke(now);

        await db.SaveChangesAsync(ct);
    }
}
