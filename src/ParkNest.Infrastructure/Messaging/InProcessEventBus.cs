using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ParkNest.Application.Abstractions;

namespace ParkNest.Infrastructure.Messaging;

/// <summary>
/// Delivers events to handlers in this process, on a background task.
///
/// The default, and not a placeholder. It is the honest shape of a modular monolith: the modules
/// are already in one process, so a broker between them buys nothing until they are not — and
/// requiring one to run the app would make every developer install RabbitMQ to see a booking
/// confirmation. Switching to the broker is a configuration change, exactly as with the payment
/// gateway (ADR 0006).
///
/// What it deliberately does *not* do is deliver synchronously. A notification handler that throws,
/// or takes two seconds, must not fail or slow the checkout that caused it — publishing is an
/// announcement about something that already happened.
/// </summary>
public sealed class InProcessEventBus : IEventBus
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<InProcessEventBus> _logger;

    public InProcessEventBus(IServiceScopeFactory scopes, ILogger<InProcessEventBus> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent
    {
        // Not awaited on purpose, and with its own scope: the publisher's DbContext and its
        // transaction belong to the request, and a handler running after that request has finished
        // must not reach into either.
        _ = Task.Run(() => DispatchAsync(@event, CancellationToken.None), CancellationToken.None);

        return Task.CompletedTask;
    }

    private async Task DispatchAsync<TEvent>(TEvent @event, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var handlers = scope.ServiceProvider.GetServices<IEventHandler<TEvent>>();

            foreach (var handler in handlers)
            {
                try
                {
                    await handler.HandleAsync(@event, cancellationToken);
                }
                catch (Exception ex)
                {
                    // One handler failing must not stop the others. They are independent
                    // subscribers to the same fact, not steps in a sequence.
                    _logger.LogError(
                        ex,
                        "Handler {Handler} failed on {Event}.",
                        handler.GetType().Name,
                        TEvent.EventName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not dispatch {Event}.", TEvent.EventName);
        }
    }
}
