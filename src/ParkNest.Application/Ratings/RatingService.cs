using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Abstractions;
using ParkNest.Domain.Bookings;
using ParkNest.Domain.Common;
using ParkNest.Domain.Ratings;

namespace ParkNest.Application.Ratings;

/// <summary>
/// Each party rates the other after a session.
///
/// Ratings feed the trust score, which until now only moved when someone left owing credits. That
/// made it a record of one failure rather than a reputation: a host whose space is never as
/// described, or a renter who leaves rubbish behind, was indistinguishable from anyone else as
/// long as their wallet covered the bill.
/// </summary>
public interface IRatingService
{
    /// <summary>Rates the other party to a finished booking. One rating per person per booking.</summary>
    Task<RatingView> RateAsync(RateRequest request, CancellationToken cancellationToken = default);

    /// <summary>What a user's counterparties have said about them.</summary>
    Task<ReputationView> GetReputationAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Whether the caller still owes a rating on this booking, and who it would be about.</summary>
    Task<RatingPrompt> GetPromptAsync(Guid bookingId, CancellationToken cancellationToken = default);
}

public sealed record RateRequest(Guid BookingId, int Score, string? Comment = null);

public sealed record RatingView(Guid Id, Guid BookingId, Guid ToUserId, int Score, string? Comment, DateTimeOffset CreatedAt);

/// <param name="TrustScore">
/// The 0-100 signal the platform acts on. Distinct from the star average: it starts from a
/// presumption of good faith and is moved by conduct, whereas the average only reports opinions.
/// </param>
public sealed record ReputationView(
    Guid UserId,
    int TrustScore,
    double? AverageScore,
    int RatingCount,
    IReadOnlyList<RatingView> Recent);

public sealed record RatingPrompt(Guid BookingId, bool CanRate, Guid? AboutUserId, string? Reason);

public sealed class RatingService : IRatingService
{
    /// <summary>
    /// How far a rating can move a trust score in one step.
    ///
    /// Small on purpose. The score gates real things — cash-out, and eventually access — so one
    /// annoyed counterparty should not be able to push someone off the platform, and one glowing
    /// review should not undo a history of trouble. A violation still costs ten, because leaving
    /// owing credits is conduct rather than opinion.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, int> TrustDelta = new Dictionary<int, int>
    {
        [1] = -6,
        [2] = -3,
        [3] = 0,
        [4] = +1,
        [5] = +2,
    };

    private const int MaxTrustScore = 100;

    private readonly IParkNestDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public RatingService(IParkNestDbContext db, ICurrentUser currentUser, IClock clock)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<RatingView> RateAsync(RateRequest request, CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.RequireUserId();

        if (request.Score is < 1 or > 5)
        {
            throw new DomainException("A rating is between 1 and 5.");
        }

        var booking = await _db.Bookings.FirstOrDefaultAsync(b => b.Id == request.BookingId, cancellationToken)
                      ?? throw new DomainException($"Booking {request.BookingId} does not exist.");

        var counterparty = CounterpartyOf(booking, userId);

        if (!IsRateable(booking))
        {
            throw new DomainException("A booking can only be rated once the session has finished.");
        }

        var already = await _db.Ratings.AnyAsync(
            r => r.BookingId == booking.Id && r.FromUserId == userId, cancellationToken);

        if (already)
        {
            // One per person per booking. Without this a grudge becomes a campaign — the same
            // renter could file a one-star review nightly against a host they fell out with.
            throw new DomainException("You have already rated this booking.");
        }

        var rating = new Rating
        {
            BookingId = booking.Id,
            FromUserId = userId,
            ToUserId = counterparty,
            Score = request.Score,
            Comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim(),
            CreatedAt = _clock.UtcNow,
        };

        _db.Ratings.Add(rating);

        var subject = await _db.Users.FirstAsync(u => u.Id == counterparty, cancellationToken);
        subject.TrustScore = Math.Clamp(subject.TrustScore + TrustDelta[request.Score], 0, MaxTrustScore);

        await _db.SaveChangesAsync(cancellationToken);

        return ToView(rating);
    }

    public async Task<ReputationView> GetReputationAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Public on purpose: deciding whether to park in a stranger's driveway, or to let a
        // stranger park in yours, is exactly the decision this exists to inform.
        _currentUser.RequireUserId();

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
                   ?? throw new DomainException("That user does not exist.");

        var ratings = await _db.Ratings
            .AsNoTracking()
            .Where(r => r.ToUserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

        return new ReputationView(
            user.Id,
            user.TrustScore,
            // Null rather than zero for someone nobody has rated. A new host displayed as "0.0
            // stars" reads as terrible rather than unknown, which is the opposite of the truth.
            ratings.Count == 0 ? null : Math.Round(ratings.Average(r => r.Score), 2),
            ratings.Count,
            ratings.Take(10).Select(ToView).ToList());
    }

    public async Task<RatingPrompt> GetPromptAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.RequireUserId();

        var booking = await _db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
                      ?? throw new DomainException($"Booking {bookingId} does not exist.");

        var counterparty = CounterpartyOf(booking, userId);

        if (!IsRateable(booking))
        {
            return new RatingPrompt(bookingId, false, null, "The session has not finished yet.");
        }

        var already = await _db.Ratings.AnyAsync(
            r => r.BookingId == bookingId && r.FromUserId == userId, cancellationToken);

        return already
            ? new RatingPrompt(bookingId, false, counterparty, "You have already rated this booking.")
            : new RatingPrompt(bookingId, true, counterparty, null);
    }

    /// <summary>
    /// Who the caller would be rating, and a 403 if they were not part of this booking at all.
    /// Ratings are the one place a user writes to someone else's reputation, so being party to the
    /// session is the whole licence to do it.
    /// </summary>
    private static Guid CounterpartyOf(Booking booking, Guid userId)
    {
        if (booking.RenterId == userId)
        {
            return booking.HostId;
        }

        if (booking.HostId == userId)
        {
            return booking.RenterId;
        }

        throw new ForbiddenException("You were not part of this booking.");
    }

    /// <summary>
    /// A session that actually happened. A cancellation is not an experience of the space, and a
    /// booking still running has not finished being one.
    ///
    /// A violation counts: the host has every right to say what happened, and the renter to say
    /// their side.
    /// </summary>
    private static bool IsRateable(Booking booking) =>
        booking.Status is BookingStatus.Completed or BookingStatus.InViolation;

    private static RatingView ToView(Rating rating) =>
        new(rating.Id, rating.BookingId, rating.ToUserId, rating.Score, rating.Comment, rating.CreatedAt);
}
