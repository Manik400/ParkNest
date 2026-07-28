namespace ParkNest.Application.Abstractions;

/// <summary>Injected so billing tests can advance time without sleeping.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
