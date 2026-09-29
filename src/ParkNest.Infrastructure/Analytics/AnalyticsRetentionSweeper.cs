using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Analytics;
using ParkNest.Application.Options;

namespace ParkNest.Infrastructure.Analytics;

/// <summary>
/// Runs <see cref="IAnalyticsRetention"/> on a timer, the same way the payment expiry sweep runs.
///
/// Idempotent, so two instances both running it collide harmlessly — the second finds nothing
/// left to delete. The first pass waits out the interval rather than firing at start-up: a
/// container that restarts often would otherwise spend its first seconds deleting, and there is
/// never anything urgent about a row a year old.
/// </summary>
public sealed class AnalyticsRetentionSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly AnalyticsOptions _options;
    private readonly ILogger<AnalyticsRetentionSweeper> _logger;

    public AnalyticsRetentionSweeper(
        IServiceScopeFactory scopes,
        IOptions<AnalyticsOptions> options,
        ILogger<AnalyticsRetentionSweeper> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.RetentionDays <= 0)
        {
            _logger.LogInformation("Analytics retention is off; events are kept indefinitely.");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, _options.RetentionSweepHours));
        using var timer = new PeriodicTimer(interval);

        _logger.LogInformation(
            "Analytics retention sweep running every {Interval}, keeping {Days} days.",
            interval, _options.RetentionDays);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var retention = scope.ServiceProvider.GetRequiredService<IAnalyticsRetention>();
                await retention.PruneAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed pass is a reason to try again tomorrow, not to stop sweeping until the
                // next deploy.
                _logger.LogError(ex, "Analytics retention sweep failed. Retrying next interval.");
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
