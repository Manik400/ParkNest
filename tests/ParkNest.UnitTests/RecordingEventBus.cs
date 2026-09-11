using ParkNest.Application.Abstractions;

namespace ParkNest.UnitTests;

/// <summary>
/// Keeps what was published instead of sending it anywhere.
///
/// Synchronous on purpose, unlike the real in-process bus: a test asserting that ending a session
/// announced it should not have to wait for a background task, and a race in the harness would
/// show up as a flaky suite rather than a bug.
/// </summary>
public sealed class RecordingEventBus : IEventBus
{
    // Held as object because IIntegrationEvent declares a static abstract member, which makes it
    // unusable as a type argument. The events are records, so the concrete types are what tests
    // want to filter on anyway.
    private readonly List<object> _published = new();

    public IReadOnlyList<object> Published => _published;

    public IEnumerable<T> OfType<T>() => _published.OfType<T>();

    public T Single<T>() => _published.OfType<T>().Single();

    public bool Any<T>() => _published.OfType<T>().Any();

    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent
    {
        _published.Add(@event);
        return Task.CompletedTask;
    }
}
