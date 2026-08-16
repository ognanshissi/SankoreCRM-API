using Sankore.Shared.Kernel.ValueObject;

namespace Sankore.Shared.Kernel;

public class Address
{
    public string Street { get; private set; } = string.Empty;
    public string City { get; private set; } = string.Empty;
    public string State { get; private set; } = string.Empty;
    public string Country { get; private set; } = string.Empty;
    public string ZipCode { get; private set; } = string.Empty;

    /// <summary>
    /// Optional geographic coordinates of this address.
    /// Mapped by each module as two double columns (lat/lng) — no PostGIS required.
    /// </summary>
    public GeoPoint? Location { get; private set; }

    public Address() { }
}
