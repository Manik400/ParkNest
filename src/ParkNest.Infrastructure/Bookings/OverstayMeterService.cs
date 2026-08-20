using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Bookings;
using ParkNest.Application.Options;

namespace ParkNest.Infrastructure.Bookings;

/// <summary>
/// Runs <see cref="IOverstayMeter"/> on a timer.
///
/// The interval is a fraction of the billing increment on purpose: the meter can only ever collect
/// whole increments, so ticking faster than that just finds nothing to do, and ticking slower means
/// an over-run that has already crossed an increment sits uncharged.
///
/// Two instances running this at once is safe rather than merely tolerable — each tick's
/// idempotency key is derived from the increment it covers, so the second instance's debit resolves
/// to the transaction the first already wrote.
/// </summary>
public sealed class OverstayMeterService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly PlatformOptions _options;
    private readonly ILogger<OverstayMeterService> _logger;

    public OverstayMeterService(
        IServiceScopeFactory scopes,
        IOptions<PlatformOptions> options,
        ILogger<OverstayMeterService> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.BillingIncrementMinutes / 3));
        using var timer = new PeriodicTimer(interval);

        _logger.LogInformation(
            "Overstay meter running every {Interval}, starting {Grace} minutes after a booked end.",
            interval, _options.OverstayGraceMinutes);

        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var meter = scope.ServiceProvider.GetRequiredService<IOverstayMeter>();

                await meter.MeterOpenSessionsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed pass must not end the loop. The database being briefly unreachable is a
                // reason to try again shortly, not to stop billing over-runs until the next deploy.
                _logger.LogError(ex, "Overstay meter pass failed. Retrying next interval.");
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
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
