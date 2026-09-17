using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;

namespace ParkNest.Infrastructure.Messaging;

/// <summary>
/// Publishes events to a topic exchange, keyed by event name.
///
/// A topic exchange rather than a queue per consumer, so a module can subscribe to
/// <c>booking.*</c> without anyone changing the publisher. The publisher's job ends at the
/// exchange; who listens is not its business.
///
/// One connection for the lifetime of the process, because opening one per publish is a TCP
/// handshake and an AMQP negotiation to announce that a session ended.
/// </summary>
public sealed class RabbitMqEventBus : IEventBus, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly MessagingOptions _options;
    private readonly ILogger<RabbitMqEventBus> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IConnection? _connection;
    private IModel? _channel;

    public RabbitMqEventBus(IOptions<MessagingOptions> options, ILogger<RabbitMqEventBus> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent
    {
        try
        {
            var channel = await EnsureChannelAsync(cancellationToken);

            var body = JsonSerializer.SerializeToUtf8Bytes(@event, @event.GetType(), Json);

            var properties = channel.CreateBasicProperties();
            // Survives a broker restart. These events drive notifications and live updates about
            // money that has already moved; losing them silently is worse than the disk cost.
            properties.Persistent = true;
            properties.ContentType = "application/json";
            properties.Type = TEvent.EventName;
            properties.MessageId = Guid.NewGuid().ToString();
            properties.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

            channel.BasicPublish(_options.Exchange, TEvent.EventName, properties, body);
        }
        catch (Exception ex)
        {
            // Swallowed by contract. The caller has already settled a booking; it cannot undo that
            // because the broker is unreachable, and throwing here would turn a messaging outage
            // into failed checkouts. Loud in the log, invisible to the renter.
            _logger.LogError(ex, "Could not publish {Event} to RabbitMQ.", TEvent.EventName);
        }
    }

    private async Task<IModel> EnsureChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            // Reconnecting is the normal case here, not an exceptional one: brokers restart, and
            // the previous channel is simply gone.
            _channel?.Dispose();
            _connection?.Dispose();

            var factory = new ConnectionFactory
            {
                HostName = _options.HostName,
                Port = _options.Port,
                UserName = _options.UserName,
                Password = _options.Password,
                VirtualHost = _options.VirtualHost,
                AutomaticRecoveryEnabled = true,
                DispatchConsumersAsync = true,
            };

            _connection = factory.CreateConnection("parknest-publisher");
            _channel = _connection.CreateModel();

            // Declared by both publisher and consumer. Whichever starts first wins, and neither has
            // to depend on the other having been deployed.
            _channel.ExchangeDeclare(_options.Exchange, ExchangeType.Topic, durable: true);

            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        _gate.Dispose();
    }
}
