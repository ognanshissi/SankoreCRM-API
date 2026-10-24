using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Domain;

public class CompanyInfo
{
    private const string DefaultPrimaryColor = "#1A4D4DFF";
    private const string DefaultSecondaryColor = "#103456FF";
    
    public Guid Id { get;  private set; }
    public Guid TenantId { get;  private set; }
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string LogoUrl { get; private set; } = string.Empty;
    public bool IsMaintenance { get; private set; }
    public string PrimaryColor { get; private set; } = DefaultPrimaryColor;
    public string SecondaryColor { get; private set; } = DefaultSecondaryColor;
    public Languages DefaultLanguage { get; private set; } = Languages.Fr;

    /// <summary>ISO 4217 code of every amount typed in this institution — see <see cref="InstitutionCurrencies"/>.</summary>
    public string Currency { get; private set; } = InstitutionCurrencies.Default;

    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    private CompanyInfo() { }

    public static CompanyInfo Create(Guid tenantId, string name, string description)
    {
        return new CompanyInfo
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Description = description,
            LogoUrl = "default.png",
        };
    }

    /// <summary>Pointe le logo vers l'image déposée, ou le retire. Les autres réglages ne bougent pas.</summary>
    public void SetLogoUrl(string logoUrl)
    {
        LogoUrl = logoUrl;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Update(
        string name,
        string description,
        string logoUrl,
        string primaryColor,
        string secondaryColor,
        Languages defaultLanguage,
        string? currency = null)
    {
        Name = name;
        Description = description;
        LogoUrl = logoUrl;
        PrimaryColor = primaryColor;
        SecondaryColor = secondaryColor;
        DefaultLanguage = defaultLanguage;
        // Absent from an older client's request: the institution keeps its currency.
        if (currency is not null) Currency = currency.Trim().ToUpperInvariant();
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}