namespace Sankore.Shared.Kernel;

/// <summary>
/// Immutable geographic coordinate value object, shared across modules
/// (Leads, Users, application terrain agent, etc.). EF Core maps it as an
/// owned type / complex type — see each module's EntityTypeConfiguration.
/// </summary>
public record GeoPoint(double Latitude, double Longitude)
{
    /// <summary>
    /// Great-circle distance in kilometers between this point and another,
    /// using the Haversine formula. Used by CompatibilityScorer for
    /// geographic proximity scoring in the lead dispatching engine.
    /// </summary>
    public double DistanceKmTo(GeoPoint other)
    {
        const double earthRadiusKm = 6371.0;

        var dLat = ToRadians(other.Latitude - Latitude);
        var dLon = ToRadians(other.Longitude - Longitude);

        var lat1 = ToRadians(Latitude);
        var lat2 = ToRadians(other.Latitude);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2) * Math.Cos(lat1) * Math.Cos(lat2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return earthRadiusKm * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
