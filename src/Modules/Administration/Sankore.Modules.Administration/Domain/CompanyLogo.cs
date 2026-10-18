using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Domain;

/// <summary>
/// Le logo déposé par l'institution. Dans sa propre table, comme <see cref="UserAvatar"/> :
/// <c>company_info</c> est relue à chaque démarrage du front (contexte du tenant), et y traîner
/// l'image ferait payer ses octets à chaque chargement pour un fichier que le navigateur garde en
/// cache.
///
/// <para>
/// Servi par une route publique (<c>public/branding/{tenantId}/logo</c>) et non en data URL :
/// <see cref="CompanyInfo.LogoUrl"/> est aussi l'image des e-mails, et Gmail comme Outlook
/// bloquent les images <c>data:</c>.
/// </para>
/// </summary>
public sealed class CompanyLogo
{
    /// <summary>Un logo, pas une photo : 1 Mo suffit largement et borne ce que la route publique sert.</summary>
    public const int MaxSizeBytes = 1024 * 1024;

    public Guid TenantId { get; private set; }
    public byte[] Content { get; private set; } = [];
    public string ContentType { get; private set; } = null!;
    public DateTimeOffset UpdatedAt { get; private set; }

    private CompanyLogo() { }

    public static CompanyLogo Create(Guid tenantId, byte[] content)
    {
        var logo = new CompanyLogo { TenantId = tenantId };
        logo.Replace(content);
        return logo;
    }

    /// <summary>
    /// Le type est lu dans les octets, jamais dans l'en-tête de la requête : la route publique le
    /// renvoie tel quel à des navigateurs tiers, et un en-tête se falsifie. Pas de SVG, pour la
    /// même raison — c'est un document qui peut porter du script, servi depuis l'origine de l'API.
    /// </summary>
    public void Replace(byte[] content)
    {
        if (content.Length == 0)
            throw new DomainException("Le fichier est vide.", "CompanyLogo.Empty");
        if (content.Length > MaxSizeBytes)
            throw new DomainException("Le logo ne peut pas dépasser 1 Mo.", "CompanyLogo.TooLarge");

        ContentType = DetectContentType(content)
            ?? throw new DomainException("Le logo doit être au format PNG, JPG ou WebP.", "CompanyLogo.UnsupportedType");
        Content = content;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string? DetectContentType(byte[] c)
    {
        if (c.Length >= 3 && c[0] == 0xFF && c[1] == 0xD8 && c[2] == 0xFF) return "image/jpeg";
        if (c.Length >= 8 && c[0] == 0x89 && c[1] == 0x50 && c[2] == 0x4E && c[3] == 0x47
            && c[4] == 0x0D && c[5] == 0x0A && c[6] == 0x1A && c[7] == 0x0A) return "image/png";
        // RIFF....WEBP
        if (c.Length >= 12 && c[0] == 0x52 && c[1] == 0x49 && c[2] == 0x46 && c[3] == 0x46
            && c[8] == 0x57 && c[9] == 0x45 && c[10] == 0x42 && c[11] == 0x50) return "image/webp";
        return null;
    }
}
