namespace Sankore.Shared.Infrastructure.Secrets;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sankore.Shared.Kernel;

/// <summary>
/// AES-256-GCM secrets vault backed by PostgreSQL.
/// Encryption key is loaded from configuration (Secrets:EncryptionKey).
/// </summary>
public sealed class AesSecretsModule(
    SecretsDbContext db,
    IOptions<SecretsOptions> options) : ISecretsModule
{
    private byte[] Key => Convert.FromBase64String(options.Value.EncryptionKey);

    public async Task SetAsync(SecretKey key, string value, DateTimeOffset? expiresAt = null, CancellationToken ct = default)
    {
        var (cipherText, iv, tag) = Encrypt(value);
        var now = DateTimeOffset.UtcNow;

        var existing = await db.Entries
            .FirstOrDefaultAsync(e => e.TenantId == key.TenantId
                                      && e.Scope == key.Scope
                                      && e.EntityId == key.EntityId
                                      && e.Name == key.Name, ct);

        if (existing is not null)
        {
            existing.EncryptedValue = cipherText;
            existing.Iv = iv;
            existing.Tag = tag;
            existing.ExpiresAt = expiresAt;
            existing.UpdatedAt = now;
        }
        else
        {
            db.Entries.Add(new SecretEntry
            {
                Id = Guid.NewGuid(),
                TenantId = key.TenantId,
                Scope = key.Scope,
                EntityId = key.EntityId,
                Name = key.Name,
                EncryptedValue = cipherText,
                Iv = iv,
                Tag = tag,
                ExpiresAt = expiresAt,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<SecretHint?> GetHintAsync(SecretKey key, CancellationToken ct = default)
    {
        var entry = await db.Entries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == key.TenantId
                                      && e.Scope == key.Scope
                                      && e.EntityId == key.EntityId
                                      && e.Name == key.Name, ct);

        if (entry is null) return null;

        var decrypted = Decrypt(entry.EncryptedValue, entry.Iv, entry.Tag);
        return new SecretHint(entry.Name, Mask(decrypted), entry.ExpiresAt);
    }

    public async Task<string?> GetValueAsync(SecretKey key, CancellationToken ct = default)
    {
        var entry = await db.Entries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == key.TenantId
                                      && e.Scope == key.Scope
                                      && e.EntityId == key.EntityId
                                      && e.Name == key.Name, ct);

        if (entry is null) return null;

        return Decrypt(entry.EncryptedValue, entry.Iv, entry.Tag);
    }

    public async Task DeleteAllAsync(Guid tenantId, string scope, Guid entityId, CancellationToken ct = default)
    {
        var entries = await db.Entries
            .Where(e => e.TenantId == tenantId && e.Scope == scope && e.EntityId == entityId)
            .ToListAsync(ct);

        db.Entries.RemoveRange(entries);
        await db.SaveChangesAsync(ct);
    }

    // ── AES-256-GCM ─────────────────────────────────────────────────────

    private (string CipherText, string Iv, string Tag) Encrypt(string plainText)
    {
        var iv = new byte[12]; // GCM standard nonce
        RandomNumberGenerator.Fill(iv);

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[16]; // GCM tag

        using var aes = new AesGcm(Key, 16);
        aes.Encrypt(iv, plainBytes, cipherBytes, tag);

        return (
            Convert.ToBase64String(cipherBytes),
            Convert.ToBase64String(iv),
            Convert.ToBase64String(tag));
    }

    private string Decrypt(string cipherText, string iv, string tag)
    {
        var cipherBytes = Convert.FromBase64String(cipherText);
        var ivBytes = Convert.FromBase64String(iv);
        var tagBytes = Convert.FromBase64String(tag);
        var plainBytes = new byte[cipherBytes.Length];

        using var aes = new AesGcm(Key, 16);
        aes.Decrypt(ivBytes, cipherBytes, tagBytes, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    private static string Mask(string value)
    {
        if (value.Length <= 8) return "****";
        return value[..4] + "****" + value[^4..];
    }
}

public sealed class SecretsOptions
{
    /// <summary>Base64-encoded 32-byte AES-256 key.</summary>
    public string EncryptionKey { get; set; } = default!;
}
