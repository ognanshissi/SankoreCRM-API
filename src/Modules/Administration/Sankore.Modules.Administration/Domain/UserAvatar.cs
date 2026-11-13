using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Domain;

/// <summary>
/// Photo de profil. Dans sa propre table plutôt que sur le profil : le profil est relu à chaque
/// <c>GET users/me</c>, et y traîner jusqu'à 2 Mo d'image à chaque requête serait payer la photo
/// partout pour ne l'afficher qu'à deux endroits.
///
/// <para>
/// Les octets sont chiffrés dans le stockage objet (bucket <c>user-avatars</c>, 2026-11-13) : la
/// ligne ne garde que la référence. <see cref="Content"/> ne sert plus qu'aux photos déposées avant,
/// jusqu'à ce que <c>BrandingObjectMigration</c> les ait déplacées.
/// </para>
/// </summary>
public sealed class UserAvatar
{
    public const int MaxSizeBytes = 2 * 1024 * 1024;

    public Guid UserId { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Ancien stockage en base. Null une fois la photo dans le stockage objet.</summary>
    public byte[]? Content { get; private set; }

    /// <summary>Référence dans le stockage objet. Null pour une photo pas encore déplacée.</summary>
    public string? StorageRef { get; private set; }

    public string ContentType { get; private set; } = null!;
    public DateTimeOffset UpdatedAt { get; private set; }

    private UserAvatar() { }

    public static UserAvatar Create(Guid tenantId, Guid userId, string storageRef, string contentType)
    {
        var avatar = new UserAvatar { TenantId = tenantId, UserId = userId };
        avatar.Replace(storageRef, contentType);
        return avatar;
    }

    public void Replace(string storageRef, string contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        StorageRef = storageRef;
        ContentType = contentType;
        Content = null;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void MoveToStorage(string storageRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRef);
        StorageRef = storageRef;
        Content = null;
    }

    /// <summary>
    /// Le type est lu dans les octets, pas dans l'en-tête de la requête : c'est ce qui sera servi
    /// à d'autres navigateurs, et un en-tête se falsifie.
    /// </summary>
    public static string ContentTypeOf(byte[] content)
    {
        if (content.Length == 0)
            throw new DomainException("Le fichier est vide.", "UserAvatar.Empty");
        if (content.Length > MaxSizeBytes)
            throw new DomainException("La photo ne peut pas dépasser 2 Mo.", "UserAvatar.TooLarge");

        return DetectContentType(content)
            ?? throw new DomainException("La photo doit être au format JPG ou PNG.", "UserAvatar.UnsupportedType");
    }

    public static string ToDataUrl(string contentType, byte[] content) =>
        $"data:{contentType};base64,{Convert.ToBase64String(content)}";

    private static string? DetectContentType(byte[] c)
    {
        if (c.Length >= 3 && c[0] == 0xFF && c[1] == 0xD8 && c[2] == 0xFF) return "image/jpeg";
        if (c.Length >= 8 && c[0] == 0x89 && c[1] == 0x50 && c[2] == 0x4E && c[3] == 0x47
            && c[4] == 0x0D && c[5] == 0x0A && c[6] == 0x1A && c[7] == 0x0A) return "image/png";
        return null;
    }
}
