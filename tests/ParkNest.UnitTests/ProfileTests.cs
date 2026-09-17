using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Payments;
using ParkNest.Application.Users;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

/// <summary>
/// The profile is where an email-only account gets the mobile number the payment gateway insists
/// on. What matters: the number saved there is what the gateway receives, the sign-in contacts
/// cannot be changed here, and a bad number is refused rather than stored.
/// </summary>
public sealed class ProfileTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private IProfileService Profile => new ProfileService(_h.Db, _h.CurrentUser);

    [Fact]
    public async Task A_payment_phone_saved_on_the_profile_reaches_the_gateway_without_being_asked_at_checkout()
    {
        var user = await _h.AddUserAsync(UserRole.Renter);
        user.Phone = null;
        user.Email = "renter@example.com";
        await _h.Db.SaveChangesAsync();

        var saved = await Profile.UpdateMineAsync(fullName: null, paymentPhone: "+91 98765 43210");
        saved.PaymentPhone.Should().Be("919876543210");
        saved.Phone.Should().BeNull("the sign-in phone is untouched");
        saved.Email.Should().Be("renter@example.com");

        var gateway = new FakeGateway("secret");
        var (payments, _) = PaymentTestSupport.Create(_h, gateway);

        await payments.StartAsync(500m);

        gateway.LastRequest!.Customer.Phone.Should().Be("919876543210");
    }

    [Fact]
    public async Task An_empty_payment_phone_clears_it_and_a_bad_one_is_refused()
    {
        await _h.AddUserAsync(UserRole.Renter);

        await Profile.UpdateMineAsync(null, "9876543210");
        (await Profile.UpdateMineAsync(null, "")).PaymentPhone.Should().BeNull();

        var act = () => Profile.UpdateMineAsync(null, "12345");
        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task The_name_is_editable_and_a_null_field_is_left_alone()
    {
        var user = await _h.AddUserAsync(UserRole.Renter);
        await Profile.UpdateMineAsync(null, "9876543210");

        var updated = await Profile.UpdateMineAsync("  Asha Rao ", null);

        updated.FullName.Should().Be("Asha Rao");
        updated.PaymentPhone.Should().Be("9876543210", "null means leave alone");
        (await _h.Db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id)).FullName.Should().Be("Asha Rao");
    }
}
