namespace Sankore.Modules.Administration.Domain;

public class ProductSpeciality
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; private set; } = string.Empty;
    public string Code { get; private set; } =  string.Empty;
}