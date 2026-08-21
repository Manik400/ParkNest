using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Domain.Common;
using ParkNest.Domain.Users;

namespace ParkNest.Application.Users;

/// <summary>
/// Identity verification for hosts.
///
/// This exists because <c>RequestCashOutAsync</c> refuses anyone who is not verified, and until
/// now nothing in the system could ever make them verified — a host could earn credits and never
/// convert them. The check was right; the missing half was this.
///
/// Verification is a human decision, deliberately. An automated document check needs a provider we
/// do not have, and the failure mode of guessing is either paying out to a fabricated identity or
/// stranding a real host's money. An operator reading a photograph is slower, and honest about it.
/// </summary>
public interface IKycService
{
    /// <summary>Submits the caller's identity for review, replacing any attempt still waiting.</summary>
    Task<KycSubmissionView> SubmitAsync(SubmitKyc request, CancellationToken cancellationToken = default);

    /// <summary>Where the caller stands, including why a previous attempt was refused.</summary>
    Task<KycStatusView> GetMineAsync(CancellationToken cancellationToken = default);

    /// <summary>The review queue, oldest first. Admin only.</summary>
    Task<IReadOnlyList<KycSubmissionView>> GetQueueAsync(
        KycStatus? status, int limit, int offset, CancellationToken cancellationToken = default);

    /// <summary>Approves a submission, which is what unlocks cash-out for that host.</summary>
    Task<KycSubmissionView> VerifyAsync(Guid submissionId, CancellationToken cancellationToken = default);

    /// <summary>Refuses a submission with a reason the host will read.</summary>
    Task<KycSubmissionView> RejectAsync(Guid submissionId, string reason, CancellationToken cancellationToken = default);
}

/// <param name="DocumentNumber">
/// The full number. Used to derive the last four and a keyed hash, then dropped — it is never
/// stored, never logged and never returned.
/// </param>
/// <param name="DocumentPhotoUrl">
/// A photograph already uploaded through the document endpoint. Separate from this call because a
/// multipart upload and a JSON form are different shapes, not because either is optional.
/// </param>
public sealed record SubmitKyc(
    string LegalName,
    KycDocumentType DocumentType,
    string DocumentNumber,
    string? DocumentPhotoUrl = null,
    string? PayoutAccountNumber = null);

public sealed record KycSubmissionView(
    Guid Id,
    Guid UserId,
    string LegalName,
    string DocumentType,
    string DocumentLast4,
    string? DocumentPhotoUrl,
    string? PayoutAccountLast4,
    string Status,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? ReviewedAt,
    string? RejectionReason,
    string? UserPhone = null,
    int PreviousRejections = 0,
    int OtherAccountsWithThisDocument = 0);

public sealed record KycStatusView(string Status, bool CanCashOut, KycSubmissionView? Latest);

public sealed class KycService : IKycService
{
    private readonly IParkNestDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;
    private readonly IEventBus _events;
    private readonly KycOptions _options;

