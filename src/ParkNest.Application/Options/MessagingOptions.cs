namespace ParkNest.Application.Options;

public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>
    /// "InProcess" dispatches to handlers in this process; "RabbitMq" goes through the broker.
    ///
    /// In-process is the default because the modules genuinely are in one process — a broker
    /// between them buys nothing until they are not, and requiring one to run the app would make
    /// every developer install RabbitMQ to see a booking confirmation.
    /// </summary>
    public string Provider { get; set; } = "InProcess";

    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "parknest";
    public string Password { get; set; } = "parknest";
    public string VirtualHost { get; set; } = "/";

    /// <summary>Topic exchange everything is published to, keyed by the event name.</summary>
    public string Exchange { get; set; } = "parknest.events";

    /// <summary>
    /// Queue this instance consumes from. Named rather than exclusive so a restart picks up what
    /// arrived while it was down, instead of the broker discarding the queue with the connection.
    /// </summary>
    public string Queue { get; set; } = "parknest.notifications";

    public bool IsRabbitMq => string.Equals(Provider, "RabbitMq", StringComparison.OrdinalIgnoreCase);
}
