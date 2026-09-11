namespace ParkNest.Domain.Common;

/// <summary>
/// Distance between two points on the earth.
///
/// Kept here rather than delegating to PostGIS because this is asked during a check-in, about two
/// points already in hand, and a database round trip to compare a phone's position with a pin it
/// was just given would be a strange way to spend a hundred milliseconds while someone stands next
/// to their car.
/// </summary>
public static class Geo
{
    private const double EarthRadiusMetres = 6_371_000;

    /// <summary>
    /// Great-circle distance in metres.
    ///
    /// A sphere, not the ellipsoid PostGIS uses. The error is a fraction of a percent, and this is
    /// compared against a radius measured in tens of metres to decide whether someone is standing
    /// where they say they are — a tolerance that swamps the difference many times over.
    /// </summary>
    public static double DistanceMetres(double fromLat, double fromLng, double toLat, double toLng)
    {
        var lat1 = ToRadians(fromLat);
        var lat2 = ToRadians(toLat);
        var deltaLat = ToRadians(toLat - fromLat);
        var deltaLng = ToRadians(toLng - fromLng);

        var a = Math.Sin(deltaLat / 2) * Math.Sin(deltaLat / 2)
                + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(deltaLng / 2) * Math.Sin(deltaLng / 2);

        return EarthRadiusMetres * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;
}
