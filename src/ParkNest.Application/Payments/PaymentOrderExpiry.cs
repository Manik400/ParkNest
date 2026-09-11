using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Domain.Payments;

namespace ParkNest.Application.Payments;

/// <summary>
/// Closes out orders the user walked away from.
///
/// A checkout that is abandoned — tab closed, app killed, gateway never called back — leaves a
/// <see cref="PaymentOrderStatus.Created"/> row that nothing else will ever touch. Left alone they
/// accumulate forever and make "pending recharges" a meaningless number, which matters because
/// that is exactly the figure someone reconciling against the gateway's settlement report reads.
///
/// Deliberately separate from <see cref="IPaymentService"/>: this runs on a timer with nobody
/// signed in, and a service that takes its acting user from an <see cref="ICurrentUser"/> has no
/// business being resolved from a background loop.
/// </summary>
public interface IPaymentOrderExpiry
{
    /// <summary>Cancels every order left unpaid past the expiry window. Returns how many.</summary>
    Task<int> ExpireStaleOrdersAsync(CancellationToken cancellationToken = default);
}

public sealed class PaymentOrderExpiry : IPaymentOrderExpiry
{
    private readonly IParkNestDbContext _db;
    private readonly IClock _clock;
    private readonly PaymentOptions _options;
    private readonly ILogger<PaymentOrderExpiry> _logger;

    public PaymentOrderExpiry(
        IParkNestDbContext db,
        IClock clock,
        IOptions<PaymentOptions> options,
        ILogger<PaymentOrderExpiry> logger)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> ExpireStaleOrdersAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var cutoff = now.AddMinutes(-_options.OrderExpiryMinutes);

        var stale = await _db.PaymentOrders
            .Where(o => o.Status == PaymentOrderStatus.Created && o.CreatedAt < cutoff)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return 0;
        }

        foreach (var order in stale)
        {
            order.Status = PaymentOrderStatus.Cancelled;
            order.FailureReason = "Expired without payment.";
            order.CompletedAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Expired {Count} payment orders left unpaid past the window.", stale.Count);

        return stale.Count;
    }
}
