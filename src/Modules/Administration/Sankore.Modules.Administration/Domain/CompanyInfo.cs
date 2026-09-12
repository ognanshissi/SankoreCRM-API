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

    public void Update(
        string name,
        string description,
        string logoUrl,
        string primaryColor,
        string secondaryColor,
        Languages defaultLanguage)
    {
        Name = name;
        Description = description;
        LogoUrl = logoUrl;
        PrimaryColor = primaryColor;
        SecondaryColor = secondaryColor;
        DefaultLanguage = defaultLanguage;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}