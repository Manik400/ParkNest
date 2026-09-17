using FluentAssertions;
using ParkNest.Application.Bookings;
using ParkNest.Application.Listings;
using ParkNest.Domain.Common;
using ParkNest.Domain.Listings;

namespace ParkNest.UnitTests;

/// <summary>
/// Availability was modelled and persisted from the start but never checked when booking, so a
/// space offered 09:00–17:00 could be booked at 3am. These cover the enforcement.
/// </summary>
public sealed class BookingAvailabilityTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    // Origin is Tuesday 2026-07-28 09:00 UTC and the test harness lists spaces in UTC.
    private static readonly DateTimeOffset TuesdayMorning = TestHarness.Origin;

    [Fact]
    public async Task A_booking_outside_the_published_hours_is_refused()
    {
        var (spaceId, vehicleId) = await SeedAsync(new[]
        {
            new AvailabilityWindowRequest(DayOfWeek.Tuesday, new TimeOnly(9, 0), new TimeOnly(17, 0))
        });

        // 22:00, long after closing.
        var act = () => _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            spaceId, vehicleId, TuesdayMorning.AddHours(13), 60, "bk-late"));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*outside the hours*");
    }

    [Fact]
    public async Task A_booking_inside_the_published_hours_is_accepted()
    {
        var (spaceId, vehicleId) = await SeedAsync(new[]
        {
            new AvailabilityWindowRequest(DayOfWeek.Tuesday, new TimeOnly(9, 0), new TimeOnly(17, 0))
        });

        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            spaceId, vehicleId, TuesdayMorning.AddHours(1), 60, "bk-ok"));

        booking.Status.Should().Be(BookingStatus.Held);
    }

    [Fact]
    public async Task A_booking_that_would_run_past_closing_time_is_refused()
    {
        var (spaceId, vehicleId) = await SeedAsync(new[]
        {
            new AvailabilityWindowRequest(DayOfWeek.Tuesday, new TimeOnly(9, 0), new TimeOnly(17, 0))
        });

        // Starts at 16:00 inside the window, but runs two hours past closing.
        var act = () => _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            spaceId, vehicleId, TuesdayMorning.AddHours(7), 120, "bk-overrun"));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*outside the hours*");
    }

    [Fact]
    public async Task A_booking_on_a_day_the_host_is_closed_is_refused()
    {
        var (spaceId, vehicleId) = await SeedAsync(new[]
        {
            new AvailabilityWindowRequest(DayOfWeek.Sunday, new TimeOnly(0, 0), new TimeOnly(0, 0))
        });

        var act = () => _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            spaceId, vehicleId, TuesdayMorning.AddHours(1), 60, "bk-wrongday"));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*outside the hours*");
    }

    [Fact]
    public async Task A_blackout_blocks_an_otherwise_available_window()
    {
        var (spaceId, vehicleId) = await SeedAsync(null);

        _h.Db.AvailabilityBlackouts.Add(new AvailabilityBlackout
        {
            ParkingSpaceId = spaceId,
            From = TuesdayMorning,
            To = TuesdayMorning.AddHours(6),
            Reason = "Building work"
        });
        await _h.Db.SaveChangesAsync();

        var act = () => _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            spaceId, vehicleId, TuesdayMorning.AddHours(1), 60, "bk-blackout"));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*blocked out*");
    }

    [Fact]
    public async Task A_booking_after_the_blackout_ends_is_accepted()
    {
        var (spaceId, vehicleId) = await SeedAsync(null);

        _h.Db.AvailabilityBlackouts.Add(new AvailabilityBlackout
        {
            ParkingSpaceId = spaceId,
            From = TuesdayMorning,
            To = TuesdayMorning.AddHours(2)
        });
        await _h.Db.SaveChangesAsync();

        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            spaceId, vehicleId, TuesdayMorning.AddHours(3), 60, "bk-after"));

        booking.Status.Should().Be(BookingStatus.Held);
    }

    [Fact]
    public async Task A_listing_with_no_availability_cannot_be_published()
    {
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync();

        var draft = await _h.Listings.CreateDraftAsync(new CreateListingRequest(
            "Driveway", "12 Main Rd", "Bengaluru", null, 12.97, 77.59, 60m,
            new[] { VehicleType.FourWheeler }));

        var act = () => _h.Listings.PublishAsync(draft.Id);

        await act.Should().ThrowAsync<DomainException>().WithMessage("*availability window*");
    }

    [Fact]
    public async Task A_booking_cannot_start_in_the_past()
    {
        var (spaceId, vehicleId) = await SeedAsync(null);

        var act = () => _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            spaceId, vehicleId, TuesdayMorning.AddHours(-3), 60, "bk-past"));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*in the past*");
    }

    [Fact]
    public async Task An_invalid_time_zone_on_a_listing_is_reported_rather_than_ignored()
    {
        var (spaceId, vehicleId) = await SeedAsync(null);

        var space = await _h.Db.ParkingSpaces.FindAsync(spaceId);
        space!.TimeZoneId = "Mars/Olympus_Mons";
        await _h.Db.SaveChangesAsync();

        var act = () => _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            spaceId, vehicleId, TuesdayMorning.AddHours(1), 60, "bk-tz"));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*invalid time zone*");
    }

    private async Task<(Guid spaceId, Guid vehicleId)> SeedAsync(
        IReadOnlyList<AvailabilityWindowRequest>? windows)
    {
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync();
        var space = await _h.AddPublishedSpaceAsync(host.Id, availabilityWindows: windows);

        var renter = await _h.AddUserAsync(UserRole.Both);
        var vehicle = await _h.AddVehicleAsync(renter.Id);
        await _h.Wallets.RechargeAsync(renter.Id, 2000m, $"recharge:{renter.Id}");

        _h.CurrentUser.SignIn(renter.Id);

        return (space.Id, vehicle.Id);
    }
}
