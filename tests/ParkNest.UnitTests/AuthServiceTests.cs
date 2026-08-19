using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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

}
