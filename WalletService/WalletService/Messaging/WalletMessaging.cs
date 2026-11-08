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

public static class MessagingRetry
{
    /// <summary>Exponential backoff: about 1 s, 3 s, 7 s, 15 s, 30 s, then the message moves to the <c>_error</c> queue.</summary>
    public static void Configure(IRetryConfigurator r) =>
        r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2));
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
