using Contracts;
using MassTransit;
using UsersService.Data;

namespace UsersService.Messaging;

public sealed class RabbitMqOptions
{
    public const string Section = "RabbitMq";

    public string Host { get; set; } = "rabbitmq";
    public ushort Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "/";
    public string Username { get; set; } = "guest";
    public string Password { get; set; } = "guest";
}

/// <summary>
/// Consumer resilience, bound from the <c>Messaging</c> section. The defaults are the production values; tests
/// shorten the retry intervals.
/// </summary>
public sealed class MessagingOptions
{
    public const string Section = "Messaging";

    /// <summary>Messages processed in parallel per endpoint (per instance).</summary>
    public int ConcurrentMessageLimit { get; set; } = 8;

    public RetrySettings Retry { get; set; } = new();
    public KillSwitchSettings KillSwitch { get; set; } = new();

    /// <summary>
    /// Exponential in-memory retry. Defaults: 10 retries, about 1 s, 6 s, 16 s, 36 s, 76 s, 156 s, then 5 min each
    /// (MassTransit adds +-20% jitter to the delta), so one message keeps retrying for roughly 25 minutes before it
    /// would move to the <c>_error</c> queue.
    /// </summary>
    public sealed class RetrySettings
    {
        public int Limit { get; set; } = 10;
        public TimeSpan MinInterval { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan MaxInterval { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan IntervalDelta { get; set; } = TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Kill switch: once at least <see cref="ActivationThreshold"/> messages were seen in <see cref="TrackingPeriod"/>
    /// and <see cref="TripThreshold"/> of them faulted, the endpoint stops for <see cref="RestartTimeout"/>; unconsumed
    /// messages stay in the queue (in-flight ones are returned) instead of draining into <c>_error</c>.
    /// </summary>
    public sealed class KillSwitchSettings
    {
        public int ActivationThreshold { get; set; } = 10;
        public double TripThreshold { get; set; } = 0.15;
        public TimeSpan TrackingPeriod { get; set; } = TimeSpan.FromMinutes(1);
        public TimeSpan RestartTimeout { get; set; } = TimeSpan.FromMinutes(1);
    }
}

public static class MessagingResilience
{
    /// <summary>Kill switch, then retry; call before the EF inbox/outbox so each retry gets a fresh DB transaction.</summary>
    public static void Apply(IReceiveEndpointConfigurator endpoint, MessagingOptions options)
    {
        var k = options.KillSwitch;
        endpoint.UseKillSwitch(o => o
            .SetActivationThreshold(k.ActivationThreshold)
            .SetTripThreshold(k.TripThreshold)
            .SetTrackingPeriod(k.TrackingPeriod)
            .SetRestartTimeout(k.RestartTimeout));
        var r = options.Retry;
        endpoint.UseMessageRetry(c => c.Exponential(r.Limit, r.MinInterval, r.MaxInterval, r.IntervalDelta));
    }
}

/// <summary>
/// MassTransit wiring: the EF bus outbox on <see cref="UsersDbContext"/> (messages published through the scoped
/// <see cref="IPublishEndpoint"/> are saved with the caller's <c>SaveChanges</c> and delivered afterwards by a
/// background service), and the verification email consumer with the EF inbox.
/// </summary>
public static class UsersMessaging
{
    public const string TransportKey = "Messaging:Transport";

    /// <summary>
    /// The Wallet service's <c>PlayerRegistered</c> queue. Must equal <c>PlayerRegisteredConsumerDefinition.QueueName</c>
    /// in the Wallet service (the services do not share code).
    /// </summary>
    public const string WalletPlayerRegisteredQueue = "wallet-player-registered";

    /// <summary>Outbox and consumers; shared by the app and the tests' MassTransit test harness.</summary>
    /// <param name="outboxDelivery">
    /// False only in tests that inspect the outbox tables: rows are written but never delivered (or deleted).
    /// </param>
    public static void Configure(IBusRegistrationConfigurator x, bool outboxDelivery = true)
    {
        x.AddEntityFrameworkOutbox<UsersDbContext>(o =>
        {
            o.UseSqlServer();
            // Delivery is triggered right after each commit; this is only the idle polling interval (multiple
            // instances share the table safely: each outbox row set is locked by one delivery service at a time).
            o.QueryDelay = TimeSpan.FromSeconds(1);
            o.UseBusOutbox(b =>
            {
                if (!outboxDelivery) b.DisableDeliveryService();
            });
        });
        x.AddConsumer<SendVerificationEmailConsumer, SendVerificationEmailConsumerDefinition>();
    }

    /// <summary>
    /// RabbitMQ host plus the publish topology this service relies on. RabbitMQ silently drops a message published to
    /// an exchange with no bound queue (the outbox would then mark it delivered), so Users itself declares the durable
    /// consumer queues for what it publishes and binds them to the message exchanges, at bus start. The queues are
    /// therefore there whenever Users can publish, whatever the start order of the services or after a broker reset.
    /// The declarations match what the consumers' receive endpoints declare (durable, not auto-delete, no arguments).
    /// </summary>
    public static void ConfigureRabbitMq(IRabbitMqBusFactoryConfigurator cfg, RabbitMqOptions rabbit)
    {
        cfg.Host(rabbit.Host, rabbit.Port, rabbit.VirtualHost, h =>
        {
            h.Username(rabbit.Username);
            h.Password(rabbit.Password);
        });
        cfg.DeployPublishTopology = true;
        cfg.Publish<PlayerRegistered>(p => p.BindQueue(p.Exchange.ExchangeName, WalletPlayerRegisteredQueue));
        cfg.Publish<VerificationEmailRequested>(p =>
            p.BindQueue(p.Exchange.ExchangeName, SendVerificationEmailConsumerDefinition.QueueName));
    }

    /// <summary>
    /// <c>Messaging:Transport</c> = <c>RabbitMq</c> (default) or <c>InMemory</c> (default in the <c>Testing</c>
    /// environment, so tests never need a broker). Queues are durable; several instances compete on the same queue.
    /// </summary>
    public static IServiceCollection AddUsersMessaging(this IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        var transport = configuration[TransportKey] ?? (environment.IsEnvironment("Testing") ? "InMemory" : "RabbitMq");
        var rabbit = configuration.GetSection(RabbitMqOptions.Section).Get<RabbitMqOptions>() ?? new RabbitMqOptions();
        services.Configure<MessagingOptions>(configuration.GetSection(MessagingOptions.Section));

        services.AddMassTransit(x =>
        {
            Configure(x);
            if (string.Equals(transport, "InMemory", StringComparison.OrdinalIgnoreCase))
            {
                x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
            }
            else if (string.Equals(transport, "RabbitMq", StringComparison.OrdinalIgnoreCase))
            {
                x.UsingRabbitMq((ctx, cfg) =>
                {
                    ConfigureRabbitMq(cfg, rabbit);
                    cfg.ConfigureEndpoints(ctx);
                });
            }
            else
            {
                throw new InvalidOperationException($"{TransportKey} must be RabbitMq or InMemory, not '{transport}'.");
            }
        });
        return services;
    }
}
