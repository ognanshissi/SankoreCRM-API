using MassTransit;

namespace Sankore.Modules.Administration.Domain;

public sealed class ProductSpeciality
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    
    public string? BusinessProductId { get; private set; }  // Identifiant CBS
    
    public string BusinessPlatformName { get; private set; } = string.Empty;

    private ProductSpeciality() { }

    public static ProductSpeciality Create(Guid tenantId, string name, string code, string? description) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Code = code.ToUpperInvariant(),
            Description = description
        };

    public void Update(string name, string? description, string? businessProductId, string? businessPlatformName)
    {
        Name = name;
        Description = description;
        BusinessProductId = businessProductId;
        BusinessPlatformName = string.Empty;
    }
}