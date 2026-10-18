using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Admin.Domain;
using Sankore.Admin.Infrastructure;
using Sankore.Admin.Infrastructure.Auth;

namespace Sankore.Admin.Features.Auth.Login;

public sealed record LoginCommand(string Email, string Password) : IRequest<AuthOutcome>;

/// <summary>Either tokens or the message to answer with — never both.</summary>
public sealed record AuthOutcome(AdminAuthTokens? Tokens, string? Error)
{
    public static AuthOutcome Ok(AdminAuthTokens tokens) => new(tokens, null);
    public static AuthOutcome Fail(string error) => new(null, error);
}

internal sealed class LoginHandler(
    AdminDbContext db,
    IPasswordHasher<PlatformAdmin> hasher,
    AdminTokenService tokens,
    ILogger<LoginHandler> logger) : IRequestHandler<LoginCommand, AuthOutcome>
{
    // One message for every refusal: telling "unknown address" from "wrong password" turns the
    // login into a way to list operator accounts.
    private const string InvalidLogin = "Identifiants invalides.";
    private const string Locked = "Compte temporairement verrouillé. Réessayez plus tard.";

    public async Task<AuthOutcome> Handle(LoginCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
            return AuthOutcome.Fail(InvalidLogin);

        var normalized = PlatformAdmin.Normalize(request.Email);
        var admin = await db.PlatformAdmins.AsTracking()
            .FirstOrDefaultAsync(a => a.NormalizedEmail == normalized, ct);

        if (admin is null || !admin.IsActive)
        {
            // Hash anyway, so an unknown address does not answer measurably faster than a known one.
            hasher.VerifyHashedPassword(DummyAdmin, DummyHash, request.Password);
            return AuthOutcome.Fail(InvalidLogin);
        }

        var now = DateTimeOffset.UtcNow;
        var verdict = hasher.VerifyHashedPassword(admin, admin.PasswordHash, request.Password);

        // Checked AFTER the password, and failures are not counted while locked: the lock message
        // is only ever shown to somebody who knows the password, so it confirms nothing to a
        // guesser, and a guesser cannot extend the lock indefinitely.
        if (admin.IsLockedOut(now))
            return AuthOutcome.Fail(verdict == PasswordVerificationResult.Failed ? InvalidLogin : Locked);

        if (verdict == PasswordVerificationResult.Failed)
        {
            admin.RecordFailedLogin(now);
            await db.SaveChangesAsync(ct);
            if (admin.IsLockedOut(now))
                logger.LogWarning("Platform administrator {AdminId} locked after repeated failures", admin.Id);
            return AuthOutcome.Fail(InvalidLogin);
        }

        if (verdict == PasswordVerificationResult.SuccessRehashNeeded)
            admin.ReplacePasswordHash(hasher.HashPassword(admin, request.Password));

        admin.RecordSuccessfulLogin(now);
        var (issued, refresh) = tokens.Issue(admin);
        db.PlatformAdminRefreshTokens.Add(refresh);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Platform administrator {AdminId} signed in", admin.Id);
        return AuthOutcome.Ok(issued);
    }

    private static readonly PlatformAdmin DummyAdmin = PlatformAdmin.Create("dummy@invalid", "dummy", string.Empty);
    private static readonly string DummyHash = new PasswordHasher<PlatformAdmin>().HashPassword(DummyAdmin, Guid.NewGuid().ToString());
}
