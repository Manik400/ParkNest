namespace ParkNest.Domain.Ratings;

public class Rating
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookingId { get; set; }
    public Guid FromUserId { get; set; }
    public Guid ToUserId { get; set; }

    /// <summary>1-5.</summary>
    public int Score { get; set; }

    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
