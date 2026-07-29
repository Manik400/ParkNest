using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

public sealed class AuthServiceTests : IDisposable
{
    private const string Phone = "9876543210";

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

        await _h.Auth.RequestOtpAsync("+919876543210");
        var second = await _h.Auth.VerifyOtpAsync("919876543210", _h.OtpSender.LastCode!);

        second.UserId.Should().Be(first.UserId);
    }
}
