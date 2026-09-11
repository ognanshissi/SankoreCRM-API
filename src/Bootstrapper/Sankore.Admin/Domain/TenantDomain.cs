namespace Sankore.Admin.Domain;

public class TenantDomain
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    public string Fqdn { get; private set; } = string.Empty;
    public bool IsPrimary { get; private set; }
    public bool IsActive { get; private set; }

    /// <summary>Inclusive start of the window during which this domain is valid.</summary>
    public DateTimeOffset ValidFrom { get; private set; }

    //
    /// <summary>Exclusive end of the validity window. Null means no expiry.</summary>
    public DateTimeOffset? ValidTo { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private TenantDomain() { }

    public static TenantDomain Create(
        Guid tenantId,
        string fqdn,
        bool isPrimary,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validTo = null)
    {
        return new TenantDomain
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Fqdn = fqdn.ToLowerInvariant(),
            IsPrimary = isPrimary,
            IsActive = true,
            ValidFrom = validFrom ?? DateTimeOffset.UtcNow,
            ValidTo = validTo,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public void Deactivate()
    {
        IsActive = false;
        ValidTo = DateTimeOffset.UtcNow;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void SetPrimary()
    {
        IsPrimary = true;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void UnsetPrimary()
    {
        IsPrimary = false;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void SetValidTo(DateTimeOffset? validTo)
    {
        ValidTo = validTo;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Returns true when this domain entry is currently in its valid window and active.
    /// </summary>
    public bool IsEffective(DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        return IsActive
            && ValidFrom <= now
            && (ValidTo is null || ValidTo > now);
    }
}
