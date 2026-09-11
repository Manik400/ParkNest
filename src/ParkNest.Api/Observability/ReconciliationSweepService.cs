using Microsoft.EntityFrameworkCore;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Wallets;
using ParkNest.Domain.Common;

namespace ParkNest.Api.Observability;

/// <summary>
/// Replays every wallet from its ledger entries and reports how far the stored balances have
/// drifted.
///
/// The whole credit model rests on the balances being a faithful projection of an append-only
/// ledger. That is asserted in tests and enforced by database constraints, but neither of those
/// watches production — and a drift is exactly the kind of fault that is silent until someone
/// complains about a number. This turns it into a gauge that can be alerted on.
///
/// Read-only by design. It reports the drift and never corrects it: quietly rewriting a balance to
/// match would erase the evidence of whatever caused the gap, and the gap is the interesting part.
/// </summary>
public sealed class ReconciliationSweepService : BackgroundService
{
    /// <summary>
    /// Hourly. The sweep reads every wallet's entries, so it is not free, and drift does not
    /// appear and vanish between one minute and the next — an hour is soon enough to catch it and
    /// slow enough to stay cheap.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ReconciliationSweepService> _logger;

    public ReconciliationSweepService(IServiceScopeFactory scopes, ILogger<ReconciliationSweepService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        // Once at startup, before waiting an hour. A deployment that introduced drift should not
        // take an hour to say so.
        await SweepAsync(stoppingToken);

        while (await WaitAsync(timer, stoppingToken))
        {
            await SweepAsync(stoppingToken);
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IParkNestDbContext>();
            var ledger = scope.ServiceProvider.GetRequiredService<ILedgerService>();

            var wallets = await db.Wallets.AsNoTracking().ToListAsync(cancellationToken);

            decimal drift = 0m;

            foreach (var wallet in wallets)
            {
                var replayed = await ledger.RecomputeFromEntriesAsync(wallet.Id, cancellationToken);

                var gap = Math.Abs(replayed.Spendable - wallet.SpendableBalance)
                          + Math.Abs(replayed.Held - wallet.HeldBalance)
                          + Math.Abs(replayed.Earning - wallet.EarningBalance);

                if (gap > 0m)
                {
                    // Named in the log even though it is not in the metric label: an operator
                    // chasing an alert needs to know which wallet, and a log line is where an id
                    // belongs. A label would make it a permanent time series.
                    _logger.LogError(
                        "Wallet {WalletId} has drifted from its ledger by {Gap} credits.",
                        wallet.Id, Money.Round(gap));
                }

                drift += gap;
            }

            ParkNestMetrics.ReconciliationDrift.Set((double)Money.Round(drift));
            ParkNestMetrics.WalletsChecked.Set(wallets.Count);

            if (drift == 0m)
            {
                _logger.LogInformation("Reconciliation clean across {Count} wallets.", wallets.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The gauge is deliberately left at its previous value rather than zeroed. Zero means
            // "checked and clean", and a failed sweep has not checked anything.
            _logger.LogError(ex, "Reconciliation sweep failed.");
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
            return false;
        }
    }
}
