using FluentAssertions;
using ParkNest.Application.Auth;
using Microsoft.EntityFrameworkCore;
using ParkNest.Domain.Analytics;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

public sealed class AuthServiceTests : IDisposable
{
    private const string Phone = "9876543210";

    /// <summary>
    /// Long enough to clear the resend cooldown. Tests that ask for a second code are describing a
    /// user who waited, not a user hammering the button — that case is covered separately below.
    /// </summary>
    private static readonly TimeSpan PastCooldown = TimeSpan.FromMinutes(2);

    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Verifying_a_code_creates_the_account_on_first_login()
    {
        await _h.Auth.RequestOtpAsync(Phone);
        var result = await _h.Auth.VerifyOtpAsync(Phone, _h.OtpSender.LastCode!);

        result.IsNewUser.Should().BeTrue();
        result.AccessToken.Should().NotBeNullOrWhiteSpace();

        var user = await _h.Db.Users.SingleAsync(u => u.Phone == Phone);
        user.Id.Should().Be(result.UserId);
    }

    [Fact]
    public async Task Logging_in_again_reuses_the_same_account()
    {
        await _h.Auth.RequestOtpAsync(Phone);
        var first = await _h.Auth.VerifyOtpAsync(Phone, _h.OtpSender.LastCode!);

        _h.Clock.Advance(PastCooldown);
        await _h.Auth.RequestOtpAsync(Phone);
        var second = await _h.Auth.VerifyOtpAsync(Phone, _h.OtpSender.LastCode!);

        second.UserId.Should().Be(first.UserId);
        second.IsNewUser.Should().BeFalse();
        (await _h.Db.Users.CountAsync(u => u.Phone == Phone)).Should().Be(1);
    }

    [Fact]
    public async Task The_raw_code_is_never_stored()
    {
        await _h.Auth.RequestOtpAsync(Phone);
        var code = _h.OtpSender.LastCode!;

        var stored = await _h.Db.OtpCodes.SingleAsync();

        stored.CodeHash.Should().NotContain(code);
    }

