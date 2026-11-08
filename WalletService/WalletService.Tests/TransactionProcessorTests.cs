using Microsoft.EntityFrameworkCore;
using WalletService.Data;
using WalletService.Domain;
using WalletService.Transactions;

namespace WalletService.Tests;

[Collection(SqlServerCollection.Name)]
public class TransactionProcessorTests(SqlServerFixture fx)
{
    // Unique per test instance so tests sharing the database never collide on (ProviderId, Reference).
    private readonly string _provider = "provider-" + Guid.NewGuid().ToString("N");

    private static TransactionProcessor NewProcessor(WalletDbContext db) =>
        new(db, [new BetHandler(), new WinHandler(), new DepositHandler()]);

    private async Task<ProcessResult> Process(TransactionCommand command)
    {
        await using var db = fx.CreateContext();
        return await NewProcessor(db).ProcessAsync(command, CancellationToken.None);
    }

    private async Task<Guid> SeedWallet(decimal balance, Currency currency = Currency.USD, Guid? playerId = null)
    {
        var player = playerId ?? Guid.NewGuid();
        await using var db = fx.CreateContext();
        db.Wallets.Add(new Wallet { Id = Guid.NewGuid(), PlayerId = player, Currency = currency, Balance = balance });
        await db.SaveChangesAsync();
        return player;
    }

    private async Task<decimal> BalanceOf(Guid playerId, Currency currency = Currency.USD)
    {
        await using var db = fx.CreateContext();
        return await db.Wallets.Where(w => w.PlayerId == playerId && w.Currency == currency)
            .Select(w => w.Balance).SingleAsync();
    }

    private async Task<int> TransactionCountOf(Guid playerId, Currency currency = Currency.USD)
    {
        await using var db = fx.CreateContext();
        var walletId = await db.Wallets.Where(w => w.PlayerId == playerId && w.Currency == currency)
            .Select(w => w.Id).SingleAsync();
        return await db.Transactions.CountAsync(t => t.WalletId == walletId);
    }

    private TransactionCommand Bet(Guid player, decimal amount, string? reference = null, Currency currency = Currency.USD) =>
        new(_provider, reference ?? Guid.NewGuid().ToString("N"), "BET", player, currency, amount, null);

    private TransactionCommand Win(Guid player, decimal amount, string? betReference, string? reference = null, Currency currency = Currency.USD) =>
        new(_provider, reference ?? Guid.NewGuid().ToString("N"), "WIN", player, currency, amount, betReference);

    [Fact]
    public async Task Bet_debits_balance_and_records_balance_after()
    {
        var player = await SeedWallet(100m);

        var result = await Process(Bet(player, 12.50m));

        Assert.Equal(ProcessStatus.Created, result.Status);
        Assert.Null(result.ErrorCode);
        Assert.NotNull(result.Transaction);
        Assert.Equal(12.50m, result.Transaction!.Amount);
        Assert.Equal(87.50m, result.Transaction.BalanceAfter);
        Assert.Equal("BET", result.Transaction.Type);
        Assert.Equal(87.50m, await BalanceOf(player));
    }

    [Fact]
    public async Task Bet_over_balance_is_rejected_with_INSUFFICIENT_FUNDS_and_balance_unchanged()
    {
        var player = await SeedWallet(10m);

        var result = await Process(Bet(player, 10.01m));

        Assert.Equal(ProcessStatus.Rejected, result.Status);
        Assert.Equal(ErrorCodes.InsufficientFunds, result.ErrorCode);
        Assert.Equal("INSUFFICIENT_FUNDS", result.ErrorCode);
        Assert.Equal(10m, await BalanceOf(player));
        Assert.Equal(0, await TransactionCountOf(player));
    }

    [Fact]
    public async Task Same_reference_same_payload_replays_without_second_debit()
    {
        var player = await SeedWallet(100m);
        var command = Bet(player, 10m, "ref-1");

        var first = await Process(command);
        var second = await Process(command);

        Assert.Equal(ProcessStatus.Created, first.Status);
        Assert.Equal(ProcessStatus.Replayed, second.Status);
        Assert.Equal(first.Transaction!.Id, second.Transaction!.Id);
        Assert.Equal(90m, second.Transaction.BalanceAfter);
        Assert.Equal(90m, await BalanceOf(player));
        Assert.Equal(1, await TransactionCountOf(player));
    }

    [Fact]
    public async Task Same_reference_different_amount_is_rejected_DUPLICATE_REFERENCE_MISMATCH()
    {
        var player = await SeedWallet(100m);

        var first = await Process(Bet(player, 10m, "ref-1"));
        var second = await Process(Bet(player, 20m, "ref-1"));

        Assert.Equal(ProcessStatus.Created, first.Status);
        Assert.Equal(ProcessStatus.Rejected, second.Status);
        Assert.Equal("DUPLICATE_REFERENCE_MISMATCH", second.ErrorCode);
        Assert.Equal(90m, await BalanceOf(player));
        Assert.Equal(1, await TransactionCountOf(player));
    }

