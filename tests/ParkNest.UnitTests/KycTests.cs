using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ParkNest.Application.Options;
using ParkNest.Application.Users;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

/// <summary>
/// Identity verification, and the thing it unlocks.
///
/// The property that matters most here is the one that was missing entirely: a host who submits
/// and is approved can cash out, and until an operator approves them they cannot. Everything else
/// is about what the reviewer is shown, because approving is how real money leaves the platform.
/// </summary>
public sealed class KycTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly KycService _kyc;

    public KycTests()
    {
        _kyc = new KycService(
            _h.Db,
            _h.CurrentUser,
            _h.Clock,
            _h.Events,
            Options.Create(new KycOptions
            {
                Pepper = "test-kyc-pepper-value",
                // Storage is not wired in this suite, and requiring a photograph here would make
                // every test about uploads rather than about the decision.
                RequireDocumentPhoto = false,
            }));
    }

    public void Dispose() => _h.Dispose();

    private async Task<Guid> SignedInHostAsync()
    {
        var host = await _h.AddUserAsync(UserRole.Host);
        _h.CurrentUser.SignIn(host.Id);
        return host.Id;
    }

    private async Task<Guid> SignedInAdminAsync()
    {
        var admin = await _h.AddUserAsync(UserRole.Admin);
        _h.CurrentUser.SignIn(admin.Id, UserRole.Admin);
        return admin.Id;
    }

    private static SubmitKyc Details(string number = "ABCDE1234F") =>
        new("Asha Menon", KycDocumentType.Pan, number, PayoutAccountNumber: "50100123456789");

    [Fact]
    public async Task Verifying_a_submission_is_what_opens_cash_out()
    {
        var hostId = await SignedInHostAsync();
        var submission = await _kyc.SubmitAsync(Details());

        (await _h.Db.Users.AsNoTracking().SingleAsync(u => u.Id == hostId)).KycStatus
            .Should().Be(KycStatus.Pending);

        await SignedInAdminAsync();
        await _kyc.VerifyAsync(submission.Id);

        // The whole point of this module. Before it existed, the cash-out check could refuse
        // everybody and nothing in the system could ever change that.
        (await _h.Db.Users.AsNoTracking().SingleAsync(u => u.Id == hostId)).KycStatus
            .Should().Be(KycStatus.Verified);
    }

    [Fact]
    public async Task The_document_number_itself_is_never_stored()
    {
        await SignedInHostAsync();
        await _kyc.SubmitAsync(Details("ABCDE1234F"));

        var stored = await _h.Db.KycSubmissions.AsNoTracking().SingleAsync();

        // Four characters for a human to match against the photograph, and a keyed hash for
        // spotting the same document twice. Holding the whole number would mean keeping identity
        // documents for every host in a table that does not need them.
        stored.DocumentLast4.Should().Be("234F");
        stored.DocumentHash.Should().NotContain("ABCDE1234F");
        stored.DocumentHash.Should().HaveLength(64);
        stored.PayoutAccountLast4.Should().Be("6789");
    }

    [Fact]
    public async Task The_same_number_typed_untidily_hashes_the_same()
    {
        await SignedInHostAsync();
        await _kyc.SubmitAsync(Details("ABCDE1234F"));
        var first = await _h.Db.KycSubmissions.AsNoTracking().SingleAsync();

        await SignedInHostAsync();
        await _kyc.SubmitAsync(Details(" abcde-1234 f "));
        var second = await _h.Db.KycSubmissions.AsNoTracking()
            .OrderByDescending(s => s.SubmittedAt).FirstAsync();

        // Otherwise "one document, two accounts" is defeated by a space bar.
        second.DocumentHash.Should().Be(first.DocumentHash);
    }

    [Fact]
    public async Task Resubmitting_replaces_the_attempt_still_waiting()
    {
        await SignedInHostAsync();
        await _kyc.SubmitAsync(Details());
        await _kyc.SubmitAsync(Details("ZZZZZ9999Z"));

        // Two pending rows for one host is a queue an operator has to reconcile, and the newer
        // one is always the one the host means.
        var pending = await _h.Db.KycSubmissions
            .AsNoTracking()
            .Where(s => s.Status == KycStatus.Pending)
            .ToListAsync();

        pending.Should().ContainSingle().Which.DocumentLast4.Should().Be("999Z");
    }

    [Fact]
    public async Task A_rejection_keeps_its_reason_and_survives_the_next_attempt()
    {
        var hostId = await SignedInHostAsync();
        var submission = await _kyc.SubmitAsync(Details());

        await SignedInAdminAsync();
        await _kyc.RejectAsync(submission.Id, "The photograph is too blurred to read.");

        _h.CurrentUser.SignIn(hostId);
        var mine = await _kyc.GetMineAsync();

        mine.CanCashOut.Should().BeFalse();
        mine.Latest!.RejectionReason.Should().Contain("blurred");

        await _kyc.SubmitAsync(Details("QWERT5678Y"));

        // The rejected row stays. "This account has been refused three times" is exactly what the
        // next reviewer needs, and a single mutable record would have erased it.
        (await _h.Db.KycSubmissions.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task The_queue_tells_a_reviewer_what_one_row_cannot()
    {
        var firstHost = await SignedInHostAsync();
        var refused = await _kyc.SubmitAsync(Details("ABCDE1234F"));

        await SignedInAdminAsync();
        await _kyc.RejectAsync(refused.Id, "Name does not match the document.");

        _h.CurrentUser.SignIn(firstHost);
        await _kyc.SubmitAsync(Details("ABCDE1234F"));

        // A second account sends the very same document.
        await SignedInHostAsync();
        await _kyc.SubmitAsync(Details("ABCDE1234F"));

        await SignedInAdminAsync();
        var queue = await _kyc.GetQueueAsync(null, 50, 0);

        queue.Should().HaveCount(2);
        queue.Should().OnlyContain(s => s.OtherAccountsWithThisDocument == 1);
        queue.Should().Contain(s => s.UserId == firstHost && s.PreviousRejections == 1);
    }

    [Fact]
    public async Task Only_an_admin_may_decide()
    {
        await SignedInHostAsync();
        var submission = await _kyc.SubmitAsync(Details());

        var act = () => _kyc.VerifyAsync(submission.Id);

        // The host would otherwise verify themselves, which makes the whole queue decorative.
        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task A_decision_is_made_once()
    {
        await SignedInHostAsync();
        var submission = await _kyc.SubmitAsync(Details());

        await SignedInAdminAsync();
        await _kyc.VerifyAsync(submission.Id);

        var act = () => _kyc.RejectAsync(submission.Id, "Changed my mind.");

        // Two operators on one queue, or one double-click. Neither second decision is better
        // informed than the first.
        await act.Should().ThrowAsync<DomainException>().WithMessage("*already verified*");
    }

    [Fact]
    public async Task A_verified_host_is_not_asked_again()
    {
        await SignedInHostAsync();
        var submission = await _kyc.SubmitAsync(Details());

        await SignedInAdminAsync();
        await _kyc.VerifyAsync(submission.Id);

        _h.CurrentUser.SignIn((await _h.Db.KycSubmissions.AsNoTracking().SingleAsync()).UserId);
        var act = () => _kyc.SubmitAsync(Details());

        await act.Should().ThrowAsync<DomainException>().WithMessage("*already verified*");
    }
}
