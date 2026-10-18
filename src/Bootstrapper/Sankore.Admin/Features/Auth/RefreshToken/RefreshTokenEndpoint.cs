using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Features.Auth.Login;
using Sankore.Admin.Infrastructure;
using Sankore.Admin.Infrastructure.Auth;

namespace Sankore.Admin.Features.Auth.RefreshToken;

public static class RefreshTokenEndpoint
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/refresh", async (RefreshTokenRequest req, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new RefreshTokenCommand(req.RefreshToken ?? string.Empty), ct);
                return result.Tokens is { } tokens
                    ? Results.Ok(tokens)
                    : Results.Problem(result.Error, statusCode: StatusCodes.Status401Unauthorized);
            })
            .WithName("RefreshToken")
            .WithSummary("Exchange a refresh token for a new token pair (rotation)")
            .Produces<AdminAuthTokens>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .AllowAnonymous()
            .RequireRateLimiting(AdminAuthSetup.RefreshRateLimit)
            .WithOpenApi();
    }

    public sealed record RefreshTokenRequest(string? RefreshToken);
}

public sealed record RefreshTokenCommand(string RefreshToken) : IRequest<AuthOutcome>;

internal sealed class RefreshTokenHandler(
    AdminDbContext db,
    AdminTokenService tokens,
    ILogger<RefreshTokenHandler> logger) : IRequestHandler<RefreshTokenCommand, AuthOutcome>
{
    private const string Invalid = "Session expirée. Reconnectez-vous.";

    public async Task<AuthOutcome> Handle(RefreshTokenCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            return AuthOutcome.Fail(Invalid);

        var now = DateTimeOffset.UtcNow;
        var hash = PlatformAdminRefreshToken.Hash(request.RefreshToken);
        var existing = await db.PlatformAdminRefreshTokens.AsTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (existing is null)
            return AuthOutcome.Fail(Invalid);

        // A token is presented again after it was rotated: either a client bug or a stolen copy,
        // and the server cannot tell which. Ending every session of the account is the answer
        // that is safe in both cases — the legitimate operator signs in again.
        if (existing.RevokedAt is not null)
        {
            var live = await db.PlatformAdminRefreshTokens.AsTracking()
                .Where(t => t.AdminId == existing.AdminId && t.RevokedAt == null)
                .ToListAsync(ct);
            foreach (var token in live)
                token.Revoke(now);
            await db.SaveChangesAsync(ct);

            logger.LogWarning(
                "Revoked refresh token reused for platform administrator {AdminId}; {Count} live session(s) ended",
                existing.AdminId, live.Count);
            return AuthOutcome.Fail(Invalid);
        }

        if (!existing.IsActiveAt(now))
            return AuthOutcome.Fail(Invalid);

        var admin = await db.PlatformAdmins.AsTracking()
            .FirstOrDefaultAsync(a => a.Id == existing.AdminId, ct);

        if (admin is null || !admin.IsActive)
        {
            existing.Revoke(now);
            await db.SaveChangesAsync(ct);
            return AuthOutcome.Fail(Invalid);
        }

        existing.Revoke(now);
        var (issued, refresh) = tokens.Issue(admin);
        db.PlatformAdminRefreshTokens.Add(refresh);
        await db.SaveChangesAsync(ct);

        return AuthOutcome.Ok(issued);
    }
}