    [Fact]
    public async Task A_wrong_code_is_rejected()
    {
        await _h.Auth.RequestOtpAsync(Phone);
        var wrong = _h.OtpSender.LastCode == "000000" ? "111111" : "000000";

        var act = () => _h.Auth.VerifyOtpAsync(Phone, wrong);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task A_code_is_burned_after_too_many_wrong_guesses()
    {
        await _h.Auth.RequestOtpAsync(Phone);
        var correct = _h.OtpSender.LastCode!;
        var wrong = correct == "000000" ? "111111" : "000000";

        for (var i = 0; i < _h.AuthOptions.OtpMaxAttempts; i++)
        {
            await Assert.ThrowsAsync<UnauthorizedException>(() => _h.Auth.VerifyOtpAsync(Phone, wrong));
        }

        // Even the correct code must now fail — otherwise the attempt cap buys nothing.
        var act = () => _h.Auth.VerifyOtpAsync(Phone, correct);
        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task An_expired_code_is_rejected()
    {
        await _h.Auth.RequestOtpAsync(Phone);
        var code = _h.OtpSender.LastCode!;

        _h.Clock.Advance(TimeSpan.FromMinutes(_h.AuthOptions.OtpLifetimeMinutes + 1));

        var act = () => _h.Auth.VerifyOtpAsync(Phone, code);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task A_code_cannot_be_replayed()
    {
        await _h.Auth.RequestOtpAsync(Phone);
        var code = _h.OtpSender.LastCode!;

        await _h.Auth.VerifyOtpAsync(Phone, code);

        var act = () => _h.Auth.VerifyOtpAsync(Phone, code);
        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Requesting_a_new_code_invalidates_the_previous_one()
    {
        await _h.Auth.RequestOtpAsync(Phone);
        var firstCode = _h.OtpSender.LastCode!;

        _h.Clock.Advance(PastCooldown);
        await _h.Auth.RequestOtpAsync(Phone);

        var act = () => _h.Auth.VerifyOtpAsync(Phone, firstCode);
        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task A_code_issued_for_one_number_cannot_be_used_on_another()
    {
        await _h.Auth.RequestOtpAsync(Phone);
        var code = _h.OtpSender.LastCode!;

        await _h.Auth.RequestOtpAsync("9000000001");

        var act = () => _h.Auth.VerifyOtpAsync("9000000001", code);
        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    public async Task An_invalid_phone_number_is_rejected(string phone)
    {
        var act = () => _h.Auth.RequestOtpAsync(phone);
        await act.Should().ThrowAsync<DomainException>();
    }

    [Fact]
    public async Task Phone_numbers_are_normalised_so_formatting_does_not_create_duplicate_accounts()
    {
        await _h.Auth.RequestOtpAsync("+91 98765 43210");
        var first = await _h.Auth.VerifyOtpAsync("9198765 43210", _h.OtpSender.LastCode!);

        _h.Clock.Advance(PastCooldown);
        await _h.Auth.RequestOtpAsync("+919876543210");
        var second = await _h.Auth.VerifyOtpAsync("919876543210", _h.OtpSender.LastCode!);

        second.UserId.Should().Be(first.UserId);
    }

    [Fact]
    public async Task A_second_code_cannot_be_requested_inside_the_cooldown()
    {
        await _h.Auth.RequestOtpAsync(Phone);

        var act = () => _h.Auth.RequestOtpAsync(Phone);

        await act.Should().ThrowAsync<TooManyRequestsException>();
        _h.OtpSender.Sent.Should().HaveCount(1, "the second request must not reach the SMS gateway");
    }

    [Fact]
    public async Task The_cooldown_lifts_once_it_has_elapsed()
    {
        await _h.Auth.RequestOtpAsync(Phone);

        _h.Clock.Advance(TimeSpan.FromSeconds(_h.AuthOptions.OtpResendCooldownSeconds));
        await _h.Auth.RequestOtpAsync(Phone);

        _h.OtpSender.Sent.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_number_cannot_be_sent_more_codes_than_the_window_allows()
    {
        for (var i = 0; i < _h.AuthOptions.OtpMaxRequestsPerWindow; i++)
        {
            await _h.Auth.RequestOtpAsync(Phone);
            _h.Clock.Advance(PastCooldown);
        }

        var act = () => _h.Auth.RequestOtpAsync(Phone);

        await act.Should().ThrowAsync<TooManyRequestsException>();
        _h.OtpSender.Sent.Should().HaveCount(_h.AuthOptions.OtpMaxRequestsPerWindow);
    }

    [Fact]
    public async Task The_window_rolls_so_a_blocked_number_is_not_blocked_forever()
    {
        for (var i = 0; i < _h.AuthOptions.OtpMaxRequestsPerWindow; i++)
        {
            await _h.Auth.RequestOtpAsync(Phone);
            _h.Clock.Advance(PastCooldown);
        }

        _h.Clock.Advance(TimeSpan.FromMinutes(_h.AuthOptions.OtpRequestWindowMinutes));
        await _h.Auth.RequestOtpAsync(Phone);

        _h.OtpSender.Sent.Should().HaveCount(_h.AuthOptions.OtpMaxRequestsPerWindow + 1);
    }

    [Fact]
    public async Task The_limit_is_per_number_so_one_flooded_number_does_not_lock_out_another()
    {
        await _h.Auth.RequestOtpAsync(Phone);

        await _h.Auth.RequestOtpAsync("9000000002");

        _h.OtpSender.Sent.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_throttled_request_says_when_to_come_back()
    {
        await _h.Auth.RequestOtpAsync(Phone);

        var thrown = await ((Func<Task>)(() => _h.Auth.RequestOtpAsync(Phone)))
            .Should().ThrowAsync<TooManyRequestsException>();

        thrown.Which.RetryAfter.Should().BePositive()
            .And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(_h.AuthOptions.OtpResendCooldownSeconds));
    }


    [Fact]
    public async Task Signing_in_returns_a_refresh_token_alongside_the_access_token()
    {
        await _h.Auth.RequestOtpAsync(Phone);
        var result = await _h.Auth.VerifyOtpAsync(Phone, _h.OtpSender.LastCode!);

        result.RefreshToken.Should().NotBeNullOrWhiteSpace();
        result.RefreshExpiresAt.Should().BeAfter(result.ExpiresAt,
            "the point of a refresh token is to outlive the access token it replaces");
    }

    [Fact]
    public async Task The_raw_refresh_token_is_never_stored()
    {
        var session = await SignInAsync();

        var stored = await _h.Db.RefreshTokens.SingleAsync();

        stored.TokenHash.Should().NotBe(session.RefreshToken);
        stored.TokenHash.Should().NotContain(session.RefreshToken);
    }

    [Fact]
    public async Task A_refresh_token_buys_a_new_access_token()
    {
        var session = await SignInAsync();

        _h.Clock.Advance(TimeSpan.FromMinutes(1));
        var refreshed = await _h.Auth.RefreshAsync(session.RefreshToken);

        refreshed.UserId.Should().Be(session.UserId);
        refreshed.AccessToken.Should().NotBeNullOrWhiteSpace();
        refreshed.RefreshToken.Should().NotBe(session.RefreshToken, "every use rotates the token");
    }

    [Fact]
    public async Task The_old_refresh_token_stops_working_once_it_has_been_used()
    {
        var session = await SignInAsync();
        await _h.Auth.RefreshAsync(session.RefreshToken);

        var act = () => _h.Auth.RefreshAsync(session.RefreshToken);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Replaying_a_spent_token_ends_the_whole_session()
    {
        // The honest client and the thief both hold a token from the same chain, and we cannot
        // tell which is which — so neither keeps their session.
        var session = await SignInAsync();
        var second = await _h.Auth.RefreshAsync(session.RefreshToken);

        var replay = () => _h.Auth.RefreshAsync(session.RefreshToken);
        await replay.Should().ThrowAsync<UnauthorizedException>();

        var act = () => _h.Auth.RefreshAsync(second.RefreshToken);
        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task An_expired_refresh_token_is_refused()
    {
        var session = await SignInAsync();

        _h.Clock.Advance(TimeSpan.FromDays(_h.AuthOptions.RefreshTokenLifetimeDays + 1));

        var act = () => _h.Auth.RefreshAsync(session.RefreshToken);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task A_made_up_refresh_token_is_refused()
    {
        await SignInAsync();

        var act = () => _h.Auth.RefreshAsync("not-a-real-token");

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        var session = await SignInAsync();

        await _h.Auth.RevokeAsync(session.RefreshToken);

        var act = () => _h.Auth.RefreshAsync(session.RefreshToken);
        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Signing_out_ends_every_token_descended_from_that_sign_in()
    {
        var session = await SignInAsync();
        var refreshed = await _h.Auth.RefreshAsync(session.RefreshToken);

        await _h.Auth.RevokeAsync(refreshed.RefreshToken);

        var act = () => _h.Auth.RefreshAsync(refreshed.RefreshToken);
        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Signing_out_with_an_unknown_token_is_not_an_error()
    {
        // The caller wanted to be signed out and they are. Reporting otherwise only tells someone
        // probing which tokens exist.
        var act = () => _h.Auth.RevokeAsync("not-a-real-token");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Signing_out_of_one_session_leaves_another_alone()
    {
        var phone = "9000000003";
        var first = await SignInAsync();

        await _h.Auth.RequestOtpAsync(phone);
        var second = await _h.Auth.VerifyOtpAsync(phone, _h.OtpSender.LastCode!);

        await _h.Auth.RevokeAsync(first.RefreshToken);

        var refreshed = await _h.Auth.RefreshAsync(second.RefreshToken);
        refreshed.UserId.Should().Be(second.UserId);
    }

    private const string Email = "renter@example.com";

    [Fact]
    public async Task Signing_in_by_email_creates_an_account_with_no_phone()
    {
        await _h.Auth.RequestOtpAsync(Email);
        var result = await _h.Auth.VerifyOtpAsync(Email, _h.EmailSender.LastCode!);

        result.IsNewUser.Should().BeTrue();

        var user = await _h.Db.Users.SingleAsync(u => u.Id == result.UserId);
        user.Email.Should().Be(Email);
        user.Phone.Should().BeNull();

        _h.OtpSender.Sent.Should().BeEmpty("an email address must never be handed to the SMS sender");
    }

    [Fact]
    public async Task Email_addresses_are_normalised_so_case_and_spacing_do_not_create_duplicate_accounts()
    {
        await _h.Auth.RequestOtpAsync("  Renter@Example.COM ");
        var first = await _h.Auth.VerifyOtpAsync("renter@example.com", _h.EmailSender.LastCode!);

        _h.Clock.Advance(PastCooldown);
        await _h.Auth.RequestOtpAsync(Email);
        var second = await _h.Auth.VerifyOtpAsync("RENTER@example.com", _h.EmailSender.LastCode!);

        second.UserId.Should().Be(first.UserId);
        second.IsNewUser.Should().BeFalse();
    }

    [Theory]
    [InlineData("renter@")]
    [InlineData("@example.com")]
    [InlineData("renter@@example.com")]
    [InlineData("renter@localhost")]
    [InlineData("Renter <renter@example.com>")]
    public async Task An_invalid_email_address_is_rejected(string email)
    {
        var act = () => _h.Auth.RequestOtpAsync(email);

        await act.Should().ThrowAsync<DomainException>();
        _h.EmailSender.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_code_sent_to_an_email_cannot_be_used_for_a_phone_number()
    {
        await _h.Auth.RequestOtpAsync(Email);
        var code = _h.EmailSender.LastCode!;

        await _h.Auth.RequestOtpAsync(Phone);

        var act = () => _h.Auth.VerifyOtpAsync(Phone, code);
        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task The_resend_cooldown_follows_the_normalised_address_not_its_spelling()
    {
        await _h.Auth.RequestOtpAsync(Email);

        var act = () => _h.Auth.RequestOtpAsync(Email.ToUpperInvariant());

        await act.Should().ThrowAsync<TooManyRequestsException>();
        _h.EmailSender.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task Phone_sign_in_is_refused_when_no_sms_provider_is_configured()
    {
        // What Production looks like until an SMS provider is paid for: email only.
        var emailOnly = new AuthService(
            _h.Db,
            new FakeTokenService(_h.Clock),
            new IOtpSender[] { _h.EmailSender },
            _h.Clock,
            Microsoft.Extensions.Options.Options.Create(_h.AuthOptions),
            _h.Analytics,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthService>.Instance);

        var act = () => emailOnly.RequestOtpAsync(Phone);

        await act.Should().ThrowAsync<DomainException>().WithMessage("*email*");
        (await _h.Db.OtpCodes.CountAsync()).Should().Be(0, "a code nobody can receive must not be issued");
    }

    [Fact]
    public async Task An_address_on_the_admin_list_signs_up_as_admin_whatever_its_case()
    {
        _h.AuthOptions.AdminEmails = new[] { "Ops@Example.com" };

        await _h.Auth.RequestOtpAsync("ops@example.com");
        var result = await _h.Auth.VerifyOtpAsync("ops@example.com", _h.EmailSender.LastCode!);

        result.Role.Should().Be(UserRole.Admin);
    }

    [Fact]
    public async Task An_account_that_already_existed_is_promoted_when_its_address_joins_the_admin_list()
    {
        // The order people actually do this in: sign in first, add your address to the list
        // afterwards. Reading the list only at account creation would leave the owner a
        // permanent non-admin on their own platform.
        await _h.Auth.RequestOtpAsync("owner@example.com");
        var before = await _h.Auth.VerifyOtpAsync("owner@example.com", _h.EmailSender.LastCode!);
        before.Role.Should().Be(UserRole.Both);

        _h.AuthOptions.AdminEmails = new[] { "owner@example.com" };
        _h.Clock.Advance(PastCooldown);

        await _h.Auth.RequestOtpAsync("owner@example.com");
        var after = await _h.Auth.VerifyOtpAsync("owner@example.com", _h.EmailSender.LastCode!);

        after.Role.Should().Be(UserRole.Admin);
    }

    [Fact]
    public async Task Signing_in_is_counted_and_the_address_is_not_recorded()
    {
        await _h.Auth.RequestOtpAsync("renter@example.com");
        await _h.Auth.VerifyOtpAsync("renter@example.com", _h.EmailSender.LastCode!);

        var recorded = await _h.Db.AnalyticsEvents.ToListAsync();

        recorded.Select(e => e.Name).Should().Contain(new[]
        {
            AnalyticsEventNames.SignInRequested,
            AnalyticsEventNames.SignedUp
        });

        // The channel is worth knowing; who it was sent to is not, and a table of every address
        // that ever asked for a code is the one thing this must never become.
        recorded.Should().OnlyContain(e => e.Detail == null || e.Detail == "Email");
    }

    private async Task<AuthResult> SignInAsync()
    {
        await _h.Auth.RequestOtpAsync(Phone);
        return await _h.Auth.VerifyOtpAsync(Phone, _h.OtpSender.LastCode!);
    }

}
