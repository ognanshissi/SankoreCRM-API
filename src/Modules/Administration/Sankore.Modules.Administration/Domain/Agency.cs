using System.ComponentModel;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Domain;

public sealed class Agency : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid? ParentAgencyId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public AgencyType AgencyType { get; private set; } = AgencyType.HeadQuarter;
    public bool IsHeadQuarterAgency => AgencyType == AgencyType.HeadQuarter;
    public Address? Address { get; private set; }
    public bool IsActive { get; private set; } = true;
    public bool IsDeleted { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }
    public Guid? CreatedBy { get; private set; }

    private readonly List<AppUser> _users = [];
    public IReadOnlyCollection<AppUser> Users => _users.AsReadOnly();

    private Agency() { }

    public static Agency Create(
        Guid tenantId,
        string name,
        string description,
        AgencyType agencyType,
        Guid? parentAgencyId,
        Guid? createdBy,
        Address? address = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Agency name is required.");
        if (agencyType != AgencyType.HeadQuarter && parentAgencyId is null)
            throw new DomainException("Non-headquarters agencies must have a parent agency.");

        return new Agency
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Code = GenerateCode(name),
            Name = name.Trim(),
            Description = description.Trim(),
            AgencyType = agencyType,
            ParentAgencyId = parentAgencyId,
            Address = address,
            IsActive = true,
            IsDeleted = false,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = createdBy,
        };
    }

    public void Update(string name, string description, AgencyType agencyType, Address? address)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Agency name is required.");

        Name = name.Trim();
        Description = description.Trim();
        AgencyType = agencyType;
        Address = address;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Deactivate()
    {
        if (IsDeleted)
            throw new DomainException("Agency is already deleted.");

        IsActive = false;
        IsDeleted = true;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Activate()
    {
        if (!IsDeleted)
            throw new DomainException("Agency is not deactivated.");

        IsActive = true;
        IsDeleted = false;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Moves this agency under a new parent.
    /// Domain invariant only — circular-reference check must be done in the handler.
    /// </summary>
    public void MoveTo(Guid? newParentId)
    {
        if (AgencyType != AgencyType.HeadQuarter && newParentId is null)
            throw new DomainException("Non-headquarters agencies must have a parent agency.");
        if (newParentId == Id)
            throw new DomainException("An agency cannot be its own parent.");

        ParentAgencyId = newParentId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string GenerateCode(string name)
    {
        var clean = new string(name.Where(char.IsLetterOrDigit).ToArray());
        return (clean.Length <= 6 ? clean : clean[..6]).ToUpperInvariant();
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