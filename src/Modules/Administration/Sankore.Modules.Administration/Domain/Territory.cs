using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

namespace Sankore.Modules.Administration.Domain;

/// <summary>
/// A Territory is a named geographic zone managed within a tenant.
/// Agents can be scoped to territories for lead dispatching (M13).
/// </summary>
public sealed class Territory
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = null!;
    /// <summary>Short uppercase identifier, unique within tenant (e.g. "DKR-NORD").</summary>
    public string Code { get; private set; } = null!;
    public string Description { get; private set; } = string.Empty;
    public GeoPoint? Location { get; private set; }
    public double RayonKm { get; private set; }
    public List<string> ProductSpecialities { get; private set; } = [];
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    private readonly List<Agency> _agencies = [];
    public IReadOnlyCollection<Agency> Agencies => _agencies.AsReadOnly();

    private Territory() { }

    public static Territory Create(
        Guid tenantId,
        string name,
        string code,
        string description,
        GeoPoint? location,
        double rayonKm,
        List<string> productSpecialities)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Territory name is required.");
        if (string.IsNullOrWhiteSpace(code))
            throw new DomainException("Territory code is required.");

        return new Territory
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name.Trim(),
            Code = code.Trim().ToUpperInvariant(),
            Description = description.Trim(),
            Location = location,
            RayonKm = Math.Max(0, rayonKm),
            ProductSpecialities = productSpecialities,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    public void Update(
        string name,
        string description,
        GeoPoint? location,
        double rayonKm,
        List<string> productSpecialities)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Territory name is required.");

        Name = name.Trim();
        Description = description.Trim();
        Location = location;
        RayonKm = Math.Max(0, rayonKm);
        ProductSpecialities = productSpecialities;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Deactivate()
    {
        IsActive = false;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}