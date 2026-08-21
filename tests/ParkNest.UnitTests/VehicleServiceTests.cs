using FluentAssertions;
using ParkNest.Application.Bookings;
using ParkNest.Application.Vehicles;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

public sealed class VehicleServiceTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly IVehicleService _vehicles;

    public VehicleServiceTests() => _vehicles = new VehicleService(_h.Db, _h.CurrentUser);

    public void Dispose() => _h.Dispose();

    [Theory]
    [InlineData("KA01AB1234")]
    [InlineData("ka 01 ab 1234")]
    [InlineData("KA-01-AB-1234")]
    public async Task Plates_are_normalised_so_formatting_does_not_create_duplicates(string input)
    {
        await _h.AddUserAsync(UserRole.Both);

        var vehicle = await _vehicles.AddAsync(input, VehicleType.FourWheeler);

        vehicle.PlateNumber.Should().Be("KA01AB1234");
    }

    [Fact]
    public async Task Adding_the_same_plate_twice_does_not_create_a_second_vehicle()
    {
        await _h.AddUserAsync(UserRole.Both);

        var first = await _vehicles.AddAsync("KA01AB1234", VehicleType.FourWheeler);
        var second = await _vehicles.AddAsync("ka01ab1234", VehicleType.TwoWheeler);

        second.Id.Should().Be(first.Id);
        second.Type.Should().Be(VehicleType.TwoWheeler);
        (await _vehicles.GetMineAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_plate_registered_to_someone_else_is_refused()
    {
        var owner = await _h.AddUserAsync(UserRole.Both);
        await _vehicles.AddAsync("KA01AB1234", VehicleType.FourWheeler);

        var other = await _h.AddUserAsync(UserRole.Both);
        _h.CurrentUser.SignIn(other.Id);

        var act = () => _vehicles.AddAsync("KA01AB1234", VehicleType.FourWheeler);

        // One plate is one physical car; two owners would break ANPR matching later.
        await act.Should().ThrowAsync<DomainException>().WithMessage("*already registered*");
    }

    [Fact]
    public async Task A_user_only_sees_their_own_vehicles()
    {
        var a = await _h.AddUserAsync(UserRole.Both);
        await _vehicles.AddAsync("KA01AB1234", VehicleType.FourWheeler);

        var b = await _h.AddUserAsync(UserRole.Both);
        await _vehicles.AddAsync("KA02CD5678", VehicleType.TwoWheeler);

        (await _vehicles.GetMineAsync()).Should().ContainSingle(v => v.PlateNumber == "KA02CD5678");

        _h.CurrentUser.SignIn(a.Id);
        (await _vehicles.GetMineAsync()).Should().ContainSingle(v => v.PlateNumber == "KA01AB1234");
    }

    [Fact]
    public async Task One_user_cannot_remove_another_users_vehicle()
    {
        await _h.AddUserAsync(UserRole.Both);
        var vehicle = await _vehicles.AddAsync("KA01AB1234", VehicleType.FourWheeler);

        var attacker = await _h.AddUserAsync(UserRole.Both);
        _h.CurrentUser.SignIn(attacker.Id);

        var act = () => _vehicles.RemoveAsync(vehicle.Id);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task A_vehicle_with_an_open_booking_cannot_be_removed()
    {
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync();
        var space = await _h.AddPublishedSpaceAsync(host.Id);

        var renter = await _h.AddUserAsync(UserRole.Both);
        await _h.Wallets.RechargeAsync(renter.Id, 1000m, $"r:{renter.Id}");
        _h.CurrentUser.SignIn(renter.Id);

        var vehicle = await _vehicles.AddAsync("KA01AB1234", VehicleType.FourWheeler);

        await _h.Bookings.CreateBookingAsync(
            new CreateBookingRequest(space.Id, vehicle.Id, TestHarness.Origin, 60, "bk-1"));

        var act = () => _vehicles.RemoveAsync(vehicle.Id);

        // Removing it would leave a live booking pointing at a vehicle that no longer exists.
        await act.Should().ThrowAsync<DomainException>().WithMessage("*open booking*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AB1")]
    public async Task An_implausible_plate_is_rejected(string plate)
    {
        await _h.AddUserAsync(UserRole.Both);

        var act = () => _vehicles.AddAsync(plate, VehicleType.FourWheeler);

        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task An_unauthenticated_caller_cannot_register_a_vehicle()
    {
        _h.CurrentUser.SignOut();

        var act = () => _vehicles.AddAsync("KA01AB1234", VehicleType.FourWheeler);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }
}
