using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;

namespace ParkNest.Infrastructure.Messaging;

/// <summary>
/// Consumes the exchange and hands each message to the handlers registered for its type.
///
/// Only runs when RabbitMQ is the selected provider — with the in-process bus the handlers are
/// invoked directly and a consumer would deliver everything twice.
/// </summary>
public sealed class RabbitMqConsumerService : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Event name to the concrete type, so a message can be turned back into something the
    /// handlers understand. Explicit rather than reflected from the assembly: a wire format should
    /// change because someone decided to, not because a class was renamed.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Type> EventTypes = new Dictionary<string, Type>
    {
        [BookingCreated.EventName] = typeof(BookingCreated),
        [SessionStarted.EventName] = typeof(SessionStarted),
        [SessionEnded.EventName] = typeof(SessionEnded),
        [BookingCancelled.EventName] = typeof(BookingCancelled),
        [OverstayCharged.EventName] = typeof(OverstayCharged),
        [NextSlotBlocked.EventName] = typeof(NextSlotBlocked),
        [WalletChanged.EventName] = typeof(WalletChanged),
        [DisputeRaised.EventName] = typeof(DisputeRaised),
        [DisputeResolved.EventName] = typeof(DisputeResolved),
    };

    private readonly IServiceScopeFactory _scopes;
    private readonly MessagingOptions _options;
    private readonly ILogger<RabbitMqConsumerService> _logger;

    private IConnection? _connection;
    private IModel? _channel;

    public RabbitMqConsumerService(
        IServiceScopeFactory scopes,
        IOptions<MessagingOptions> options,
        ILogger<RabbitMqConsumerService> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            Connect();
        }
        catch (Exception ex)
        {
            // The API must still serve requests with the broker down. Notifications are the thing
            // that degrades, not bookings.
            _logger.LogError(ex, "Could not start the RabbitMQ consumer. Events will not be handled.");
        }

        return Task.CompletedTask;
    }

    private void Connect()
    {
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

        _connection = factory.CreateConnection("parknest-consumer");
        _channel = _connection.CreateModel();

        _channel.ExchangeDeclare(_options.Exchange, ExchangeType.Topic, durable: true);
        _channel.QueueDeclare(_options.Queue, durable: true, exclusive: false, autoDelete: false);
        _channel.QueueBind(_options.Queue, _options.Exchange, routingKey: "#");

        // One unacknowledged message at a time. These handlers write notifications and push socket
        // updates; there is nothing to gain from buffering a hundred of them into one process
        // while another instance sits idle.
        _channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += OnReceivedAsync;

        _channel.BasicConsume(_options.Queue, autoAck: false, consumer);

        _logger.LogInformation(
            "Consuming {Exchange} into {Queue} at {Host}.", _options.Exchange, _options.Queue, _options.HostName);
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs args)
    {
        var name = args.BasicProperties.Type ?? args.RoutingKey;

        try
        {
            if (!EventTypes.TryGetValue(name, out var type))
            {
                // Something newer than this instance published it. Acknowledged rather than
                // requeued: sending it back only to fail again is a loop, and a message we do not
                // understand is not one we will understand on the second try.
                _logger.LogWarning("Ignoring unknown event {Event}.", name);
                _channel?.BasicAck(args.DeliveryTag, multiple: false);
                return;
            }

            var @event = JsonSerializer.Deserialize(args.Body.Span, type, Json);

            if (@event is not null)
            {
                await DispatchAsync(type, @event);
            }

            _channel?.BasicAck(args.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed handling {Event}. Dropping it.", name);

            // Not requeued. A handler that threw on this message will almost certainly throw
            // again, and an endlessly redelivered message starves everything behind it. A dead
            // letter exchange is the right home for these once there is somewhere to review them.
            _channel?.BasicNack(args.DeliveryTag, multiple: false, requeue: false);
        }
    }

    /// <summary>
    /// Resolves <c>IEventHandler&lt;T&gt;</c> for the runtime type and invokes each one. Reflection
    /// is unavoidable here: the type is only known once the message has been read.
    /// </summary>
    private async Task DispatchAsync(Type eventType, object @event)
    {
        using var scope = _scopes.CreateScope();

        var handlerType = typeof(IEventHandler<>).MakeGenericType(eventType);
        var handlers = scope.ServiceProvider.GetServices(handlerType);

        foreach (var handler in handlers)
        {
            if (handler is null)
            {
                continue;
            }

            var method = handlerType.GetMethod(nameof(IEventHandler<BookingCreated>.HandleAsync))!;
            await (Task)method.Invoke(handler, new[] { @event, CancellationToken.None })!;
        }
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
