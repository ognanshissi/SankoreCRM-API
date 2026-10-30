using System.Security.Cryptography;

namespace Sankore.Modules.Administration.Domain;

/// <summary>
/// Opaque refresh token tied to a user session. Stored in DB for revocation support.
/// Rotated on every use — the old token is revoked when a new one is issued.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public string Token { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>La session que ce jeton prolonge. Null pour les jetons émis avant les sessions.</summary>
    public Guid? SessionId { get; private set; }

    /// <summary>
    /// Sandbox lot 2: the test twin this session works on, null in production. Carried from token
    /// to token so a refresh keeps the mode the user chose — the access token it mints names this
    /// tenant, and <see cref="TenantId"/> stays the user's own (the team's).
    /// </summary>
    public Guid? DataTenantId { get; private set; }

    public bool IsActive => RevokedAt is null && DateTimeOffset.UtcNow < ExpiresAt;

    private RefreshToken() { } // EF Core

    public static RefreshToken Create(Guid tenantId, Guid userId, TimeSpan ttl, Guid? sessionId = null, Guid? dataTenantId = null)
    {
        return new RefreshToken
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(ttl),
            SessionId = sessionId,
            DataTenantId = dataTenantId,
        };
    }

    public void Revoke() => RevokedAt = DateTimeOffset.UtcNow;
}
