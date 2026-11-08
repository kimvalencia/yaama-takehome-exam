using Contracts;
using MassTransit;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WalletService.Data;
using WalletService.Domain;
using WalletService.Messaging;

namespace WalletService.Tests;

/// <summary>
/// The real consumer wiring (<see cref="WalletMessaging.Configure"/>: consumer definition, retry, EF inbox on
/// WalletDbContext) on the MassTransit in-memory test harness, against the real SQL Server container.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class PlayerRegisteredConsumerTests(SqlServerFixture fx) : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _provider = new ServiceCollection()
            .AddLogging()
            .AddDbContext<WalletDbContext>(o => o.UseSqlServer(fx.ConnectionString))
            .AddMassTransitTestHarness(x =>
            {
                x.SetTestTimeouts(testTimeout: Timeout);
                WalletMessaging.Configure(x);
            })
            .BuildServiceProvider(true);
        _harness = _provider.GetRequiredService<ITestHarness>();
        await _harness.Start();
        _harness.Bus.ConnectReceiveObserver(_probe);
    }

    private readonly ReceiveProbe _probe = new();

    public async Task DisposeAsync()
    {
        await _harness.Stop();
        await _provider.DisposeAsync();
    }

    private async Task<List<Wallet>> WalletsOf(Guid playerId)
    {
        await using var db = fx.CreateContext();
        var wallets = await db.Wallets.AsNoTracking().Where(w => w.PlayerId == playerId).ToListAsync();
        return wallets.OrderBy(w => w.Currency).ToList(); // enum order (USD, EUR, BTC); SQL would sort the strings
    }

    /// <summary>Publishes and waits until the endpoint has finished with that delivery (consumed or skipped).</summary>
    private async Task PublishAndWaitReceived(PlayerRegistered message, Guid messageId, int deliveryNumber = 1)
    {
        await _harness.Bus.Publish(message, ctx => ctx.MessageId = messageId);
        await _probe.WaitForAsync(messageId, deliveryNumber, Timeout);
    }

    private int ConsumerInvocations(Guid playerId) =>
        _harness.GetConsumerHarness<PlayerRegisteredConsumer>().Consumed
            .Select<PlayerRegistered>(m => m.Context.Message.PlayerId == playerId).Count();

    private static void AssertExactlyThreeZeroWallets(IReadOnlyList<Wallet> wallets)
    {
        Assert.Equal([Currency.USD, Currency.EUR, Currency.BTC], wallets.Select(w => w.Currency).ToArray());
        Assert.All(wallets, w => Assert.Equal(0m, w.Balance));
    }

    [Fact]
    public async Task Consuming_PlayerRegistered_creates_exactly_three_zero_balance_wallets()
    {
        var playerId = Guid.NewGuid();

        await PublishAndWaitReceived(new PlayerRegistered(playerId, DateTimeOffset.UtcNow), NewId.NextGuid());

        AssertExactlyThreeZeroWallets(await WalletsOf(playerId));
        Assert.Equal(1, ConsumerInvocations(playerId));
    }

    [Fact]
    public async Task Redelivered_PlayerRegistered_with_same_message_id_still_leaves_exactly_three_wallets()
    {
        var playerId = Guid.NewGuid();
        var messageId = NewId.NextGuid();
        var message = new PlayerRegistered(playerId, DateTimeOffset.UtcNow);
        await PublishAndWaitReceived(message, messageId);

        // A broker redelivery carries the same MessageId: the inbox recognises it and skips the consumer.
        await PublishAndWaitReceived(message, messageId, deliveryNumber: 2);

        AssertExactlyThreeZeroWallets(await WalletsOf(playerId));
        Assert.Equal(1, ConsumerInvocations(playerId));
    }

    [Fact]
    public async Task Republished_PlayerRegistered_with_new_message_id_still_leaves_exactly_three_wallets()
    {
        var playerId = Guid.NewGuid();
        var message = new PlayerRegistered(playerId, DateTimeOffset.UtcNow);

        // Different MessageIds bypass the inbox (e.g. the event published twice); the consumer itself must be idempotent.
        await PublishAndWaitReceived(message, NewId.NextGuid());
        await PublishAndWaitReceived(message, NewId.NextGuid());

        AssertExactlyThreeZeroWallets(await WalletsOf(playerId));
        Assert.Equal(2, ConsumerInvocations(playerId));
    }

    [Fact]
    public async Task PlayerRegistered_when_one_wallet_exists_creates_only_the_missing_ones()
    {
        var playerId = Guid.NewGuid();
        var existingId = Guid.NewGuid();
        await using (var db = fx.CreateContext())
        {
            db.Wallets.Add(new Wallet { Id = existingId, PlayerId = playerId, Currency = Currency.EUR, Balance = 5m });
            await db.SaveChangesAsync();
        }

        await PublishAndWaitReceived(new PlayerRegistered(playerId, DateTimeOffset.UtcNow), NewId.NextGuid());

        var wallets = await WalletsOf(playerId);
        Assert.Equal([Currency.USD, Currency.EUR, Currency.BTC], wallets.Select(w => w.Currency).ToArray());
        var eur = Assert.Single(wallets, w => w.Currency == Currency.EUR);
        Assert.Equal(existingId, eur.Id); // untouched, balance kept
        Assert.Equal(5m, eur.Balance);
        Assert.All(wallets.Where(w => w.Currency != Currency.EUR), w => Assert.Equal(0m, w.Balance));
    }

    [Fact]
    public async Task Concurrent_creator_winning_the_unique_index_race_does_not_fail_the_consumer()
    {
        var playerId = Guid.NewGuid();
        // Between the consumer's "which wallets exist?" read and its insert, another instance commits the USD wallet.
        var interceptor = new InsertBeforeFirstSave(fx.ConnectionString, playerId, Currency.USD);
        await using var db = new WalletDbContext(new DbContextOptionsBuilder<WalletDbContext>()
            .UseSqlServer(fx.ConnectionString).AddInterceptors(interceptor).Options);
        var consumer = new PlayerRegisteredConsumer(db, NullLogger<PlayerRegisteredConsumer>.Instance);

        await consumer.CreateWalletsAsync(playerId, CancellationToken.None);

        Assert.True(interceptor.Fired);
        AssertExactlyThreeZeroWallets(await WalletsOf(playerId));
    }

    /// <summary>Simulates a racing consumer: inserts a wallet on a separate connection just before the first SaveChanges.</summary>
    private sealed class InsertBeforeFirstSave(string connectionString, Guid playerId, Currency currency) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired)
            {
                Fired = true;
                await using var other = new WalletDbContext(new DbContextOptionsBuilder<WalletDbContext>()
                    .UseSqlServer(connectionString).Options);
                other.Wallets.Add(new Wallet { Id = Guid.NewGuid(), PlayerId = playerId, Currency = currency, Balance = 0m });
                await other.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }
}
