using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Options;
using ParkNest.Application.Payments;

namespace ParkNest.Infrastructure.Payments;

/// <summary>
/// Runs <see cref="IPaymentOrderExpiry"/> on a timer.
///
/// A plain hosted service rather than a scheduler dependency: the work is idempotent and cheap, so
/// two instances of the API both running it collide harmlessly — the second finds nothing left to
/// expire. That is worth more than exactly-once scheduling would be here.
/// </summary>
public sealed class PaymentOrderExpirySweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly PaymentOptions _options;
    private readonly ILogger<PaymentOrderExpirySweeper> _logger;

    public PaymentOrderExpirySweeper(
        IServiceScopeFactory scopes,
        IOptions<PaymentOptions> options,
        ILogger<PaymentOrderExpirySweeper> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.ExpirySweepIntervalMinutes));
        using var timer = new PeriodicTimer(interval);

        _logger.LogInformation(
            "Payment order expiry sweep running every {Interval}, cancelling orders unpaid after {Window} minutes.",
            interval, _options.OrderExpiryMinutes);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                // A scope per pass: the DbContext is scoped and one held for the process lifetime
                // would accumulate every order it ever tracked.
                using var scope = _scopes.CreateScope();
                var expiry = scope.ServiceProvider.GetRequiredService<IPaymentOrderExpiry>();
                await expiry.ExpireStaleOrdersAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed pass must not kill the loop — the database being briefly unreachable is
                // a reason to try again in five minutes, not to stop sweeping until the next deploy.
                _logger.LogError(ex, "Payment order expiry sweep failed. Retrying next interval.");
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown, not a fault.
            return false;
        }
    }
}
