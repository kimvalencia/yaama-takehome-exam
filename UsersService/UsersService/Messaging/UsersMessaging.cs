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

public static class MessagingRetry
{
    /// <summary>Exponential backoff: about 1 s, 3 s, 7 s, 15 s, 30 s, then the message moves to the <c>_error</c> queue.</summary>
    public static void Configure(IRetryConfigurator r) =>
        r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2));
}

/// <summary>
/// MassTransit wiring: the EF bus outbox on <see cref="UsersDbContext"/> (messages published through the scoped
/// <see cref="IPublishEndpoint"/> are saved with the caller's <c>SaveChanges</c> and delivered afterwards by a
/// background service), and the verification email consumer with the EF inbox.
/// </summary>
public static class UsersMessaging
{
    public const string TransportKey = "Messaging:Transport";

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
    /// <c>Messaging:Transport</c> = <c>RabbitMq</c> (default) or <c>InMemory</c> (default in the <c>Testing</c>
    /// environment, so tests never need a broker). Queues are durable; several instances compete on the same queue.
    /// </summary>
    public static IServiceCollection AddUsersMessaging(this IServiceCollection services, IConfiguration configuration,
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
