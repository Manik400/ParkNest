using ParkNest.Domain.Common;

namespace ParkNest.Domain.Listings;

/// <summary>
/// A city the marketplace operates in: where its centre is and how far out "in this city" reaches.
/// </summary>
/// <param name="RadiusKm">
/// Generous on purpose. It exists to catch a pin on the wrong side of the country — a Gurgaon
/// driveway whose host typed the city and then never moved the marker off Bengaluru — not to
/// argue about where a suburb ends.
/// </param>
public sealed record City(
    string Name,
    string State,
    double Latitude,
    double Longitude,
    double RadiusKm,
    string TimeZoneId);

/// <summary>
/// The cities a space can be listed in.
///
/// Code rather than a table, for now: the list changes when the business decides to open a city,
/// which happens by a person choosing to, on a timescale of months, with a pricing band to add and
/// a launch to plan. A row a form can insert would make "we operate in Testville" a typo away.
/// Someone wanting a city that is not here asks for it through <see cref="CityRequest"/>.
/// </summary>
public static class Cities
{
    public static readonly IReadOnlyList<City> Supported = new[]
    {
        new City("Bengaluru", "Karnataka", 12.9716, 77.5946, 60, "Asia/Kolkata"),
        new City("Gurgaon", "Haryana", 28.4595, 77.0266, 45, "Asia/Kolkata"),
        new City("Mumbai", "Maharashtra", 19.0760, 72.8777, 60, "Asia/Kolkata"),
        new City("Delhi", "Delhi", 28.6139, 77.2090, 60, "Asia/Kolkata"),
        new City("Hyderabad", "Telangana", 17.3850, 78.4867, 60, "Asia/Kolkata"),
        new City("Pune", "Maharashtra", 18.5204, 73.8567, 50, "Asia/Kolkata"),
    };

    /// <summary>The catalogue entry, matched without regard to case or surrounding space.</summary>
    public static City? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var wanted = name.Trim();
        return Supported.FirstOrDefault(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a point is close enough to the city's centre to be called part of it.</summary>
    public static bool Contains(this City city, double latitude, double longitude) =>
        Geo.DistanceMetres(city.Latitude, city.Longitude, latitude, longitude) <= city.RadiusKm * 1000;
}

/// <summary>
/// Somebody asked for a city the marketplace is not in yet. A row per ask, so the operator can see
/// which city is asked for most rather than only that somebody, once, wanted one.
/// </summary>
public class CityRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>As typed, trimmed. Not matched against anything — the whole point is that it is new.</summary>
    public string City { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    /// <summary>Optional: an area, a reason, a "there are 300 flats and no parking".</summary>
    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
