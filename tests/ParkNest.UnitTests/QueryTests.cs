using FluentAssertions;
using ParkNest.Application.Bookings;
using ParkNest.Application.Listings;
using ParkNest.Application.Queries;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

/// <summary>
/// Read endpoints are as much of an authorisation surface as the write ones — a leak here exposes
/// other people's bookings and wallet history rather than letting someone spend their credits.
/// </summary>
public sealed class QueryTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly IParkNestQueries _queries;

    public QueryTests() => _queries = new ParkNestQueries(_h.Db, _h.CurrentUser);

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task My_bookings_returns_only_the_callers_own()
    {
        var ctx = await SeedAsync();

        await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            ctx.SpaceId, ctx.VehicleId, TestHarness.Origin, 60, "bk-1"));

        // A second renter books the same space at a different time.
        var other = await _h.AddUserAsync(UserRole.Both);
        var otherCar = await _h.AddVehicleAsync(other.Id);
        await _h.Wallets.RechargeAsync(other.Id, 1000m, $"r:{other.Id}");
        _h.CurrentUser.SignIn(other.Id);
        await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            ctx.SpaceId, otherCar.Id, TestHarness.Origin.AddHours(5), 60, "bk-2"));

        _h.CurrentUser.SignIn(ctx.RenterId);
        var mine = await _queries.GetMyBookingsAsync(new BookingFilter());

        mine.Should().HaveCount(1);
        mine[0].SpaceTitle.Should().Be("Driveway");
    }

    [Fact]
    public async Task Hosting_bookings_shows_the_host_what_was_booked_on_their_space()
    {
        var ctx = await SeedAsync();

        await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            ctx.SpaceId, ctx.VehicleId, TestHarness.Origin, 60, "bk-1"));

        _h.CurrentUser.SignIn(ctx.HostId, UserRole.Host);
        var hosting = await _queries.GetHostingBookingsAsync(new BookingFilter());

        hosting.Should().HaveCount(1);

        // ...and the host sees nothing under "my bookings", because they rented nothing.
        (await _queries.GetMyBookingsAsync(new BookingFilter())).Should().BeEmpty();
    }

    [Fact]
    public async Task Booking_detail_is_visible_to_both_parties_but_nobody_else()
    {
        var ctx = await SeedAsync();

        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            ctx.SpaceId, ctx.VehicleId, TestHarness.Origin, 60, "bk-1"));

        // Renter
        (await _queries.GetBookingAsync(booking.Id)).Summary.Id.Should().Be(booking.Id);

        // Host
        _h.CurrentUser.SignIn(ctx.HostId, UserRole.Host);
        (await _queries.GetBookingAsync(booking.Id)).Summary.Id.Should().Be(booking.Id);

        // Stranger
        var stranger = await _h.AddUserAsync(UserRole.Both);
        _h.CurrentUser.SignIn(stranger.Id);

        var act = () => _queries.GetBookingAsync(booking.Id);
        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Booking_detail_carries_the_ledger_entries_behind_the_settlement()
    {
        var ctx = await SeedAsync();

        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            ctx.SpaceId, ctx.VehicleId, TestHarness.Origin, 60, "bk-1"));
        await _h.Bookings.StartSessionAsync(booking.Id, DetectionMethod.AppConfirmed, TestHarness.Origin);
        await _h.Bookings.EndSessionAsync(
            booking.Id, DetectionMethod.AppConfirmed, TestHarness.Origin.AddMinutes(60));

        var detail = await _queries.GetBookingAsync(booking.Id);

        detail.LedgerEntries.Should().NotBeEmpty();
        detail.LedgerEntries.Select(e => e.TransactionType).Should().Contain("Settlement");
        detail.Summary.Status.Should().Be(nameof(BookingStatus.Completed));
    }

    [Fact]
    public async Task My_listings_returns_only_the_callers_own_and_counts_active_bookings()
    {
        var ctx = await SeedAsync();

        await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            ctx.SpaceId, ctx.VehicleId, TestHarness.Origin, 60, "bk-1"));

        _h.CurrentUser.SignIn(ctx.HostId, UserRole.Host);
        var listings = await _queries.GetMyListingsAsync();

        listings.Should().HaveCount(1);
        listings[0].ActiveBookings.Should().Be(1);

        // A different host sees none of it.
        var rival = await _h.AddUserAsync(UserRole.Host);
        _h.CurrentUser.SignIn(rival.Id, UserRole.Host);
        (await _queries.GetMyListingsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_draft_listing_is_not_visible_to_other_users()
    {
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync();

        var draft = await _h.Listings.CreateDraftAsync(new CreateListingRequest(
            "Secret spot", "1 Quiet Ln", "Bengaluru", null, 12.9, 77.5, 60m,
            new[] { VehicleType.FourWheeler },
            new[] { new AvailabilityWindowRequest(DayOfWeek.Tuesday, new TimeOnly(9, 0), new TimeOnly(17, 0)) }));

        // The host can see their own draft.
        (await _queries.GetListingAsync(draft.Id)).Summary.Title.Should().Be("Secret spot");

        var stranger = await _h.AddUserAsync(UserRole.Both);
        _h.CurrentUser.SignIn(stranger.Id);

        var act = () => _queries.GetListingAsync(draft.Id);
        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Listing_detail_exposes_the_availability_windows_a_client_needs()
    {
        var ctx = await SeedAsync();

        var detail = await _queries.GetListingAsync(ctx.SpaceId);

        detail.AvailabilityWindows.Should().HaveCount(7);
        detail.TimeZoneId.Should().Be(TestHarness.TimeZone);
        detail.SupportedVehicleTypes.Should().Contain(nameof(VehicleType.FourWheeler));
    }

    [Fact]
    public async Task My_ledger_returns_only_the_callers_own_entries()
    {
        var ctx = await SeedAsync();

        await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            ctx.SpaceId, ctx.VehicleId, TestHarness.Origin, 60, "bk-1"));

        var entries = await _queries.GetMyLedgerAsync(50, 0);

        entries.Should().NotBeEmpty();
        entries.Select(e => e.TransactionType).Should().Contain("Hold");

        // A brand-new user has no history rather than seeing someone else's.
        var newcomer = await _h.AddUserAsync(UserRole.Both);
        _h.CurrentUser.SignIn(newcomer.Id);
        (await _queries.GetMyLedgerAsync(50, 0)).Should().BeEmpty();
    }

    [Fact]
    public async Task Ledger_paging_caps_an_oversized_page_request()
    {
        var ctx = await SeedAsync();

        await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            ctx.SpaceId, ctx.VehicleId, TestHarness.Origin, 60, "bk-1"));

        var act = () => _queries.GetMyLedgerAsync(int.MaxValue, 0);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Query_endpoints_reject_an_unauthenticated_caller()
    {
        await SeedAsync();
        _h.CurrentUser.SignOut();

        await new Func<Task>(() => _queries.GetMyBookingsAsync(new BookingFilter()))
            .Should().ThrowAsync<UnauthorizedException>();
        await new Func<Task>(() => _queries.GetMyListingsAsync())
            .Should().ThrowAsync<UnauthorizedException>();
        await new Func<Task>(() => _queries.GetMyLedgerAsync(10, 0))
            .Should().ThrowAsync<UnauthorizedException>();
    }

    private async Task<(Guid RenterId, Guid HostId, Guid SpaceId, Guid VehicleId)> SeedAsync()
    {
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync();
        var space = await _h.AddPublishedSpaceAsync(host.Id);

        var renter = await _h.AddUserAsync(UserRole.Both);
        var vehicle = await _h.AddVehicleAsync(renter.Id);
        await _h.Wallets.RechargeAsync(renter.Id, 2000m, $"recharge:{renter.Id}");

        _h.CurrentUser.SignIn(renter.Id);

        return (renter.Id, host.Id, space.Id, vehicle.Id);
    }
}
