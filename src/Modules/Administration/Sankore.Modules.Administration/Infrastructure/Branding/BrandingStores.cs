using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.ObjectStorage;

namespace Sankore.Modules.Administration.Infrastructure.Branding;

/// <summary>Section <c>Administration:Logos</c>.</summary>
public sealed class CompanyLogoStorageOptions
{
    public const string SectionName = "Administration:Logos";

    /// <summary>Dossier de repli quand aucun bucket n'est configuré. Par défaut : sous le dossier de l'application.</summary>
    public string? BasePath { get; set; }
}

/// <summary>Section <c>Administration:Avatars</c>.</summary>
public sealed class UserAvatarStorageOptions
{
    public const string SectionName = "Administration:Avatars";

    /// <summary>Dossier de repli quand aucun bucket n'est configuré. Par défaut : sous le dossier de l'application.</summary>
    public string? BasePath { get; set; }

    /// <summary>Clé maîtresse (base64, 32 octets). Absente : pas de stockage, un dépôt répond « indisponible ».</summary>
    public string? EncryptionKey { get; set; }
}

public static class CompanyLogoStorage
{
    public const string Concern = "company-logos";

    public static string ResolveBasePath(IConfiguration config, IHostEnvironment environment) =>
        BrandingPaths.Resolve(config[$"{CompanyLogoStorageOptions.SectionName}:BasePath"], Concern);
}

public static class UserAvatarStorage
{
    public const string Concern = "user-avatars";

    public static string ResolveBasePath(IConfiguration config, IHostEnvironment environment) =>
        BrandingPaths.Resolve(config[$"{UserAvatarStorageOptions.SectionName}:BasePath"], Concern);
}

internal static class BrandingPaths
{
    public static string Resolve(string? configured, string concern) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, concern)
            : Path.GetFullPath(configured);

    /// <summary>Le tenant n'apparaît pas en clair dans les clés : un listage du bucket ne dit pas qui est client.</summary>
    public static string TenantToken(Guid tenantId) =>
        Convert.ToHexString(SHA256.HashData(tenantId.ToByteArray()))[..16].ToLowerInvariant();

    public static string NewObjectId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}

internal interface ICompanyLogoStore
{
    Task<string> StoreAsync(Guid tenantId, byte[] content, CancellationToken ct);

    Task<byte[]?> OpenAsync(Guid tenantId, string storageRef, CancellationToken ct);

    Task<bool> DeleteAsync(Guid tenantId, string storageRef, CancellationToken ct);
}

internal interface IUserAvatarStore
{
    Task<string> StoreAsync(Guid tenantId, byte[] content, CancellationToken ct);

    Task<byte[]?> OpenAsync(Guid tenantId, string storageRef, CancellationToken ct);

    Task<bool> DeleteAsync(Guid tenantId, string storageRef, CancellationToken ct);
}

/// <summary>
/// Les logos, EN CLAIR : un logo est public (la route <c>public/branding</c> le sert à n'importe
/// qui, les e-mails l'affichent), le chiffrer ne protégerait rien. Une référence par dépôt, jamais
/// réécrite : l'URL publiée change avec le logo, et l'ancien objet est supprimé après l'enregistrement.
/// </summary>
internal sealed class CompanyLogoStore(IObjectBackend backend) : ICompanyLogoStore
{
    private static readonly Regex StorageRefShape = new(@"^l1\.[0-9a-f]{16}\.[0-9a-f]{32}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<string> StoreAsync(Guid tenantId, byte[] content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0) throw new InvalidOperationException("Refusing to store an empty logo.");

        var token = BrandingPaths.TenantToken(tenantId);
        var storageRef = $"l1.{token}.{BrandingPaths.NewObjectId()}";
        await backend.PutAsync(BuildKey(storageRef), content, ct);
        return storageRef;
    }

    public async Task<byte[]?> OpenAsync(Guid tenantId, string storageRef, CancellationToken ct) =>
        Belongs(tenantId, storageRef)
            ? await backend.GetAsync(BuildKey(storageRef), CompanyLogo.MaxSizeBytes, ct)
            : null;

    public async Task<bool> DeleteAsync(Guid tenantId, string storageRef, CancellationToken ct) =>
        Belongs(tenantId, storageRef) && await backend.DeleteAsync(BuildKey(storageRef), ct);

    private static bool Belongs(Guid tenantId, string? storageRef) =>
        !string.IsNullOrEmpty(storageRef)
        && StorageRefShape.IsMatch(storageRef)
        && string.Equals(storageRef.Split('.')[1], BrandingPaths.TenantToken(tenantId), StringComparison.Ordinal);

