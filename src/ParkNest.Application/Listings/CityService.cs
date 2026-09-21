using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Abstractions;
using ParkNest.Domain.Common;
using ParkNest.Domain.Listings;

namespace ParkNest.Application.Listings;

/// <param name="HasPricing">
/// Whether an active price band exists. A city without one can be chosen but nothing in it can be
/// published, and a host is better told that on the form than at the publish button.
/// </param>
public sealed record CityView(
    string Name,
    string State,
    double Latitude,
    double Longitude,
    string TimeZoneId,
    bool HasPricing);

/// <param name="Count">How many people have asked for this city.</param>
public sealed record CityRequestSummary(
    string City,
    int Count,
    DateTimeOffset FirstAskedAt,
    DateTimeOffset LastAskedAt,
    IReadOnlyList<string> Notes);

public interface ICityService
{
    Task<IReadOnlyList<CityView>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Records that the caller wants a city the platform is not in. Idempotent per user and city.</summary>
    Task RequestAsync(string city, string? note, CancellationToken cancellationToken = default);

    /// <summary>The asks, most-wanted first. Admin only.</summary>
    Task<IReadOnlyList<CityRequestSummary>> ListRequestsAsync(CancellationToken cancellationToken = default);
}

public sealed class CityService : ICityService
{
    private const int MaxCityLength = 80;
    private const int MaxNoteLength = 300;

    private readonly IParkNestDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public CityService(IParkNestDbContext db, ICurrentUser currentUser, IClock clock)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<IReadOnlyList<CityView>> ListAsync(CancellationToken cancellationToken = default)
    {
        var priced = await _db.CityPricingConfigs
            .Where(b => b.IsActive)
            .Select(b => b.City)
            .Distinct()
            .ToListAsync(cancellationToken);

        var pricedSet = new HashSet<string>(priced, StringComparer.OrdinalIgnoreCase);

        return Cities.Supported
            .Select(c => new CityView(c.Name, c.State, c.Latitude, c.Longitude, c.TimeZoneId, pricedSet.Contains(c.Name)))
            .ToList();
    }

    public async Task RequestAsync(string city, string? note, CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.RequireUserId();
        var name = (city ?? string.Empty).Trim();

        if (name.Length is < 2 or > MaxCityLength)
        {
            throw new DomainException("Tell us which city, in a couple of words.");
        }

        if (Cities.Find(name) is { } known)
        {
            throw new DomainException($"{known.Name} is already available — pick it from the list.");
        }

        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        if (trimmedNote is { Length: > MaxNoteLength })
        {
            trimmedNote = trimmedNote[..MaxNoteLength];
        }

        // One ask per person per city. Pressing the button twice is enthusiasm, not two people.
        var existing = await _db.CityRequests.FirstOrDefaultAsync(
            r => r.UserId == userId && r.City.ToLower() == name.ToLower(),
            cancellationToken);

        if (existing is not null)
        {
            existing.Note = trimmedNote ?? existing.Note;
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        _db.CityRequests.Add(new CityRequest
        {
            City = name,
            UserId = userId,
            Note = trimmedNote,
            CreatedAt = _clock.UtcNow
        });

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CityRequestSummary>> ListRequestsAsync(CancellationToken cancellationToken = default)
    {
        _currentUser.RequireAdmin();

        var rows = await _db.CityRequests
            .AsNoTracking()
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

        // Grouped without regard to case in memory: "gurugram" and "Gurugram" are one ask, and
        // the volume here is a handful of rows, not a table worth a SQL GROUP BY.
        return rows
            .GroupBy(r => r.City.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new CityRequestSummary(
                g.First().City,
                g.Count(),
                g.Min(r => r.CreatedAt),
                g.Max(r => r.CreatedAt),
                g.Where(r => !string.IsNullOrWhiteSpace(r.Note)).Select(r => r.Note!).Take(10).ToList()))
            .OrderByDescending(s => s.Count)
            .ThenByDescending(s => s.LastAskedAt)
            .ToList();
    }
}
