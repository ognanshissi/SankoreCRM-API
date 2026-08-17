using System.ComponentModel;
using NetTopologySuite.Geometries;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Users.Domain;

public class Agency: AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid? ParentAgencyId { get; private set; }

    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = String.Empty;
    public string Description { get; private set; } =  String.Empty;
    public Address? Address { get; private set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public bool IsDeleted { get; set; }
    public bool IsActive { get; set; }

    private readonly List<AppUser> _users = new List<AppUser>();
    public IReadOnlyCollection<AppUser> Users => _users.AsReadOnly();
    
    public AgencyType AgencyType { get; private set; } = AgencyType.HeadQuarter;

    private Agency() { }

    public static Agency Create(Guid tenantId, string name, string description)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("The name of the agency is required");

        
        return new Agency()
        {
            Id =  Guid.NewGuid(),
            Code = GenerateCode(name),
            TenantId = tenantId,
            Name = name,
            Description = description
        };
    }

    private static string GenerateCode(string name)
    {
        return name;
    }
}

public enum AgencyType
{
    [Description("Head Quarter")]
    HeadQuarter,
    [Description("Branch")]
    Branch,
    [Description("Service Point")]
    ServicePoint,
    [Description("Counter")]
    Counter
}