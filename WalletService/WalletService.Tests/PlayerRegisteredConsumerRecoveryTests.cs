using Contracts;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WalletService.Data;
using WalletService.Domain;
using WalletService.Messaging;

namespace WalletService.Tests;

/// <summary>
/// The endpoint survives a database outage longer than the old 5-retry policy: wallet inserts fail 6 times, the
/// message is retried (short test intervals, production-shaped 10-retry policy) and then succeeds without a Fault,
/// i.e. it never reaches wallet-player-registered_error.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class PlayerRegisteredConsumerRecoveryTests(SqlServerFixture fx) : IAsyncLifetime
{
    private const int Failures = 6;

    private readonly FailWalletInserts _outage = new(Failures);
    private readonly ReceiveProbe _probe = new();
    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _provider = new ServiceCollection()
            .AddLogging()
            .AddDbContext<WalletDbContext>(o => o.UseSqlServer(fx.ConnectionString).AddInterceptors(_outage))
            .Configure<MessagingOptions>(o => o.Retry = new MessagingOptions.RetrySettings
            {
                Limit = 10,
                MinInterval = TimeSpan.FromMilliseconds(10),
                MaxInterval = TimeSpan.FromMilliseconds(50),
                IntervalDelta = TimeSpan.FromMilliseconds(10),
            })
            .AddMassTransitTestHarness(x =>
            {
                x.SetTestTimeouts(testTimeout: TimeSpan.FromSeconds(30));
                WalletMessaging.Configure(x);
            })
            .BuildServiceProvider(true);
        _harness = _provider.GetRequiredService<ITestHarness>();
        await _harness.Start();
        _harness.Bus.ConnectReceiveObserver(_probe);
    }

    public async Task DisposeAsync()
    {
        await _harness.Stop();
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task Consumer_recovers_after_repeated_database_failures_without_faulting()
    {
        Assert.True(Failures < new MessagingOptions().Retry.Limit);
        var playerId = Guid.NewGuid();
        var messageId = NewId.NextGuid();

        await _harness.Bus.Publish(new PlayerRegistered(playerId, DateTimeOffset.UtcNow), ctx => ctx.MessageId = messageId);
        await _probe.WaitForAsync(messageId, timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(Failures + 1, _outage.Attempts);
        await using var db = fx.CreateContext();
        Assert.Equal(3, await db.Wallets.CountAsync(w => w.PlayerId == playerId));
        Assert.False(_harness.Published.Select<Fault<PlayerRegistered>>().Any());
    }

    [Fact]
    public void Endpoint_defaults_limit_concurrency_and_retry_for_long_outages()
    {
        var definition = new PlayerRegisteredConsumerDefinition(Options.Create(new MessagingOptions()));
        var defaults = new MessagingOptions();

        Assert.Equal(PlayerRegisteredConsumerDefinition.QueueName,
            ((IConsumerDefinition)definition).GetEndpointName(DefaultEndpointNameFormatter.Instance));
        Assert.Equal(8, definition.ConcurrentMessageLimit);
        Assert.Equal(10, defaults.Retry.Limit);
        Assert.Equal(TimeSpan.FromMinutes(5), defaults.Retry.MaxInterval);
        Assert.Equal(10, defaults.KillSwitch.ActivationThreshold);
        Assert.Equal(0.15, defaults.KillSwitch.TripThreshold);
    }

    /// <summary>Makes the first N saves that insert wallets throw, like SQL Server being unavailable.</summary>
    private sealed class FailWalletInserts(int failures) : SaveChangesInterceptor
    {
        private int _attempts;

        public int Attempts => _attempts;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Wallet>().Any(e => e.State == EntityState.Added)
                && Interlocked.Increment(ref _attempts) <= failures)
                throw new InvalidOperationException("Simulated database outage.");
            return ValueTask.FromResult(result);
        }
    }
}
