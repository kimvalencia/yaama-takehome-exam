using Contracts;
using MassTransit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using WalletService.Data;
using WalletService.Domain;

namespace WalletService.Messaging;

/// <summary>
/// Creates the player's wallets (one per supported currency, balance 0) when the Users service reports a registration.
/// Idempotent on three levels: the EF inbox skips a redelivered message (same MessageId); wallets that already exist
/// are skipped; and the unique <c>(PlayerId, Currency)</c> index turns a race with another instance into a no-op.
/// </summary>
public sealed class PlayerRegisteredConsumer(WalletDbContext db, ILogger<PlayerRegisteredConsumer> logger)
    : IConsumer<PlayerRegistered>
{
    private const int MaxRaceAttempts = 3;

    public static IReadOnlyList<Currency> Currencies { get; } = Enum.GetValues<Currency>();

    public Task Consume(ConsumeContext<PlayerRegistered> context) =>
        CreateWalletsAsync(context.Message.PlayerId, context.CancellationToken);

    public async Task CreateWalletsAsync(Guid playerId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var existing = await db.Wallets.Where(w => w.PlayerId == playerId).Select(w => w.Currency).ToListAsync(ct);
            var added = Currencies.Except(existing)
                .Select(c => new Wallet { Id = Guid.NewGuid(), PlayerId = playerId, Currency = c, Balance = 0m })
                .ToList();
            if (added.Count == 0)
            {
                logger.LogInformation("Wallets for player {PlayerId} already exist.", playerId);
                return;
            }

            db.Wallets.AddRange(added);
            try
            {
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Created {Currencies} wallets for player {PlayerId}.",
                    string.Join(",", added.Select(w => w.Currency)), playerId);
                return;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex) && attempt < MaxRaceAttempts)
            {
                // Another instance (or a concurrent redelivery) inserted one of them first. EF rolled back to its
                // savepoint, so forget our inserts and re-read what exists now.
                foreach (var wallet in added) db.Entry(wallet).State = EntityState.Detached;
                logger.LogInformation("Concurrent wallet creation for player {PlayerId}; re-checking.", playerId);
            }
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };
}

public sealed class PlayerRegisteredConsumerDefinition : ConsumerDefinition<PlayerRegisteredConsumer>
{
    public const string QueueName = "wallet-player-registered";

    public PlayerRegisteredConsumerDefinition() => EndpointName = QueueName;

    protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<PlayerRegisteredConsumer> consumerConfigurator, IRegistrationContext context)
    {
        // Retry outside the inbox/outbox so each attempt runs in a fresh DB transaction.
        endpointConfigurator.UseMessageRetry(r => MessagingRetry.Configure(r));
        endpointConfigurator.UseEntityFrameworkOutbox<WalletDbContext>(context);
    }
}