    public KycService(
        IParkNestDbContext db,
        ICurrentUser currentUser,
        IClock clock,
        IEventBus events,
        IOptions<KycOptions> options)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
        _events = events;
        _options = options.Value;
    }

    public async Task<KycSubmissionView> SubmitAsync(
        SubmitKyc request,
        CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.RequireUserId();
        var user = await _db.Users.FirstAsync(u => u.Id == userId, cancellationToken);

        if (user.KycStatus == KycStatus.Verified)
        {
            throw new DomainException("You are already verified.");
        }

        var legalName = (request.LegalName ?? string.Empty).Trim();

        if (legalName.Length < 2)
        {
            throw new DomainException("Enter your name exactly as it appears on the document.");
        }

        var number = Normalise(request.DocumentNumber);

        if (number.Length < 6)
        {
            throw new DomainException("That does not look like a document number.");
        }

        if (_options.RequireDocumentPhoto && string.IsNullOrWhiteSpace(request.DocumentPhotoUrl))
        {
            throw new DomainException("Attach a photograph of the document.");
        }

        // An attempt still waiting is replaced rather than queued alongside. Two pending rows for
        // one host is a queue an operator has to reconcile, and the newer one is always the one
        // the host means.
        var pending = await _db.KycSubmissions
            .Where(s => s.UserId == userId && s.Status == KycStatus.Pending)
            .ToListAsync(cancellationToken);

        _db.KycSubmissions.RemoveRange(pending);

        var submission = new KycSubmission
        {
            UserId = userId,
            LegalName = legalName,
            DocumentType = request.DocumentType,
            DocumentLast4 = Last4(number),
            DocumentHash = Hash(number),
            DocumentPhotoUrl = string.IsNullOrWhiteSpace(request.DocumentPhotoUrl)
                ? null
                : request.DocumentPhotoUrl,
            PayoutAccountLast4 = string.IsNullOrWhiteSpace(request.PayoutAccountNumber)
                ? null
                : Last4(Normalise(request.PayoutAccountNumber)),
            Status = KycStatus.Pending,
            SubmittedAt = _clock.UtcNow,
        };

        _db.KycSubmissions.Add(submission);

        // The user's own status tracks the newest attempt, because that is what every other part
        // of the system reads — a rejected host who resubmits is pending again, not rejected.
        user.KycStatus = KycStatus.Pending;

        await _db.SaveChangesAsync(cancellationToken);

        return ToView(submission);
    }

    public async Task<KycStatusView> GetMineAsync(CancellationToken cancellationToken = default)
    {
        var userId = _currentUser.RequireUserId();
        var user = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, cancellationToken);

        var latest = await _db.KycSubmissions
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.SubmittedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new KycStatusView(
            user.KycStatus.ToString(),
            user.KycStatus == KycStatus.Verified,
            latest is null ? null : ToView(latest));
    }

    public async Task<IReadOnlyList<KycSubmissionView>> GetQueueAsync(
        KycStatus? status,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        _currentUser.RequireAdmin();

        // Pending by default: the queue is a worklist, and a finished submission is history that
        // only matters when somebody goes looking for it.
        var wanted = status ?? KycStatus.Pending;

        var submissions = await _db.KycSubmissions
            .AsNoTracking()
            .Where(s => s.Status == wanted)
            .OrderBy(s => s.SubmittedAt)
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);

        if (submissions.Count == 0)
        {
            return [];
        }

        var userIds = submissions.Select(s => s.UserId).Distinct().ToList();
        var hashes = submissions.Select(s => s.DocumentHash).Distinct().ToList();

        var phones = await _db.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Phone, cancellationToken);

        // The two patterns a reviewer needs and cannot see from one row: an account that has been
        // refused before, and one document arriving under several accounts.
        var related = await _db.KycSubmissions
            .AsNoTracking()
            .Where(s => userIds.Contains(s.UserId) || hashes.Contains(s.DocumentHash))
            .Select(s => new { s.UserId, s.DocumentHash, s.Status })
            .ToListAsync(cancellationToken);

        return submissions.Select(s => ToView(
            s,
            phones.GetValueOrDefault(s.UserId),
            related.Count(r => r.UserId == s.UserId && r.Status == KycStatus.Rejected),
            related.Where(r => r.DocumentHash == s.DocumentHash)
                .Select(r => r.UserId)
                .Distinct()
                .Count(id => id != s.UserId))).ToList();
    }

    public async Task<KycSubmissionView> VerifyAsync(
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        var reviewerId = _currentUser.RequireUserId();
        _currentUser.RequireAdmin();

        var submission = await LoadPendingAsync(submissionId, cancellationToken);
        var user = await _db.Users.FirstAsync(u => u.Id == submission.UserId, cancellationToken);

        submission.Status = KycStatus.Verified;
        submission.ReviewedAt = _clock.UtcNow;
        submission.ReviewedByUserId = reviewerId;
        submission.RejectionReason = null;

        user.KycStatus = KycStatus.Verified;

        await _db.SaveChangesAsync(cancellationToken);
        await _events.PublishAsync(new KycReviewed(user.Id, true, null), cancellationToken);

        return ToView(submission);
    }

    public async Task<KycSubmissionView> RejectAsync(
        Guid submissionId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var reviewerId = _currentUser.RequireUserId();
        _currentUser.RequireAdmin();

        if (string.IsNullOrWhiteSpace(reason))
        {
            // The host reads this and has to act on it. "Rejected", unqualified, produces a
            // resubmission of exactly the same thing and a support ticket.
            throw new DomainException("A reason is required so the host knows what to fix.");
        }

        var submission = await LoadPendingAsync(submissionId, cancellationToken);
        var user = await _db.Users.FirstAsync(u => u.Id == submission.UserId, cancellationToken);

        submission.Status = KycStatus.Rejected;
        submission.ReviewedAt = _clock.UtcNow;
        submission.ReviewedByUserId = reviewerId;
        submission.RejectionReason = reason.Trim();

        user.KycStatus = KycStatus.Rejected;

        await _db.SaveChangesAsync(cancellationToken);
        await _events.PublishAsync(
            new KycReviewed(user.Id, false, submission.RejectionReason), cancellationToken);

        return ToView(submission);
    }

    private async Task<KycSubmission> LoadPendingAsync(Guid submissionId, CancellationToken cancellationToken)
    {
        var submission = await _db.KycSubmissions
            .FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken)
            ?? throw new DomainException("That submission does not exist.");

        if (submission.Status != KycStatus.Pending)
        {
            // Reviewing twice is either a double-click or two operators on one queue. Neither
            // second decision is better informed than the first.
            throw new DomainException(
                $"That submission was already {submission.Status.ToString().ToLowerInvariant()}.");
        }

        return submission;
    }

    /// <summary>Upper-cased, stripped of the spaces and dashes people type into forms.</summary>
    private static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string Last4(string value) => value.Length <= 4 ? value : value[^4..];

    /// <summary>
    /// HMAC rather than a plain digest, keyed with the configured pepper. A PAN is ten characters
    /// of known structure — an unkeyed SHA-256 of one is a lookup table away from the number
    /// itself, which would make this column worse than storing nothing.
    /// </summary>
    private string Hash(string number)
    {
        if (string.IsNullOrWhiteSpace(_options.Pepper))
        {
            throw new InvalidOperationException("Kyc:Pepper is not configured.");
        }

        var mac = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_options.Pepper),
            Encoding.UTF8.GetBytes(number));

        return Convert.ToHexString(mac);
    }

    private static KycSubmissionView ToView(
        KycSubmission submission,
        string? phone = null,
        int previousRejections = 0,
        int otherAccounts = 0) =>
        new(submission.Id,
            submission.UserId,
            submission.LegalName,
            submission.DocumentType.ToString(),
            submission.DocumentLast4,
            submission.DocumentPhotoUrl,
            submission.PayoutAccountLast4,
            submission.Status.ToString(),
            submission.SubmittedAt,
            submission.ReviewedAt,
            submission.RejectionReason,
            phone,
            previousRejections,
            otherAccounts);
}