    private static string BuildKey(string storageRef)
    {
        var parts = storageRef.Split('.');
        return $"{parts[1]}/{parts[2]}.logo";
    }
}

/// <summary>
/// Les photos de profil, chiffrées en enveloppe : une clé de données aléatoire par photo, enveloppée
/// par la clé maîtresse <c>Administration:Avatars:EncryptionKey</c>, tenant et référence liés en
/// données associées. Une photo est une donnée personnelle, et chaque magasin de cette solution a sa
/// propre clé. Copié du magasin des pièces jointes de tâches plutôt que partagé, comme tous les
/// magasins ici : le marqueur de format et les données associées gardent les objets de deux usages
/// séparés.
/// </summary>
internal sealed class EncryptedUserAvatarStore : IUserAvatarStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private static readonly byte[] Magic = "UAV1"u8.ToArray();
    private static readonly int HeaderSize = Magic.Length + NonceSize + TagSize + KeySize + NonceSize + TagSize;

    private static readonly Regex StorageRefShape = new(@"^a1\.[0-9a-f]{16}\.[0-9a-f]{32}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IObjectBackend _backend;
    private readonly byte[] _masterKey;

    public EncryptedUserAvatarStore(IObjectBackend backend, IOptions<UserAvatarStorageOptions> options)
    {
        _backend = backend;
        _masterKey = ReadKey(options.Value.EncryptionKey);
    }

    public async Task<string> StoreAsync(Guid tenantId, byte[] content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0) throw new InvalidOperationException("Refusing to store an empty photo.");
        if (content.Length > UserAvatar.MaxSizeBytes)
            throw new InvalidOperationException($"A profile photo may not exceed {UserAvatar.MaxSizeBytes} bytes.");

        var token = BrandingPaths.TenantToken(tenantId);
        var storageRef = $"a1.{token}.{BrandingPaths.NewObjectId()}";
        var aad = AssociatedData(tenantId, storageRef);
        var dataKey = RandomNumberGenerator.GetBytes(KeySize);

        try
        {
            var wrapNonce = RandomNumberGenerator.GetBytes(NonceSize);
            var wrapTag = new byte[TagSize];
            var wrappedKey = new byte[KeySize];

            using (var master = new AesGcm(_masterKey, TagSize))
                master.Encrypt(wrapNonce, dataKey, wrappedKey, wrapTag, aad);

            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var tag = new byte[TagSize];
            var ciphertext = new byte[content.Length];

            using (var data = new AesGcm(dataKey, TagSize))
                data.Encrypt(nonce, content, ciphertext, tag, aad);

            var stored = new byte[HeaderSize + ciphertext.Length];
            var offset = 0;
            foreach (var part in new[] { Magic, wrapNonce, wrapTag, wrappedKey, nonce, tag, ciphertext })
            {
                part.CopyTo(stored, offset);
                offset += part.Length;
            }

            await _backend.PutAsync(BuildKey(storageRef), stored, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }

        return storageRef;
    }

    public async Task<byte[]?> OpenAsync(Guid tenantId, string storageRef, CancellationToken ct)
    {
        if (!Belongs(tenantId, storageRef)) return null;

        var raw = await _backend.GetAsync(BuildKey(storageRef), UserAvatar.MaxSizeBytes + HeaderSize, ct);
        if (raw is null || raw.Length < HeaderSize) return null;

        return Decrypt(raw, tenantId, storageRef);
    }

    public async Task<bool> DeleteAsync(Guid tenantId, string storageRef, CancellationToken ct) =>
        Belongs(tenantId, storageRef) && await _backend.DeleteAsync(BuildKey(storageRef), ct);

    private byte[] Decrypt(byte[] raw, Guid tenantId, string storageRef)
    {
        var span = raw.AsSpan();
        if (!span[..Magic.Length].SequenceEqual(Magic))
            throw new CryptographicException("Not a profile photo object (unknown format marker).");

        var offset = Magic.Length;
        ReadOnlySpan<byte> Next(ReadOnlySpan<byte> s, int size, ref int at) { var part = s.Slice(at, size); at += size; return part; }

        var wrapNonce = Next(span, NonceSize, ref offset);
        var wrapTag = Next(span, TagSize, ref offset);
        var wrappedKey = Next(span, KeySize, ref offset);
        var nonce = Next(span, NonceSize, ref offset);
        var tag = Next(span, TagSize, ref offset);
        var ciphertext = span[offset..];

        var aad = AssociatedData(tenantId, storageRef);
        var dataKey = new byte[KeySize];

        try
        {
            using (var master = new AesGcm(_masterKey, TagSize))
                master.Decrypt(wrapNonce, wrappedKey, wrapTag, dataKey, aad);

            var plaintext = new byte[ciphertext.Length];
            using (var data = new AesGcm(dataKey, TagSize))
                data.Decrypt(nonce, ciphertext, tag, plaintext, aad);

            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private static bool Belongs(Guid tenantId, string? storageRef) =>
        !string.IsNullOrEmpty(storageRef)
        && StorageRefShape.IsMatch(storageRef)
        && string.Equals(storageRef.Split('.')[1], BrandingPaths.TenantToken(tenantId), StringComparison.Ordinal);

    private static string BuildKey(string storageRef)
    {
        var parts = storageRef.Split('.');
        return $"{parts[1]}/{parts[2][..2]}/{parts[2]}.uav";
    }

    private static byte[] AssociatedData(Guid tenantId, string storageRef) =>
        Encoding.UTF8.GetBytes($"user-avatar:{tenantId:N}:{storageRef}");

    private static byte[] ReadKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            throw new InvalidOperationException($"{UserAvatarStorageOptions.SectionName}:EncryptionKey is not configured.");

        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{UserAvatarStorageOptions.SectionName}:EncryptionKey is not valid base64.");
        }

        if (key.Length != KeySize)
            throw new InvalidOperationException($"{UserAvatarStorageOptions.SectionName}:EncryptionKey must be 32 bytes.");

        return key;
    }
}

