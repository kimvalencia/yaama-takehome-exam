using Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.RabbitMq;
using UsersService.Data;
using UsersService.Email;
using UsersService.Messaging;

namespace UsersService.Tests;

public static class TestMessaging
{
    /// <summary>Same policy shape as production (10 retries), with millisecond intervals so tests stay fast.</summary>
    public static void FastRetry(MessagingOptions o) => o.Retry = new MessagingOptions.RetrySettings
    {
        Limit = 10,
        MinInterval = TimeSpan.FromMilliseconds(10),
        MaxInterval = TimeSpan.FromMilliseconds(50),
        IntervalDelta = TimeSpan.FromMilliseconds(10),
    };
}

/// <summary>
/// Against a real, empty RabbitMQ broker: the Users bus alone (no Wallet service ever started) declares the durable
/// <c>wallet-player-registered</c> queue and binds it to the <c>PlayerRegistered</c> exchange, so a registration
/// published before Wallet first starts is queued, not dropped. A consumer that later declares the queue the way
/// Wallet's receive endpoint does (defaults: durable, no arguments) then receives it.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class RabbitMqTopologyTests(SqlServerFixture fx) : IAsyncLifetime
{
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:3-management").Build();
    private RabbitMqOptions _options = null!;

    public async Task InitializeAsync()
    {
        await _rabbit.StartAsync();
        var uri = new Uri(_rabbit.GetConnectionString());
        var credentials = Uri.UnescapeDataString(uri.UserInfo).Split(':', 2);
        _options = new RabbitMqOptions
        {
            Host = uri.Host, Port = (ushort)uri.Port, VirtualHost = "/", Username = credentials[0], Password = credentials[1],
        };
    }

    public async Task DisposeAsync() => await _rabbit.DisposeAsync();

    [Fact]
    public async Task PlayerRegistered_published_before_wallet_ever_started_is_kept_in_the_wallet_queue()
    {
        var playerId = Guid.NewGuid();

        // 1. Users only: its bus deploys the publish topology at start, then publishes and stops.
        // (The provider stays alive to the end: MassTransit's static log context still points at its LoggerFactory.)
        await using var users = new ServiceCollection()
            .AddLogging()
            .AddDbContext<UsersDbContext>(o => o.UseSqlServer(fx.NoDeliveryConnectionString))
            .AddSingleton<IEmailSender>(new FakeEmailSender())
            .AddMassTransit(x =>
            {
                UsersMessaging.Configure(x, outboxDelivery: false);
                x.UsingRabbitMq((ctx, cfg) =>
                {
                    UsersMessaging.ConfigureRabbitMq(cfg, _options);
                    cfg.ConfigureEndpoints(ctx);
                });
            })
            .BuildServiceProvider(true);
        var usersBus = users.GetRequiredService<IBusControl>();
        await usersBus.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        try
        {
            await usersBus.Publish(new PlayerRegistered(playerId, DateTimeOffset.UtcNow));
        }
        finally
        {
            await usersBus.StopAsync();
        }

        // 2. Later, a consumer on wallet-player-registered with default endpoint settings (as Wallet declares it).
        var received = new TaskCompletionSource<PlayerRegistered>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var wallet = new ServiceCollection()
            .AddLogging()
            .AddMassTransit(x => x.UsingRabbitMq((ctx, cfg) =>
            {
                cfg.Host(_options.Host, _options.Port, _options.VirtualHost, h =>
                {
                    h.Username(_options.Username);
                    h.Password(_options.Password);
                });
                cfg.ReceiveEndpoint(UsersMessaging.WalletPlayerRegisteredQueue, e => e.Handler<PlayerRegistered>(c =>
                {
                    if (c.Message.PlayerId == playerId) received.TrySetResult(c.Message);
                    return Task.CompletedTask;
                }));
            }))
            .BuildServiceProvider(true);
        var walletBus = wallet.GetRequiredService<IBusControl>();
        // Throws (PRECONDITION_FAILED) if the queue Users declared does not match what the endpoint declares.
        await walletBus.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        try
        {
            var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(playerId, message.PlayerId);
        }
        finally
        {
            await walletBus.StopAsync();
        }
    }
}
