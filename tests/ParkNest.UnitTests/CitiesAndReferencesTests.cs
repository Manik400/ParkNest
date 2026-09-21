using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ParkNest.Application.Admin;
using ParkNest.Application.Bookings;
using ParkNest.Application.Listings;
using ParkNest.Application.Options;
using ParkNest.Application.Queries;
using ParkNest.Application.Wallets;
using ParkNest.Domain.Common;
using ParkNest.Domain.Listings;
using ParkNest.Domain.Wallets;

namespace ParkNest.UnitTests;

/// <summary>
/// Two things that came out of the same screenshot: a Gurgaon listing appearing 760 metres from a
/// renter in Bengaluru, and a ledger trail nobody could quote a line of. The first is a data
/// problem the API now refuses at the door; the second is a reference on every transaction.
/// </summary>
public sealed class CitiesAndReferencesTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly ICityService _cities;
    private readonly IParkNestQueries _queries;

    public CitiesAndReferencesTests()
    {
        _cities = new CityService(_h.Db, _h.CurrentUser, _h.Clock);
        _queries = new ParkNestQueries(_h.Db, _h.CurrentUser);
    }

    public void Dispose() => _h.Dispose();

    // --- Where a space may be -------------------------------------------------------------

    [Fact]
    public async Task A_listing_in_a_city_the_platform_is_not_in_is_refused()
    {
        var host = await _h.AddUserAsync(UserRole.Host);
        _h.CurrentUser.SignIn(host.Id, UserRole.Host);

        var act = () => _h.Listings.CreateDraftAsync(Listing("Testville", 12.97, 77.59));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*not in*Testville*");
    }

    [Fact]
    public async Task A_pin_outside_the_chosen_city_is_refused()
    {
        var host = await _h.AddUserAsync(UserRole.Host);
        _h.CurrentUser.SignIn(host.Id, UserRole.Host);

        // The screenshot: "gurgaon" typed, marker never moved off Bengaluru.
        var act = () => _h.Listings.CreateDraftAsync(Listing("Gurgaon", 12.97, 77.59));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*pin is not in Gurgaon*");
    }

    [Fact]
    public async Task The_city_is_stored_as_the_catalogue_spells_it()
    {
        var host = await _h.AddUserAsync(UserRole.Host);
        _h.CurrentUser.SignIn(host.Id, UserRole.Host);

        var space = await _h.Listings.CreateDraftAsync(Listing("  bengaluru ", 12.97, 77.59));

        // The band lookup is by name, so one spelling is the difference between a price band
        // that applies and a listing that can never be published.
        space.City.Should().Be("Bengaluru");
    }

    [Fact]
    public async Task The_city_list_says_which_ones_have_a_price_band()
    {
        await _h.AddBandAsync("Bengaluru");

        var cities = await _cities.ListAsync();

        cities.Should().Contain(c => c.Name == "Bengaluru" && c.HasPricing);
        cities.Should().Contain(c => c.Name == "Pune" && !c.HasPricing);
    }

    // --- Asking for a city ------------------------------------------------------------------

    [Fact]
    public async Task Asking_for_a_city_is_recorded_once_per_person_and_ranked_by_demand()
    {
        var one = await _h.AddUserAsync(UserRole.Both);
        var two = await _h.AddUserAsync(UserRole.Both);

        _h.CurrentUser.SignIn(one.Id);
        await _cities.RequestAsync("Jaipur", "Pink City has no parking");
        await _cities.RequestAsync("jaipur", null); // the same person, twice
        await _cities.RequestAsync("Kochi", null);

        _h.CurrentUser.SignIn(two.Id);
        await _cities.RequestAsync("JAIPUR", null);

        var admin = await _h.AddUserAsync(UserRole.Admin);
        _h.CurrentUser.SignIn(admin.Id, UserRole.Admin);

        var asks = await _cities.ListRequestsAsync();

        asks.Should().HaveCount(2);
        asks[0].City.Should().Be("Jaipur");
        asks[0].Count.Should().Be(2, "two people, however many times one of them pressed the button");
        asks[0].Notes.Should().ContainSingle();
        asks[1].City.Should().Be("Kochi");
    }

    [Fact]
    public async Task Asking_for_a_city_already_on_the_list_is_pointed_at_the_list()
    {
        var user = await _h.AddUserAsync(UserRole.Both);
        _h.CurrentUser.SignIn(user.Id);

        var act = () => _cities.RequestAsync("mumbai", null);

        await act.Should().ThrowAsync<DomainException>().WithMessage("*Mumbai is already available*");
    }

    [Fact]
    public async Task Only_an_admin_reads_the_asks()
    {
        var user = await _h.AddUserAsync(UserRole.Both);
        _h.CurrentUser.SignIn(user.Id);

        var act = () => _cities.ListRequestsAsync();

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    // --- References -------------------------------------------------------------------------

    [Fact]
    public async Task Every_transaction_gets_a_reference_a_person_can_quote()
    {
        var renter = await _h.AddUserAsync(UserRole.Renter);
        await _h.Wallets.RechargeAsync(renter.Id, 500m, $"r:{renter.Id}");
        await _h.Wallets.RechargeAsync(renter.Id, 200m, $"r2:{renter.Id}");

        var references = await _h.Db.LedgerTransactions.Select(t => t.Reference).ToListAsync();

        references.Should().HaveCount(2);
        references.Should().OnlyContain(r => r.StartsWith("TXN-") && r.Length == LedgerReference.Length);
        references.Distinct().Should().HaveCount(2);
        // No 0, O, 1 or I: the reference is read aloud and typed into a support form.
        references.Should().OnlyContain(r => !r.Substring(4).Any(c => "01OI".Contains(c)));
    }

    [Fact]
    public async Task A_cancelled_booking_returns_the_hold_with_a_line_that_names_it()
    {
        var (renterId, _, bookingId) = await HeldBookingAsync();

        await _h.Bookings.CancelBookingAsync(bookingId);

        _h.CurrentUser.SignIn(renterId);
        var lines = await _queries.GetMyLedgerAsync(20, 0);

        var hold = lines.Single(l => l.TransactionType == "Hold" && l.Account == "Spendable");
        var release = lines.Single(l => l.TransactionType == "ReleaseHold" && l.Account == "Spendable");

        // The wallet line can say "returned from TXN-…" rather than leaving the renter to match
        // a plus against a minus by amount.
        release.RevertsReference.Should().Be(hold.Reference);
        hold.RevertsReference.Should().BeNull();
    }

    [Fact]
    public async Task A_booking_detail_carries_the_references_on_its_ledger_trail()
    {
        var (renterId, _, bookingId) = await HeldBookingAsync();
        _h.CurrentUser.SignIn(renterId);

        var detail = await _queries.GetBookingAsync(bookingId);

        detail.LedgerEntries.Should().NotBeEmpty();
        detail.LedgerEntries.Should().OnlyContain(e => e.Reference.StartsWith("TXN-"));
    }

    // --- The platform's own money -----------------------------------------------------------

    [Fact]
    public async Task Platform_revenue_is_the_commission_the_ledger_actually_kept()
    {
        var (renterId, _, bookingId) = await HeldBookingAsync();
        _h.Clock.Advance(TimeSpan.FromHours(3));
        var start = _h.Clock.UtcNow;
        await _h.Bookings.StartSessionAsync(bookingId, DetectionMethod.AppConfirmed, start);
        _h.Clock.Advance(TimeSpan.FromMinutes(60));
        await _h.Bookings.EndSessionAsync(bookingId, DetectionMethod.AppConfirmed, _h.Clock.UtcNow);

        var admin = await _h.AddUserAsync(UserRole.Admin);
        _h.CurrentUser.SignIn(admin.Id, UserRole.Admin);

        var revenue = await new PlatformRevenueQueries(_h.Db, _h.CurrentUser, _h.Clock).GetAsync();

        // 60 credits for the hour at the harness's 10% commission.
        revenue.Total.Should().Be(6m);
        revenue.ThisMonth.Should().Be(6m);
        revenue.Settlements.Should().Be(1);
        revenue.GivenBack.Should().Be(0m);

        _h.CurrentUser.SignIn(renterId);
        var act = () => new PlatformRevenueQueries(_h.Db, _h.CurrentUser, _h.Clock).GetAsync();
        await act.Should().ThrowAsync<ForbiddenException>();
    }

    // --- The reset button -------------------------------------------------------------------

    [Fact]
    public async Task Reset_needs_the_admin_role_the_switch_and_the_phrase()
    {
        var wiper = new RecordingWiper();
        var options = new MaintenanceOptions { AllowDataReset = false };
        var reset = new DataResetService(
            wiper, _h.CurrentUser, Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<DataResetService>.Instance);

        var user = await _h.AddUserAsync(UserRole.Both);
        _h.CurrentUser.SignIn(user.Id);
        await FluentActions.Awaiting(() => reset.ResetAsync(reset.ConfirmationPhrase))
            .Should().ThrowAsync<ForbiddenException>("an ordinary user cannot");

        var admin = await _h.AddUserAsync(UserRole.Admin);
        _h.CurrentUser.SignIn(admin.Id, UserRole.Admin);
        await FluentActions.Awaiting(() => reset.ResetAsync(reset.ConfirmationPhrase))
            .Should().ThrowAsync<ForbiddenException>("the deployment has it switched off");

        options.AllowDataReset = true;
        await FluentActions.Awaiting(() => reset.ResetAsync("reset"))
            .Should().ThrowAsync<DomainException>("the phrase has to be typed exactly");

        wiper.Calls.Should().Be(0, "nothing above should have reached the wiper");

        await reset.ResetAsync(reset.ConfirmationPhrase);

        wiper.Calls.Should().Be(1);
    }

    // --- Helpers ----------------------------------------------------------------------------

    private static CreateListingRequest Listing(string city, double lat, double lng) => new(
        "Driveway", "12 Main Rd", city, null, lat, lng, 60m, new[] { VehicleType.FourWheeler },
        Enum.GetValues<DayOfWeek>()
            .Select(d => new AvailabilityWindowRequest(d, new TimeOnly(0, 0), new TimeOnly(0, 0)))
            .ToArray(),
        TestHarness.TimeZone);

    private async Task<(Guid RenterId, Guid HostId, Guid BookingId)> HeldBookingAsync()
    {
        var renter = await _h.AddUserAsync(UserRole.Renter);
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync();

        var space = await _h.AddPublishedSpaceAsync(host.Id, pricePerHour: 60m);
        var vehicle = await _h.AddVehicleAsync(renter.Id);
        await _h.Wallets.RechargeAsync(renter.Id, 1000m, $"recharge:{renter.Id}");

        _h.CurrentUser.SignIn(renter.Id);

        // Far enough out that cancelling is free, so the release returns the whole hold.
        var start = _h.Clock.UtcNow.AddHours(3);
        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            space.Id, vehicle.Id, start, 60, $"bk-{Guid.NewGuid()}"));

        return (renter.Id, host.Id, booking.Id);
    }

    private sealed class RecordingWiper : IDataWiper
    {
        public int Calls { get; private set; }

        public Task<DataResetResult> WipeAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new DataResetResult(new Dictionary<string, long>(), Array.Empty<string>()));
        }
    }
}