/// <summary>Lit les octets d'un logo ou d'une photo, où qu'ils soient : stockage objet, ou encore en base.</summary>
internal static class BrandingContent
{
    public static async Task<byte[]?> LogoAsync(ICompanyLogoStore store, Guid tenantId, string? storageRef, byte[]? legacy, CancellationToken ct) =>
        storageRef is null ? legacy : await store.OpenAsync(tenantId, storageRef, ct);

    /// <summary>Sans magasin (clé absente), seule une photo restée en base se lit encore.</summary>
    public static async Task<byte[]?> AvatarAsync(IUserAvatarStore? store, UserAvatar? avatar, CancellationToken ct)
    {
        if (avatar is null) return null;
        if (avatar.StorageRef is null) return avatar.Content;
        return store is null ? null : await store.OpenAsync(avatar.TenantId, avatar.StorageRef, ct);
    }

    public static async Task<string?> AvatarDataUrlAsync(IUserAvatarStore? store, UserAvatar? avatar, CancellationToken ct) =>
        await AvatarAsync(store, avatar, ct) is { Length: > 0 } bytes ? UserAvatar.ToDataUrl(avatar!.ContentType, bytes) : null;
}

/// <summary>
/// Déplace une fois, au démarrage, les logos et photos encore en base vers le stockage objet. Rejoué
/// à chaque démarrage et sans effet quand il ne reste rien : une ligne n'est vidée qu'une fois son
/// objet écrit, donc un arrêt au milieu laisse au pire un objet orphelin, jamais une image perdue.
/// Les photos ne bougent que si leur magasin chiffré est configuré.
/// </summary>
internal sealed class BrandingObjectMigration(
    IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, ILogger<BrandingObjectMigration> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(() => _ = Task.Run(() => RunAsync(lifetime.ApplicationStopping)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AdministrationDbContext>();
            var logos = scope.ServiceProvider.GetRequiredService<ICompanyLogoStore>();
            var avatars = scope.ServiceProvider.GetService<IUserAvatarStore>();

            var movedLogos = 0;
            foreach (var logo in await db.CompanyLogos.IgnoreQueryFilters().AsTracking()
                         .Where(l => l.StorageRef == null && l.Content != null).ToListAsync(ct))
            {
                logo.MoveToStorage(await logos.StoreAsync(logo.TenantId, logo.Content!, ct));
                await db.SaveChangesAsync(ct);
                movedLogos++;
            }

            var movedAvatars = 0;
            if (avatars is not null)
            {
                foreach (var avatar in await db.UserAvatars.IgnoreQueryFilters().AsTracking()
                             .Where(a => a.StorageRef == null && a.Content != null).ToListAsync(ct))
                {
                    avatar.MoveToStorage(await avatars.StoreAsync(avatar.TenantId, avatar.Content!, ct));
                    await db.SaveChangesAsync(ct);
                    movedAvatars++;
                }
            }

            if (movedLogos + movedAvatars > 0)
                logger.LogInformation(
                    "Moved {Logos} company logo(s) and {Avatars} profile photo(s) from the database to object storage",
                    movedLogos, movedAvatars);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Rien n'est perdu : ce qui n'a pas bougé se lit encore en base, et le prochain démarrage reprend.
            logger.LogError(ex, "Moving company logos and profile photos to object storage failed; retried at next start");
        }
    }
}
