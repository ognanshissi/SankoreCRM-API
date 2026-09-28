namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Per tenant + agency + year counter behind the client number.
/// Concurrent allocations are serialized by the <see cref="Version"/> (xmin) concurrency token:
/// the losing transaction retries rather than handing out a duplicate number.
/// </summary>
public sealed class ClientNumberSequence
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string AgencyCode { get; private set; } = default!;
    public int Year { get; private set; }

    /// <summary>The number the next client of this agency and year will get.</summary>
    public int NextValue { get; private set; }

    /// <summary>PostgreSQL <c>xmin</c>, mapped as the optimistic concurrency token.</summary>
    public uint Version { get; private set; }

    private ClientNumberSequence() { } // EF Core

    internal static ClientNumberSequence Start(Guid tenantId, string agencyCode, int year)
    {
        if (string.IsNullOrWhiteSpace(agencyCode))
            throw new DomainException("Agency code is required.", "ClientNumberSequence.AgencyCode.Required");
        if (year < 2000)
            throw new DomainException("Year is out of range.", "ClientNumberSequence.Year.Invalid");

        return new ClientNumberSequence
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AgencyCode = agencyCode.Trim(),
            Year = year,
            NextValue = 1,
        };
    }

    /// <summary>Returns the current value and moves the counter forward.</summary>
    internal int Take()
    {
        var taken = NextValue;
        NextValue = taken + 1;
        return taken;
    }
}
