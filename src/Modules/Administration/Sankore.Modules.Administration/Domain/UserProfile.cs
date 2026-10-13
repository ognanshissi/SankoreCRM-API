using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Domain;

public class UserProfile
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public AppUser? User { get; private set; }
    public string DefaultLanguage { get; private set; } = LanguageCode.Default;

    public Address Address { get; private set; } = null!;

    public PhoneNumber WorkNumber { get; private set; } = null!;

    public PhoneNumber HomeNumber { get; private set; } = null!;

    public PhoneNumber PersonalNumber { get; private set; } = null!;

    public string JobTitle { get; private set; } = null!; // The job title is different from the role. It is a string that describes the user's position in the company.
    
    public DateTimeOffset? BirthDate { get; private set; }
    
    public string AdditionalEmail  {get; private set; } = string.Empty;

    // ── Réglé par l'utilisateur lui-même (onglet Informations) ───────────
    public string? WhatsAppNumber { get; private set; }

    /// <summary>Ajoutée en fin des messages que l'agent envoie aux leads et clients.</summary>
    public string? Signature { get; private set; }

    /// <summary>
    /// Fuseau IANA. C'est lui qui situe les horaires de travail de l'agent pour le dispatching :
    /// « 8 h » à Cotonou n'est pas « 8 h » à Abidjan.
    /// </summary>
    public string TimeZone { get; private set; } = DefaultTimeZone;

    public AmountFormat AmountFormat { get; private set; } = AmountFormat.SpaceGrouped;

    public DateTimeOffset? UpdatedAt { get; private set; }

    public const string DefaultTimeZone = "Africa/Abidjan";
    public const int MaxSignatureLength = 500;

    private UserProfile() { }

    public static UserProfile Create(Guid tenantId, Guid userId, string defaultLanguage = "fr")
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            DefaultLanguage = LanguageCode.Normalize(defaultLanguage),
            Address = new Address(),
            AdditionalEmail = string.Empty,
            JobTitle = string.Empty
        };

    public void UpdateBySelf(
        string? workPhone, string? whatsApp, string? signature,
        string language, string timeZone, AmountFormat amountFormat)
    {
        if (signature?.Length > MaxSignatureLength)
            throw new DomainException(
                $"La signature ne peut pas dépasser {MaxSignatureLength} caractères.", "UserProfile.Signature.TooLong");
        if (!TryResolveTimeZone(timeZone, out _))
            throw new DomainException($"Fuseau horaire inconnu : {timeZone}.", "UserProfile.TimeZone.Unknown");

        // Un numéro effacé redevient « pas de numéro », comme sur un profil jamais renseigné.
        // Le même numéro garde sa confirmation éventuelle.
        var phone = Blank(workPhone);
        if (phone != WorkNumber?.Contact)
            WorkNumber = phone is null ? null! : PhoneNumber.Create(phone);
        WhatsAppNumber = Blank(whatsApp);
        Signature = Blank(signature);
        DefaultLanguage = LanguageCode.Normalize(language);
        TimeZone = timeZone;
        AmountFormat = amountFormat;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Le téléphone professionnel, réglé par l'administrateur. Blanc l'efface.</summary>
    public void SetWorkPhone(string? workPhone)
    {
        var phone = Blank(workPhone);
        if (phone != WorkNumber?.Contact)
            WorkNumber = phone is null ? null! : PhoneNumber.Create(phone);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Le fuseau de l'agent, ou UTC s'il est illisible — une ligne corrompue ne doit pas sortir
    /// l'agent du dispatching.
    /// </summary>
    public static TimeZoneInfo ResolveTimeZone(string? id)
        => TryResolveTimeZone(id, out var tz) ? tz : TimeZoneInfo.Utc;

    private static bool TryResolveTimeZone(string? id, out TimeZoneInfo tz)
    {
        tz = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id)) return false;
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out tz!);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Présentation des montants. Le code de devise suit toujours le nombre.</summary>
public enum AmountFormat
{
    /// <summary>250 000 FCFA</summary>
    SpaceGrouped,

    /// <summary>250.000 FCFA</summary>
    DotGrouped,

    /// <summary>250,000 FCFA</summary>
    CommaGrouped,
}
