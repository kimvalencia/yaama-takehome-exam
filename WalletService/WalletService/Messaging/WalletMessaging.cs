using MassTransit;
using WalletService.Data;

namespace WalletService.Messaging;

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
/// shorten the retry intervals. (Same shape as in the Users service.)
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
    /// would move to the <c>_error</c> queue (e.g. across a SQL Server restart or failover).
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

/// <summary>MassTransit wiring: the <c>PlayerRegistered</c> consumer with the EF inbox on <see cref="WalletDbContext"/>.</summary>
public static class WalletMessaging
{
    public const string TransportKey = "Messaging:Transport";

    /// <summary>Consumers and the EF inbox/outbox; shared by the app and the tests' MassTransit test harness.</summary>
    public static void Configure(IBusRegistrationConfigurator x)
    {
        // The inbox remembers consumed MessageIds (default window 30 min) and skips redeliveries.
        x.AddEntityFrameworkOutbox<WalletDbContext>(o => o.UseSqlServer());
        x.AddConsumer<PlayerRegisteredConsumer, PlayerRegisteredConsumerDefinition>();
    }

    /// <summary>
    /// <c>Messaging:Transport</c> = <c>RabbitMq</c> (default) or <c>InMemory</c> (default in the <c>Testing</c>
    /// environment, so tests never need a broker). Queues are durable; several instances compete on the same queue.
    /// </summary>
    public static IServiceCollection AddWalletMessaging(this IServiceCollection services, IConfiguration configuration,
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
                    cfg.Host(rabbit.Host, rabbit.Port, rabbit.VirtualHost, h =>
                    {
                        h.Username(rabbit.Username);
                        h.Password(rabbit.Password);
                    });
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
