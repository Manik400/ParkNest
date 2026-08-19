using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ParkNest.Application.Bookings;
using ParkNest.Application.Disputes;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

/// <summary>
/// A dispute is the only place an operator can move credits after the fact, so the tests here are
/// mostly about what it must refuse to do: pay out more than the session collected, pay out twice,
/// or let anyone but the two parties near it.
/// </summary>
public sealed class DisputeTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly IDisputeService _disputes;

    public DisputeTests()
    {
        _disputes = new DisputeService(
            _h.Db, _h.Ledger, _h.Wallets, _h.CurrentUser, _h.Clock,
            NullLogger<DisputeService>.Instance);
    }

    public void Dispose() => _h.Dispose();

    /// <summary>A completed session: booked, started, ended, settled. Leaves the renter signed in.</summary>
    private async Task<(Guid RenterId, Guid HostId, Guid BookingId)> CompletedBookingAsync()
    {
        var renter = await _h.AddUserAsync(UserRole.Renter);
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync();

        var space = await _h.AddPublishedSpaceAsync(host.Id, pricePerHour: 60m);
        var vehicle = await _h.AddVehicleAsync(renter.Id);
        await _h.Wallets.RechargeAsync(renter.Id, 1000m, $"recharge:{renter.Id}");

        _h.CurrentUser.SignIn(renter.Id);

        // From wherever the clock has reached, and with a fresh key: a test that builds two
        // sessions runs the second one after the first has already consumed an hour, and the
        // idempotency key is global, so a fixed key would hand the second caller the first
        // caller's booking.
        var start = _h.Clock.UtcNow;

        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            space.Id, vehicle.Id, start, 60, $"bk-{Guid.NewGuid()}"));

        await _h.Bookings.StartSessionAsync(booking.Id, DetectionMethod.AppConfirmed, start);
        _h.Clock.Advance(TimeSpan.FromMinutes(60));
        await _h.Bookings.EndSessionAsync(booking.Id, DetectionMethod.AppConfirmed, _h.Clock.UtcNow);

        return (renter.Id, host.Id, booking.Id);
    }

    private void SignInAdmin() => _h.CurrentUser.SignIn(Guid.NewGuid(), UserRole.Admin);

    [Fact]
    public async Task Either_party_can_raise_a_dispute()
    {
        var (renterId, hostId, bookingId) = await CompletedBookingAsync();

        var byRenter = await _disputes.RaiseAsync(new RaiseDisputeRequest(bookingId, "The space was blocked."));
        byRenter.RaisedByUserId.Should().Be(renterId);
        byRenter.Status.Should().Be(nameof(DisputeStatus.Open));

        SignInAdmin();
        await _disputes.RejectAsync(byRenter.DisputeId, "Not upheld.");

        _h.CurrentUser.SignIn(hostId);
        var byHost = await _disputes.RaiseAsync(new RaiseDisputeRequest(bookingId, "They never left."));
        byHost.RaisedByUserId.Should().Be(hostId);
    }

    [Fact]
    public async Task A_stranger_cannot_dispute_someone_elses_booking()
    {
        var (_, _, bookingId) = await CompletedBookingAsync();
        var stranger = await _h.AddUserAsync(UserRole.Both);
        _h.CurrentUser.SignIn(stranger.Id);

        var act = () => _disputes.RaiseAsync(new RaiseDisputeRequest(bookingId, "Curious."));

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task A_stranger_cannot_read_a_dispute_they_are_not_part_of()
    {
        var (_, _, bookingId) = await CompletedBookingAsync();
        var dispute = await _disputes.RaiseAsync(new RaiseDisputeRequest(bookingId, "The space was blocked."));

        var stranger = await _h.AddUserAsync(UserRole.Both);
        _h.CurrentUser.SignIn(stranger.Id);

        var act = () => _disputes.GetAsync(dispute.DisputeId);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task A_booking_that_never_started_cannot_be_disputed()
    {
        var renter = await _h.AddUserAsync(UserRole.Renter);
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync();
        var space = await _h.AddPublishedSpaceAsync(host.Id, pricePerHour: 60m);
        var vehicle = await _h.AddVehicleAsync(renter.Id);
        await _h.Wallets.RechargeAsync(renter.Id, 1000m, $"recharge:{renter.Id}");
        _h.CurrentUser.SignIn(renter.Id);

        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            space.Id, vehicle.Id, TestHarness.Origin, 60, "bk-1"));

        var act = () => _disputes.RaiseAsync(new RaiseDisputeRequest(booking.Id, "Changed my mind."));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*session has started*");
    }

    [Fact]
    public async Task A_booking_cannot_have_two_disputes_awaiting_a_decision()
    {
        var (_, _, bookingId) = await CompletedBookingAsync();
        await _disputes.RaiseAsync(new RaiseDisputeRequest(bookingId, "The space was blocked."));

        var act = () => _disputes.RaiseAsync(new RaiseDisputeRequest(bookingId, "Also this."));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*already has a dispute*");
    }

    [Fact]
    public async Task A_decided_booking_can_be_disputed_again()
    {
        var (renterId, _, bookingId) = await CompletedBookingAsync();
        var first = await _disputes.RaiseAsync(new RaiseDisputeRequest(bookingId, "The space was blocked."));

        SignInAdmin();
        await _disputes.RejectAsync(first.DisputeId, "Photos show it was clear.");

        _h.CurrentUser.SignIn(renterId);

        var second = await _disputes.RaiseAsync(new RaiseDisputeRequest(bookingId, "New evidence."));

        second.Status.Should().Be(nameof(DisputeStatus.Open));
    }

    [Fact]
    public async Task Resolving_in_the_renters_favour_at_the_platforms_cost_refunds_them()
    {
        var (renterId, hostId, bookingId) = await CompletedBookingAsync();
        var dispute = await _disputes.RaiseAsync(new RaiseDisputeRequest(bookingId, "The space was blocked."));

        var renterBefore = (await _h.Wallets.GetOrCreateWalletAsync(renterId)).SpendableBalance;
        var hostBefore = (await _h.Wallets.GetOrCreateWalletAsync(hostId)).EarningBalance;

        SignInAdmin();
        var resolved = await _disputes.ResolveAsync(
            new ResolveDisputeRequest(dispute.DisputeId, "Upheld, goodwill refund.", 60m, ChargedToPlatform: true));

        resolved.Status.Should().Be(nameof(DisputeStatus.Resolved));
        resolved.AdjustmentAmount.Should().Be(60m);

        (await _h.Wallets.GetOrCreateWalletAsync(renterId)).SpendableBalance.Should().Be(renterBefore + 60m);
        (await _h.Wallets.GetOrCreateWalletAsync(hostId)).EarningBalance.Should().Be(hostBefore,
            "the platform absorbed this one, so the host keeps their earnings");
    }

    [Fact]
    public async Task Resolving_against_the_host_takes_the_refund_out_of_their_earnings()
    {
        var (renterId, hostId, bookingId) = await CompletedBookingAsync();
        var dispute = await _disputes.RaiseAsync(new RaiseDisputeRequest(bookingId, "The space was blocked."));

        var hostBefore = (await _h.Wallets.GetOrCreateWalletAsync(hostId)).EarningBalance;

        SignInAdmin();
        await _disputes.ResolveAsync(
            new ResolveDisputeRequest(dispute.DisputeId, "Host was at fault.", 50m, ChargedToPlatform: false));

        (await _h.Wallets.GetOrCreateWalletAsync(hostId)).EarningBalance.Should().Be(hostBefore - 50m);
        (await _h.Wallets.GetOrCreateWalletAsync(renterId)).SpendableBalance.Should().BeGreaterThan(0m);
    }

    [Fact]
    public async Task A_refund_larger_than_the_session_collected_is_refused()
    {
        var (_, _, bookingId) = await CompletedBookingAsync();
        var dispute = await _disputes.RaiseAsync(new RaiseDisputeRequest(bookingId, "The space was blocked."));

        SignInAdmin();
        var act = () => _disputes.ResolveAsync(
            new ResolveDisputeRequest(dispute.DisputeId, "Upheld.", 5_000m, ChargedToPlatform: true));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*cannot exceed what the booking collected*");
    }

    [Fact]
    public async Task A_dispute_can_be_upheld_without_any_money_moving()
    {
        var (renterId, _, bookingId) = await CompletedBookingAsync();
        var dispute = await _disputes.RaiseAsync(new RaiseDisputeRequest(bookingId, "The host was rude."));
        var before = (await _h.Wallets.GetOrCreateWalletAsync(renterId)).SpendableBalance;

        SignInAdmin();
        var resolved = await _disputes.ResolveAsync(
            new ResolveDisputeRequest(dispute.DisputeId, "Noted against the host.", 0m, ChargedToPlatform: true));

        resolved.AdjustmentAmount.Should().BeNull();
        resolved.AdjustmentTransactionId.Should().BeNull();
        (await _h.Wallets.GetOrCreateWalletAsync(renterId)).SpendableBalance.Should().Be(before);
    }

    [Fact]
    public async Task A_decided_dispute_cannot_be_decided_again()
    {
        var (_, _, bookingId) = await CompletedBookingAsync();
        var dispute = await _disputes.RaiseAsync(new RaiseDisputeRequest(bookingId, "The space was blocked."));

        SignInAdmin();
        await _disputes.ResolveAsync(
            new ResolveDisputeRequest(dispute.DisputeId, "Upheld.", 60m, ChargedToPlatform: true));

        var act = () => _disputes.ResolveAsync(
            new ResolveDisputeRequest(dispute.DisputeId, "Upheld again.", 60m, ChargedToPlatform: true));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*already been decided*");
    }

    [Fact]
    public async Task A_resolved_dispute_leaves_both_wallets_reconciling_against_the_ledger()
    {
        var (renterId, hostId, bookingId) = await CompletedBookingAsync();
        var dispute = await _disputes.RaiseAsync(new RaiseDisputeRequest(bookingId, "The space was blocked."));

        SignInAdmin();
        await _disputes.ResolveAsync(
            new ResolveDisputeRequest(dispute.DisputeId, "Host was at fault.", 50m, ChargedToPlatform: false));

        foreach (var userId in new[] { renterId, hostId })
        {
            var wallet = await _h.Wallets.GetOrCreateWalletAsync(userId);
            var replayed = await _h.Ledger.RecomputeFromEntriesAsync(wallet.Id);

            replayed.Spendable.Should().Be(wallet.SpendableBalance);
            replayed.Held.Should().Be(wallet.HeldBalance);
            replayed.Earning.Should().Be(wallet.EarningBalance);
        }
    }

    [Fact]
    public async Task A_party_cannot_decide_their_own_dispute()
    {
        var (_, _, bookingId) = await CompletedBookingAsync();
        var dispute = await _disputes.RaiseAsync(new RaiseDisputeRequest(bookingId, "The space was blocked."));

        // Still signed in as the renter who raised it.
        var act = () => _disputes.ResolveAsync(
            new ResolveDisputeRequest(dispute.DisputeId, "I win.", 60m, ChargedToPlatform: true));

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task The_queue_shows_an_admin_everything_and_a_user_only_their_own()
    {
        var (renterId, _, firstBooking) = await CompletedBookingAsync();
        await _disputes.RaiseAsync(new RaiseDisputeRequest(firstBooking, "Mine."));

        var (_, _, secondBooking) = await CompletedBookingAsync();
        await _disputes.RaiseAsync(new RaiseDisputeRequest(secondBooking, "Somebody else's."));

        SignInAdmin();
        (await _disputes.ListAsync(new DisputeQuery())).Should().HaveCount(2);

        _h.CurrentUser.SignIn(renterId);
        var mine = await _disputes.ListAsync(new DisputeQuery());
        mine.Should().HaveCount(1);
        mine[0].RenterId.Should().Be(renterId);
    }

    [Fact]
    public async Task Evidence_submitted_with_a_dispute_comes_back_with_it()
    {
        var (_, _, bookingId) = await CompletedBookingAsync();

        var dispute = await _disputes.RaiseAsync(new RaiseDisputeRequest(
            bookingId,
            "The space was blocked.",
            new[] { new DisputeEvidenceInput("https://example.test/photo.jpg", "Car in the bay") }));

        dispute.Evidence.Should().ContainSingle();
        dispute.Evidence[0].Note.Should().Be("Car in the bay");
    }
}