    [Fact]
    public async Task Win_requires_existing_bet_of_same_player_and_currency()
    {
        var player = await SeedWallet(100m);
        await SeedWallet(100m, Currency.EUR, player);
        var otherPlayer = await SeedWallet(100m);
        var bet = await Process(Bet(player, 10m, "bet-1"));
        Assert.Equal(ProcessStatus.Created, bet.Status);

        var missingBet = await Process(Win(player, 5m, "no-such-bet"));
        var otherPlayersWin = await Process(Win(otherPlayer, 5m, "bet-1"));
        var otherCurrencyWin = await Process(Win(player, 5m, "bet-1", currency: Currency.EUR));
        var noBetReference = await Process(Win(player, 5m, null));

        Assert.Equal("UNKNOWN_BET", missingBet.ErrorCode);
        Assert.Equal("UNKNOWN_BET", otherPlayersWin.ErrorCode);
        Assert.Equal("UNKNOWN_BET", otherCurrencyWin.ErrorCode);
        Assert.Equal(ProcessStatus.Rejected, noBetReference.Status);
        Assert.Equal(100m, await BalanceOf(otherPlayer));
        Assert.Equal(100m, await BalanceOf(player, Currency.EUR));

        var win = await Process(Win(player, 25m, "bet-1"));

        Assert.Equal(ProcessStatus.Created, win.Status);
        Assert.Equal(bet.Transaction!.Id, win.Transaction!.RelatedTransactionId);
        Assert.Equal(115m, win.Transaction.BalanceAfter);
        Assert.Equal(115m, await BalanceOf(player));
    }

    [Fact]
    public async Task Second_win_for_same_bet_is_rejected_BET_ALREADY_SETTLED()
    {
        var player = await SeedWallet(100m);
        await Process(Bet(player, 10m, "bet-1"));

        var firstWin = await Process(Win(player, 30m, "bet-1", "win-1"));
        var secondWin = await Process(Win(player, 30m, "bet-1", "win-2"));

        Assert.Equal(ProcessStatus.Created, firstWin.Status);
        Assert.Equal(ProcessStatus.Rejected, secondWin.Status);
        Assert.Equal("BET_ALREADY_SETTLED", secondWin.ErrorCode);
        Assert.Equal(120m, await BalanceOf(player));
    }

    [Fact]
    public async Task Unknown_wallet_is_rejected_UNKNOWN_PLAYER()
    {
        var player = await SeedWallet(100m, Currency.USD);

        var unknownPlayer = await Process(Bet(Guid.NewGuid(), 10m));
        var missingCurrencyWallet = await Process(Bet(player, 10m, currency: Currency.BTC));

        Assert.Equal(ProcessStatus.Rejected, unknownPlayer.Status);
        Assert.Equal("UNKNOWN_PLAYER", unknownPlayer.ErrorCode);
        Assert.Equal("UNKNOWN_PLAYER", missingCurrencyWallet.ErrorCode);
    }

    [Fact]
    public async Task Deposit_credits_balance()
    {
        var player = await SeedWallet(0m, Currency.BTC);

        var result = await Process(new TransactionCommand(
            $"topup:{player:D}", Guid.NewGuid().ToString("N"), "DEPOSIT", player, Currency.BTC, 0.12345678m, null));

        Assert.Equal(ProcessStatus.Created, result.Status);
        Assert.Equal(0.12345678m, result.Transaction!.BalanceAfter);
        Assert.Equal(0.12345678m, await BalanceOf(player, Currency.BTC));
    }

    [Theory]
    [InlineData("0", "USD")]
    [InlineData("-5", "USD")]
    [InlineData("1.005", "USD")]
    [InlineData("0.123456789", "BTC")]
    public async Task Invalid_amount_is_rejected_VALIDATION_FAILED(string amount, string currency)
    {
        var c = Enum.Parse<Currency>(currency);
        var player = await SeedWallet(100m, c);

        var result = await Process(Bet(player, decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), currency: c));

        Assert.Equal(ProcessStatus.Rejected, result.Status);
        Assert.Equal(ErrorCodes.ValidationFailed, result.ErrorCode);
        Assert.Equal(100m, await BalanceOf(player, c));
    }

    [Fact]
    public async Task Unknown_type_is_rejected_VALIDATION_FAILED()
    {
        var player = await SeedWallet(100m);

        var result = await Process(new TransactionCommand(_provider, "r", "JACKPOT", player, Currency.USD, 1m, null));

        Assert.Equal(ProcessStatus.Rejected, result.Status);
        Assert.Equal("VALIDATION_FAILED", result.ErrorCode);
    }

    [Fact]
    public async Task Fifty_parallel_bets_of_10_on_balance_100_leave_exactly_ten_successes_and_balance_zero()
    {
        var player = await SeedWallet(100m);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
        {
            await using var db = fx.CreateContext();
            var processor = NewProcessor(db);
            await start.Task;
            return await processor.ProcessAsync(Bet(player, 10m, $"bet-{i}"), CancellationToken.None);
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(10, results.Count(r => r.Status == ProcessStatus.Created));
        Assert.Equal(40, results.Count(r => r.Status == ProcessStatus.Rejected && r.ErrorCode == ErrorCodes.InsufficientFunds));
        Assert.Equal(0m, await BalanceOf(player));
        Assert.Equal(10, await TransactionCountOf(player));
        Assert.Equal(
            Enumerable.Range(0, 10).Select(n => 90m - 10m * n).OrderBy(x => x),
            results.Where(r => r.Status == ProcessStatus.Created).Select(r => r.Transaction!.BalanceAfter).OrderBy(x => x));
    }

    [Fact]
    public async Task Twenty_parallel_identical_requests_create_one_transaction_and_one_debit()
    {
        var player = await SeedWallet(100m);
        var command = Bet(player, 10m, "same-ref");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            await using var db = fx.CreateContext();
            var processor = NewProcessor(db);
            await start.Task;
            return await processor.ProcessAsync(command, CancellationToken.None);
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.Status == ProcessStatus.Created));
        Assert.Equal(19, results.Count(r => r.Status == ProcessStatus.Replayed));
        Assert.Single(results.Select(r => r.Transaction!.Id).Distinct());
        Assert.All(results, r => Assert.Equal(90m, r.Transaction!.BalanceAfter));
        Assert.Equal(90m, await BalanceOf(player));
        Assert.Equal(1, await TransactionCountOf(player));
    }
}
