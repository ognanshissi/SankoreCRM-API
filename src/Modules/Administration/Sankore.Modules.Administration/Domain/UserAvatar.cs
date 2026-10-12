using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Domain;

/// <summary>
/// Photo de profil. Dans sa propre table plutôt que sur le profil : le profil est relu à chaque
/// <c>GET users/me</c>, et y traîner jusqu'à 2 Mo d'image à chaque requête serait payer la photo
/// partout pour ne l'afficher qu'à deux endroits.
/// </summary>
public sealed class UserAvatar
{
    public const int MaxSizeBytes = 2 * 1024 * 1024;

    public Guid UserId { get; private set; }
    public Guid TenantId { get; private set; }
    public byte[] Content { get; private set; } = [];
    public string ContentType { get; private set; } = null!;
    public DateTimeOffset UpdatedAt { get; private set; }

    private UserAvatar() { }

    public static UserAvatar Create(Guid tenantId, Guid userId, byte[] content)
    {
        var avatar = new UserAvatar { TenantId = tenantId, UserId = userId };
        avatar.Replace(content);
        return avatar;
    }

    /// <summary>
    /// Le type est lu dans les octets, pas dans l'en-tête de la requête : c'est ce qui sera servi
    /// à d'autres navigateurs, et un en-tête se falsifie.
    /// </summary>
    public void Replace(byte[] content)
    {
        if (content.Length == 0)
            throw new DomainException("Le fichier est vide.", "UserAvatar.Empty");
        if (content.Length > MaxSizeBytes)
            throw new DomainException("La photo ne peut pas dépasser 2 Mo.", "UserAvatar.TooLarge");

        ContentType = DetectContentType(content)
            ?? throw new DomainException("La photo doit être au format JPG ou PNG.", "UserAvatar.UnsupportedType");
        Content = content;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public string ToDataUrl() => $"data:{ContentType};base64,{Convert.ToBase64String(Content)}";

    private static string? DetectContentType(byte[] c)
    {
        if (c.Length >= 3 && c[0] == 0xFF && c[1] == 0xD8 && c[2] == 0xFF) return "image/jpeg";
        if (c.Length >= 8 && c[0] == 0x89 && c[1] == 0x50 && c[2] == 0x4E && c[3] == 0x47
            && c[4] == 0x0D && c[5] == 0x0A && c[6] == 0x1A && c[7] == 0x0A) return "image/png";
        return null;
    }
}
