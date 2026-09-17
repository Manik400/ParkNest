using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Bookings;
using ParkNest.Application.Ratings;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

/// <summary>
/// Ratings are the one place a user writes to someone else's reputation, and the trust score they
/// move gates cash-out. So the tests are largely about who may write, how often, and how far one
/// opinion can move the number.
/// </summary>
public sealed class RatingTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly IRatingService _ratings;

    public RatingTests()
    {
        _ratings = new RatingService(_h.Db, _h.CurrentUser, _h.Clock);
    }

    public void Dispose() => _h.Dispose();

    /// <summary>A completed session. Leaves the renter signed in.</summary>
    private async Task<(Guid RenterId, Guid HostId, Guid BookingId)> CompletedBookingAsync()
    {
        var renter = await _h.AddUserAsync(UserRole.Renter);
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync();

        var space = await _h.AddPublishedSpaceAsync(host.Id, 60m);
        var vehicle = await _h.AddVehicleAsync(renter.Id);
        await _h.Wallets.RechargeAsync(renter.Id, 1000m, $"recharge:{renter.Id}");

        _h.CurrentUser.SignIn(renter.Id);

        var start = _h.Clock.UtcNow;
        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            space.Id, vehicle.Id, start, 60, $"bk-{Guid.NewGuid()}"));

        await _h.Bookings.StartSessionAsync(booking.Id, DetectionMethod.AppConfirmed, start);
        _h.Clock.Advance(TimeSpan.FromMinutes(60));
        await _h.Bookings.EndSessionAsync(booking.Id, DetectionMethod.AppConfirmed, _h.Clock.UtcNow);

        return (renter.Id, host.Id, booking.Id);
    }

    private async Task<int> TrustOf(Guid userId) =>
        (await _h.Db.Users.AsNoTracking().FirstAsync(u => u.Id == userId)).TrustScore;

    [Fact]
    public async Task A_renter_rates_the_host_and_the_host_rates_the_renter()
    {
        var (renterId, hostId, bookingId) = await CompletedBookingAsync();

        var fromRenter = await _ratings.RateAsync(new RateRequest(bookingId, 5, "Easy to find."));
        fromRenter.ToUserId.Should().Be(hostId);

        _h.CurrentUser.SignIn(hostId);
        var fromHost = await _ratings.RateAsync(new RateRequest(bookingId, 4));
        fromHost.ToUserId.Should().Be(renterId);
    }

    [Fact]
    public async Task A_stranger_cannot_rate_a_booking_they_were_not_part_of()
    {
        var (_, _, bookingId) = await CompletedBookingAsync();
        var stranger = await _h.AddUserAsync(UserRole.Both);
        _h.CurrentUser.SignIn(stranger.Id);

        var act = () => _ratings.RateAsync(new RateRequest(bookingId, 1));

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Nobody_can_rate_the_same_booking_twice()
    {
        // Without this a grudge becomes a campaign: the same renter filing one star nightly.
        var (_, _, bookingId) = await CompletedBookingAsync();
        await _ratings.RateAsync(new RateRequest(bookingId, 5));

        var act = () => _ratings.RateAsync(new RateRequest(bookingId, 1));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*already rated*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public async Task A_score_outside_one_to_five_is_refused(int score)
    {
        var (_, _, bookingId) = await CompletedBookingAsync();

        var act = () => _ratings.RateAsync(new RateRequest(bookingId, score));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*between 1 and 5*");
    }

    [Fact]
    public async Task A_session_that_has_not_finished_cannot_be_rated()
    {
        var renter = await _h.AddUserAsync(UserRole.Renter);
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync();
        var space = await _h.AddPublishedSpaceAsync(host.Id, 60m);
        var vehicle = await _h.AddVehicleAsync(renter.Id);
        await _h.Wallets.RechargeAsync(renter.Id, 1000m, $"recharge:{renter.Id}");
        _h.CurrentUser.SignIn(renter.Id);

        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            space.Id, vehicle.Id, _h.Clock.UtcNow, 60, "bk-open"));

        var act = () => _ratings.RateAsync(new RateRequest(booking.Id, 5));

        await act.Should().ThrowAsync<DomainException>().WithMessage("*session has finished*");
    }

    [Fact]
    public async Task A_good_rating_lifts_the_trust_score_and_a_bad_one_lowers_it()
    {
        var (_, hostId, bookingId) = await CompletedBookingAsync();

        // A fresh account starts at 100, which is already the ceiling — so measure the fall first.
        await _ratings.RateAsync(new RateRequest(bookingId, 1));
        var afterBad = await TrustOf(hostId);
        afterBad.Should().Be(94);

        var (_, secondHostId, secondBooking) = await CompletedBookingAsync();
        await _ratings.RateAsync(new RateRequest(secondBooking, 5));
        (await TrustOf(secondHostId)).Should().Be(100, "praise cannot push anyone above the ceiling");
    }

    [Fact]
    public async Task A_middling_rating_leaves_the_trust_score_alone()
    {
        var (_, hostId, bookingId) = await CompletedBookingAsync();

        await _ratings.RateAsync(new RateRequest(bookingId, 3));

        (await TrustOf(hostId)).Should().Be(100, "three out of five is not a complaint");
    }

    [Fact]
    public async Task The_trust_score_cannot_be_driven_below_zero()
    {
        var host = await _h.AddUserAsync(UserRole.Host);
        var user = await _h.Db.Users.FirstAsync(u => u.Id == host.Id);
        user.TrustScore = 2;
        await _h.Db.SaveChangesAsync();

        // Borrow a finished booking and point the rating at this host.
        var (_, _, bookingId) = await CompletedBookingAsync();
        var booking = await _h.Db.Bookings.FirstAsync(b => b.Id == bookingId);
        booking.HostId = host.Id;
        await _h.Db.SaveChangesAsync();

        await _ratings.RateAsync(new RateRequest(bookingId, 1));

        (await TrustOf(host.Id)).Should().Be(0);
    }

    [Fact]
    public async Task One_opinion_cannot_push_someone_off_the_platform()
    {
        // The reason the steps are small: the score gates cash-out, so a single annoyed
        // counterparty must not be able to strand a host's earnings.
        var (_, hostId, bookingId) = await CompletedBookingAsync();

        await _ratings.RateAsync(new RateRequest(bookingId, 1));

        (await TrustOf(hostId)).Should().BeGreaterThan(50);
    }

    [Fact]
    public async Task Reputation_reports_the_average_and_the_recent_comments()
    {
        var (_, hostId, firstBooking) = await CompletedBookingAsync();
        await _ratings.RateAsync(new RateRequest(firstBooking, 5, "Spotless."));

        var (_, _, secondBooking) = await CompletedBookingAsync();
        var second = await _h.Db.Bookings.FirstAsync(b => b.Id == secondBooking);
        second.HostId = hostId;
        await _h.Db.SaveChangesAsync();
        await _ratings.RateAsync(new RateRequest(secondBooking, 3));

        var reputation = await _ratings.GetReputationAsync(hostId);

        reputation.RatingCount.Should().Be(2);
        reputation.AverageScore.Should().Be(4);
        reputation.Recent.Should().HaveCount(2);
        reputation.Recent.Select(r => r.Comment).Should().Contain("Spotless.");
    }

    [Fact]
    public async Task An_unrated_user_has_no_average_rather_than_a_zero()
    {
        // "0.0 stars" for a new host reads as terrible, which is the opposite of the truth.
        var (renterId, _, _) = await CompletedBookingAsync();

        var reputation = await _ratings.GetReputationAsync(renterId);

        reputation.AverageScore.Should().BeNull();
        reputation.RatingCount.Should().Be(0);
        reputation.TrustScore.Should().Be(100);
    }

    [Fact]
    public async Task The_prompt_says_whether_a_rating_is_still_owed()
    {
        var (_, hostId, bookingId) = await CompletedBookingAsync();

        var before = await _ratings.GetPromptAsync(bookingId);
        before.CanRate.Should().BeTrue();
        before.AboutUserId.Should().Be(hostId);

        await _ratings.RateAsync(new RateRequest(bookingId, 5));

        var after = await _ratings.GetPromptAsync(bookingId);
        after.CanRate.Should().BeFalse();
        after.Reason.Should().Contain("already rated");
    }

    [Fact]
    public async Task A_violation_can_still_be_rated_by_both_sides()
    {
        // The host has every right to say what happened, and the renter to give their side.
        var renter = await _h.AddUserAsync(UserRole.Renter);
        var host = await _h.AddUserAsync(UserRole.Host);
        await _h.AddBandAsync();
        var space = await _h.AddPublishedSpaceAsync(host.Id, 60m);
        var vehicle = await _h.AddVehicleAsync(renter.Id);
        await _h.Wallets.RechargeAsync(renter.Id, 100m, $"recharge:{renter.Id}");
        _h.CurrentUser.SignIn(renter.Id);

        var start = _h.Clock.UtcNow;
        var booking = await _h.Bookings.CreateBookingAsync(new CreateBookingRequest(
            space.Id, vehicle.Id, start, 60, "bk-violation"));
        await _h.Bookings.StartSessionAsync(booking.Id, DetectionMethod.AppConfirmed, start);
        _h.Clock.Advance(TimeSpan.FromMinutes(180));
        var outcome = await _h.Bookings.EndSessionAsync(
            booking.Id, DetectionMethod.AppConfirmed, _h.Clock.UtcNow);

        outcome.Booking.Status.Should().Be(BookingStatus.InViolation);

        _h.CurrentUser.SignIn(host.Id);
        var rating = await _ratings.RateAsync(new RateRequest(booking.Id, 1, "Left owing."));

        rating.ToUserId.Should().Be(renter.Id);
    }
}
