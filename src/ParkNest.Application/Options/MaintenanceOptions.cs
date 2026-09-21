namespace ParkNest.Application.Options;

public sealed class MaintenanceOptions
{
    public const string SectionName = "Maintenance";

    /// <summary>
    /// Whether an admin may wipe the operational data from the console.
    ///
    /// Off unless switched on, and the API refuses to start with it on in Production. It exists
    /// for a pilot on a free database tier, where the test bookings of the last month are worth
    /// less than the space they take up — not for a platform with a single real rupee in it.
    /// </summary>
    public bool AllowDataReset { get; set; }
}
