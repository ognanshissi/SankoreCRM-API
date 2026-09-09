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

    public bool IsActive => RevokedAt is null && DateTimeOffset.UtcNow < ExpiresAt;

    private RefreshToken() { } // EF Core

    public static RefreshToken Create(Guid tenantId, Guid userId, TimeSpan ttl)
    {
        return new RefreshToken
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(ttl),
        };
    }

    public void Revoke() => RevokedAt = DateTimeOffset.UtcNow;
}
