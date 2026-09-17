using FluentAssertions;
using ParkNest.Application.Bookings;
using ParkNest.Application.Listings;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

/// <summary>
/// Before this existed, every endpoint took the acting user's id from the request body — so any
/// caller could spend anyone's credits by typing their GUID. These tests are the regression net
/// for that: they assert the services refuse to act on a resource the caller does not own.
/// </summary>
public sealed class AuthorizationTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task An_unauthenticated_caller_cannot_create_a_booking()
    {
        var (_, spaceId, vehicleId) = await SeedBookableSpaceAsync();

        _h.CurrentUser.SignOut();

        var act = () => _h.Bookings.CreateBookingAsync(
            new CreateBookingRequest(spaceId, vehicleId, TestHarness.Origin, 60, "bk-1"));

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task A_booking_is_always_created_for_the_caller_not_for_a_supplied_id()
    {
        var (renterId, spaceId, vehicleId) = await SeedBookableSpaceAsync();

        var booking = await _h.Bookings.CreateBookingAsync(
            new CreateBookingRequest(spaceId, vehicleId, TestHarness.Origin, 60, "bk-1"));

        // There is no input that could have said otherwise — that is the point.
        booking.RenterId.Should().Be(renterId);
    }

    [Fact]
    public async Task One_renter_cannot_end_another_renters_session()
    {
        var (_, spaceId, vehicleId) = await SeedBookableSpaceAsync();

        var booking = await _h.Bookings.CreateBookingAsync(
            new CreateBookingRequest(spaceId, vehicleId, TestHarness.Origin, 60, "bk-1"));
        await _h.Bookings.StartSessionAsync(booking.Id, DetectionMethod.AppConfirmed, TestHarness.Origin);

        // A different authenticated user who happens to know the booking id.
        var attacker = await _h.AddUserAsync(UserRole.Both);
        _h.CurrentUser.SignIn(attacker.Id);

        var act = () => _h.Bookings.EndSessionAsync(
            booking.Id, DetectionMethod.AppConfirmed, TestHarness.Origin.AddMinutes(30));

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task One_renter_cannot_cancel_another_renters_booking()
    {
        var (_, spaceId, vehicleId) = await SeedBookableSpaceAsync();

        var booking = await _h.Bookings.CreateBookingAsync(
            new CreateBookingRequest(spaceId, vehicleId, TestHarness.Origin, 60, "bk-1"));

        var attacker = await _h.AddUserAsync(UserRole.Both);
        _h.CurrentUser.SignIn(attacker.Id);

        var act = () => _h.Bookings.CancelBookingAsync(booking.Id);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task An_admin_can_act_on_a_booking_to_resolve_a_dispute()
    {
        var (_, spaceId, vehicleId) = await SeedBookableSpaceAsync();

        var booking = await _h.Bookings.CreateBookingAsync(
            new CreateBookingRequest(spaceId, vehicleId, TestHarness.Origin, 60, "bk-1"));

        var admin = await _h.AddUserAsync(UserRole.Admin);
        _h.CurrentUser.SignIn(admin.Id, UserRole.Admin);

        var cancelled = await _h.Bookings.CancelBookingAsync(booking.Id);

        cancelled.Booking.Status.Should().Be(BookingStatus.Cancelled);
    }

    [Fact]
    public async Task A_host_cannot_publish_another_hosts_listing()
    {
        var owner = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync();

        var draft = await _h.Listings.CreateDraftAsync(new CreateListingRequest(
            "Driveway", "12 Main Rd", "Bengaluru", null, 12.97, 77.59, 60m,
            new[] { VehicleType.FourWheeler }));

        var rival = await _h.AddUserAsync(UserRole.Host);
        _h.CurrentUser.SignIn(rival.Id, UserRole.Host);

        var act = () => _h.Listings.PublishAsync(draft.Id);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task A_host_cannot_delist_another_hosts_listing()
    {
        var owner = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync();
        var space = await _h.AddPublishedSpaceAsync(owner.Id);

        var rival = await _h.AddUserAsync(UserRole.Host);
        _h.CurrentUser.SignIn(rival.Id, UserRole.Host);

        var act = () => _h.Listings.SetStatusAsync(space.Id, SpaceStatus.Delisted);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task A_listing_is_always_owned_by_the_caller()
    {
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync();

        var draft = await _h.Listings.CreateDraftAsync(new CreateListingRequest(
            "Driveway", "12 Main Rd", "Bengaluru", null, 12.97, 77.59, 60m,
            new[] { VehicleType.FourWheeler }));

        draft.HostId.Should().Be(host.Id);
    }

    [Fact]
    public async Task A_renter_cannot_book_using_a_vehicle_that_is_not_theirs()
    {
        var (_, spaceId, _) = await SeedBookableSpaceAsync();

        var stranger = await _h.AddUserAsync(UserRole.Both);
        var strangersCar = await _h.AddVehicleAsync(stranger.Id);

        var attacker = await _h.AddUserAsync(UserRole.Both);
        _h.CurrentUser.SignIn(attacker.Id);
        await _h.Wallets.RechargeAsync(attacker.Id, 1000m, $"r:{attacker.Id}");
        _h.CurrentUser.SignIn(attacker.Id);

        var act = () => _h.Bookings.CreateBookingAsync(
            new CreateBookingRequest(spaceId, strangersCar.Id, TestHarness.Origin, 60, "bk-1"));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*different user*");
    }

    private async Task<(Guid renterId, Guid spaceId, Guid vehicleId)> SeedBookableSpaceAsync()
    {
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync();
        var space = await _h.AddPublishedSpaceAsync(host.Id);

        var renter = await _h.AddUserAsync(UserRole.Both);
        var vehicle = await _h.AddVehicleAsync(renter.Id);
        await _h.Wallets.RechargeAsync(renter.Id, 1000m, $"recharge:{renter.Id}");

        _h.CurrentUser.SignIn(renter.Id);

        return (renter.Id, space.Id, vehicle.Id);
    }
}
