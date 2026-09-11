using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Domain;

public class CompanyInfo
{
    private const string DefaultPrimaryColor = "";
    private const string DefaultSecondaryColor = "";
    
    public Guid Id { get;  private set; }
    public Guid TenantId { get;  private set; }
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string LogoUrl { get; private set; } = string.Empty;
    public bool IsMaintenance { get; private set; }
    public string PrimaryColor { get; private set; } = DefaultPrimaryColor;
    public string SecondaryColor { get; private set; } = DefaultSecondaryColor;
    public string RefCode { get; private set; } = string.Empty;
    public Languages DefaultLanguage { get; private set; } = Languages.Fr;

    private CompanyInfo() { }

    public static CompanyInfo Create(Guid tenantId, string name, string description, string logoUrl)
    {
        return new CompanyInfo
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Description = description,
            LogoUrl = logoUrl,
            RefCode = ""
        };
    }
    
}