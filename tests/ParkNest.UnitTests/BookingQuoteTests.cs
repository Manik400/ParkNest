using FluentAssertions;
using ParkNest.Application.Bookings;
using ParkNest.Application.Listings;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

/// <summary>
/// A quote answers "could I book this, and what would it cost" without moving anything. It must
/// agree with what booking actually does, or the UI will promise something the API then refuses.
/// </summary>
public sealed class BookingQuoteTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task A_quote_prices_the_session_without_touching_the_wallet()
    {
        var ctx = await SeedAsync();

        var quote = await _h.Bookings.QuoteAsync(ctx.SpaceId, TestHarness.Origin, 90);

        quote.CanBook.Should().BeTrue();
        quote.Unavailable.Should().BeNull();
        quote.BilledMinutes.Should().Be(90);
        quote.Amount.Should().Be(90m);

        var wallet = await _h.Wallets.GetOrCreateWalletAsync(ctx.RenterId);
        wallet.HeldBalance.Should().Be(0m);
        wallet.SpendableBalance.Should().Be(1000m);
    }

    [Fact]
    public async Task A_quote_rounds_up_to_the_billing_increment_exactly_as_booking_does()
    {
        var ctx = await SeedAsync();

        var quote = await _h.Bookings.QuoteAsync(ctx.SpaceId, TestHarness.Origin, 61);
        quote.BilledMinutes.Should().Be(75);

        var booking = await _h.Bookings.CreateBookingAsync(
            new CreateBookingRequest(ctx.SpaceId, ctx.VehicleId, TestHarness.Origin, 61, "bk-1"));

        booking.BookedMinutes.Should().Be(quote.BilledMinutes);
        booking.HoldAmount.Should().Be(quote.Amount);
    }

    [Fact]
    public async Task A_quote_reports_the_overstay_rate_the_renter_would_face()
    {
        var ctx = await SeedAsync(overstayMultiplier: 1.5m);

        var quote = await _h.Bookings.QuoteAsync(ctx.SpaceId, TestHarness.Origin, 60);

        quote.RatePerHour.Should().Be(60m);
        quote.OverstayRatePerHour.Should().Be(90m);
    }

    [Fact]
    public async Task A_quote_outside_opening_hours_explains_itself_rather_than_throwing()
    {
        var ctx = await SeedAsync(windows: new[]
        {
            new AvailabilityWindowRequest(DayOfWeek.Tuesday, new TimeOnly(9, 0), new TimeOnly(17, 0))
        });

        // 22:00 on the Tuesday, well past closing.
        var quote = await _h.Bookings.QuoteAsync(ctx.SpaceId, TestHarness.Origin.AddHours(13), 60);

        quote.CanBook.Should().BeFalse();
        quote.Unavailable.Should().Contain("outside the hours");
        // The price is still returned, so the UI can show what it would have cost.
        quote.Amount.Should().Be(60m);
    }

    [Fact]
    public async Task A_quote_for_an_already_booked_window_says_so()
    {
        var ctx = await SeedAsync();

        await _h.Bookings.CreateBookingAsync(
            new CreateBookingRequest(ctx.SpaceId, ctx.VehicleId, TestHarness.Origin, 120, "bk-1"));

        var quote = await _h.Bookings.QuoteAsync(ctx.SpaceId, TestHarness.Origin.AddMinutes(60), 60);

        quote.CanBook.Should().BeFalse();
        quote.Unavailable.Should().Contain("already booked");
    }

    [Fact]
    public async Task A_quote_in_the_past_is_refused()
    {
        var ctx = await SeedAsync();

        var quote = await _h.Bookings.QuoteAsync(ctx.SpaceId, TestHarness.Origin.AddHours(-5), 60);

        quote.CanBook.Should().BeFalse();
        quote.Unavailable.Should().Contain("in the past");
    }

    [Fact]
    public async Task An_unpublished_space_cannot_be_quoted()
    {
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync();

        var draft = await _h.Listings.CreateDraftAsync(new CreateListingRequest(
            "Draft spot", "1 Quiet Ln", "Bengaluru", null, 12.9, 77.5, 60m,
            new[] { VehicleType.FourWheeler },
            new[] { new AvailabilityWindowRequest(DayOfWeek.Tuesday, new TimeOnly(0, 0), new TimeOnly(0, 0)) }));

        var act = () => _h.Bookings.QuoteAsync(draft.Id, TestHarness.Origin, 60);

        await act.Should().ThrowAsync<DomainException>().WithMessage("*not currently accepting*");
    }

    private async Task<(Guid RenterId, Guid SpaceId, Guid VehicleId)> SeedAsync(
        decimal overstayMultiplier = 1.0m,
        IReadOnlyList<AvailabilityWindowRequest>? windows = null)
    {
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync(overstayMultiplier: overstayMultiplier);
        var space = await _h.AddPublishedSpaceAsync(host.Id, availabilityWindows: windows);

        var renter = await _h.AddUserAsync(UserRole.Both);
        var vehicle = await _h.AddVehicleAsync(renter.Id);
        await _h.Wallets.RechargeAsync(renter.Id, 1000m, $"r:{renter.Id}");
        _h.CurrentUser.SignIn(renter.Id);

        return (renter.Id, space.Id, vehicle.Id);
    }
}
