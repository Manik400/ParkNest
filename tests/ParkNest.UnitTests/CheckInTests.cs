using FluentAssertions;
using ParkNest.Application.Bookings;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

/// <summary>
/// Tier 2 check-in: the code from the sticker at the space, corroborated by where the phone says
/// it is. Neither half proves anything alone, and the tests below are mostly about the service
/// declining to accept one without the other.
/// </summary>
public sealed class CheckInTests : IDisposable
{
    /// <summary>The space's pin. Bengaluru, matching the rest of the suite.</summary>
    private const double SpaceLat = 12.9716;
    private const double SpaceLng = 77.5946;

    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task<(Guid BookingId, Guid SpaceId, Guid HostId, Guid RenterId)> SetupAsync()
    {
        var renter = await _h.AddUserAsync(UserRole.Renter);
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync();

        var space = await _h.AddPublishedSpaceAsync(host.Id, 60m);
        space.Latitude = SpaceLat;
        space.Longitude = SpaceLng;
        await _h.Db.SaveChangesAsync();

        var vehicle = await _h.AddVehicleAsync(renter.Id);
        await _h.Wallets.RechargeAsync(renter.Id, 1000m, $"recharge:{renter.Id}");

        _h.CurrentUser.SignIn(renter.Id);

        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            space.Id, vehicle.Id, TestHarness.Origin, 60, $"bk-{Guid.NewGuid()}"));

        return (booking.Id, space.Id, host.Id, renter.Id);
    }

    /// <summary>Mints the code as the host would, then hands the session back to the renter.</summary>
    private async Task<string> IssueCodeAsync(Guid spaceId, Guid hostId, Guid renterId)
    {
        _h.CurrentUser.SignIn(hostId);
        var code = await _h.Listings.GetOrCreateCheckInCodeAsync(spaceId);
        _h.CurrentUser.SignIn(renterId);
        return code.Token;
    }

    /// <summary>A point roughly <paramref name="metresNorth"/> from the pin.</summary>
    private static CheckInProof At(string token, double metresNorth) =>
        new(token, SpaceLat + metresNorth / 111_320d, SpaceLng);

    [Fact]
    public async Task Scanning_the_code_at_the_space_starts_the_session()
    {
        var (bookingId, spaceId, hostId, renterId) = await SetupAsync();
        var token = await IssueCodeAsync(spaceId, hostId, renterId);

        var booking = await _h.Bookings.StartSessionAsync(
            bookingId, DetectionMethod.QrGeofence, TestHarness.Origin, At(token, 20));

        booking.Status.Should().Be(BookingStatus.Active);
        booking.StartDetectionMethod.Should().Be(DetectionMethod.QrGeofence);
    }

    [Fact]
    public async Task A_scan_from_far_away_is_refused()
    {
        // The geofence half. Someone with a photograph of the sticker, checking in from home.
        var (bookingId, spaceId, hostId, renterId) = await SetupAsync();
        var token = await IssueCodeAsync(spaceId, hostId, renterId);

        var act = () => _h.Bookings.StartSessionAsync(
            bookingId, DetectionMethod.QrGeofence, TestHarness.Origin, At(token, 5_000));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*from the space*");
    }

    [Fact]
    public async Task A_wrong_code_from_the_right_place_is_refused()
    {
        // The other half. Standing at the space is not the same as having scanned its code, and
        // a code from a different space must not work here.
        var (bookingId, spaceId, hostId, renterId) = await SetupAsync();
        await IssueCodeAsync(spaceId, hostId, renterId);

        var act = () => _h.Bookings.StartSessionAsync(
            bookingId, DetectionMethod.QrGeofence, TestHarness.Origin, At("not-the-code", 10));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*does not belong to this space*");
    }

    [Fact]
    public async Task A_scan_with_no_location_is_refused()
    {
        var (bookingId, spaceId, hostId, renterId) = await SetupAsync();
        await IssueCodeAsync(spaceId, hostId, renterId);

        var act = () => _h.Bookings.StartSessionAsync(
            bookingId, DetectionMethod.QrGeofence, TestHarness.Origin);

        await act.Should().ThrowAsync<DomainException>().WithMessage("*code and your location*");
    }

    [Fact]
    public async Task A_space_with_no_code_says_so_rather_than_failing_obscurely()
    {
        var (bookingId, _, _, _) = await SetupAsync();

        var act = () => _h.Bookings.StartSessionAsync(
            bookingId, DetectionMethod.QrGeofence, TestHarness.Origin, At("anything", 10));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*does not have a check-in code*");
    }

    [Fact]
    public async Task Rotating_the_code_stops_the_old_one_working()
    {
        var (bookingId, spaceId, hostId, renterId) = await SetupAsync();
        var original = await IssueCodeAsync(spaceId, hostId, renterId);

        _h.CurrentUser.SignIn(hostId);
        var rotated = await _h.Listings.RotateCheckInCodeAsync(spaceId);
        _h.CurrentUser.SignIn(renterId);

        rotated.Token.Should().NotBe(original);

        var act = () => _h.Bookings.StartSessionAsync(
            bookingId, DetectionMethod.QrGeofence, TestHarness.Origin, At(original, 10));

        await act.Should().ThrowAsync<DomainException>();

        // The new one works in its place.
        var booking = await _h.Bookings.StartSessionAsync(
            bookingId, DetectionMethod.QrGeofence, TestHarness.Origin, At(rotated.Token, 10));

        booking.Status.Should().Be(BookingStatus.Active);
    }

    [Fact]
    public async Task Asking_twice_returns_the_same_code()
    {
        // The sticker on the wall must not stop working because the host opened the screen again.
        var (_, spaceId, hostId, _) = await SetupAsync();

        _h.CurrentUser.SignIn(hostId);
        var first = await _h.Listings.GetOrCreateCheckInCodeAsync(spaceId);
        var second = await _h.Listings.GetOrCreateCheckInCodeAsync(spaceId);

        second.Token.Should().Be(first.Token);
    }

    [Fact]
    public async Task A_renter_cannot_read_the_code_for_a_space_they_are_booked_into()
    {
        // Fetching it would defeat the whole mechanism: the code is evidence of having been there.
        var (_, spaceId, _, renterId) = await SetupAsync();
        _h.CurrentUser.SignIn(renterId);

        var act = () => _h.Listings.GetOrCreateCheckInCodeAsync(spaceId);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task A_renter_cannot_claim_an_admin_override()
    {
        // The detection method is the audit field a human reads when settling a dispute. If a
        // client could assert any value, the strongest evidence would be the cheapest to fake.
        var (bookingId, _, _, _) = await SetupAsync();

        var act = () => _h.Bookings.StartSessionAsync(
            bookingId, DetectionMethod.AdminOverride, TestHarness.Origin);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Sensor_detection_cannot_be_claimed_by_a_phone()
    {
        var (bookingId, _, _, _) = await SetupAsync();

        var act = () => _h.Bookings.StartSessionAsync(
            bookingId, DetectionMethod.AnprSensor, TestHarness.Origin);

        await act.Should().ThrowAsync<DomainException>().WithMessage("*not available*");
    }

    [Fact]
    public async Task Checking_out_by_scan_is_verified_the_same_way()
    {
        var (bookingId, spaceId, hostId, renterId) = await SetupAsync();
        var token = await IssueCodeAsync(spaceId, hostId, renterId);

        await _h.Bookings.StartSessionAsync(
            bookingId, DetectionMethod.QrGeofence, TestHarness.Origin, At(token, 10));

        _h.Clock.Advance(TimeSpan.FromMinutes(30));

        var act = () => _h.Bookings.EndSessionAsync(
            bookingId, DetectionMethod.QrGeofence, _h.Clock.UtcNow, At(token, 9_000));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*from the space*");

        var outcome = await _h.Bookings.EndSessionAsync(
            bookingId, DetectionMethod.QrGeofence, _h.Clock.UtcNow, At(token, 10));

        outcome.Booking.EndDetectionMethod.Should().Be(DetectionMethod.QrGeofence);
    }

    [Fact]
    public async Task Tier_1_still_works_without_any_of_this()
    {
        // Tier 2 is opt-in per space. A host who never prints a sticker must be unaffected.
        var (bookingId, _, _, _) = await SetupAsync();

        var booking = await _h.Bookings.StartSessionAsync(
            bookingId, DetectionMethod.AppConfirmed, TestHarness.Origin);

        booking.Status.Should().Be(BookingStatus.Active);
        booking.StartDetectionMethod.Should().Be(DetectionMethod.AppConfirmed);
    }
}

public sealed class GeoTests
{
    [Fact]
    public void Distance_between_a_point_and_itself_is_zero()
    {
        Geo.DistanceMetres(12.9716, 77.5946, 12.9716, 77.5946).Should().Be(0);
    }

    [Fact]
    public void A_degree_of_latitude_is_about_111_kilometres()
    {
        Geo.DistanceMetres(12.9716, 77.5946, 13.9716, 77.5946)
            .Should().BeApproximately(111_195, 500);
    }

    [Fact]
    public void Distance_does_not_depend_on_which_point_comes_first()
    {
        var there = Geo.DistanceMetres(12.9716, 77.5946, 12.9800, 77.6000);
        var back = Geo.DistanceMetres(12.9800, 77.6000, 12.9716, 77.5946);

        there.Should().BeApproximately(back, 0.0001);
    }

    [Fact]
    public void Longitude_degrees_are_shorter_away_from_the_equator()
    {
        // Guards against the classic bug of treating latitude and longitude as interchangeable
        // offsets, which would put a geofence check kilometres out at this latitude.
        var alongLatitude = Geo.DistanceMetres(12.9716, 77.5946, 13.9716, 77.5946);
        var alongLongitude = Geo.DistanceMetres(12.9716, 77.5946, 12.9716, 78.5946);

        alongLongitude.Should().BeLessThan(alongLatitude);
    }
}
