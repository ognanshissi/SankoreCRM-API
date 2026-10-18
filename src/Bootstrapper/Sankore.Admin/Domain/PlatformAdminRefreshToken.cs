using System.Security.Cryptography;
using System.Text;

namespace Sankore.Admin.Domain;

/// <summary>
/// Opaque refresh token of a <see cref="PlatformAdmin"/>, rotated on every use.
///
/// <para>
/// Only a SHA-256 of the token is stored, unlike the tenant side: a copy of this table must not
/// be enough to impersonate an operator who can suspend every tenant. The clear value exists
/// once, in the response to the call that issued it.
/// </para>
/// </summary>
public sealed class PlatformAdminRefreshToken
{
    public Guid Id { get; private set; }
    public Guid AdminId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActiveAt(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    private PlatformAdminRefreshToken() { } // EF Core

    /// <summary>Returns the entity to persist and the clear token to hand to the client.</summary>
    public static (PlatformAdminRefreshToken Entity, string Token) Issue(Guid adminId, TimeSpan ttl)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var now = DateTimeOffset.UtcNow;
        var entity = new PlatformAdminRefreshToken
        {
            Id = Guid.NewGuid(),
            AdminId = adminId,
            TokenHash = Hash(token),
            CreatedAt = now,
            ExpiresAt = now.Add(ttl),
        };
        return (entity, token);
    }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
