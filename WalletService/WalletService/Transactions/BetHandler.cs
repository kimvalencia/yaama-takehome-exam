using WalletService.Data;

namespace WalletService.Transactions;

/// <summary>BET debits the wallet. Funds are checked by the processor's guarded update.</summary>
public sealed class BetHandler : ITransactionHandler
{
    public const string TypeName = "BET";

    public string Type => TypeName;
    public Direction Direction => Direction.Debit;

    public Task<(string? ErrorCode, Guid? RelatedTransactionId)> ValidateAsync(
        TransactionCommand c, WalletDbContext db, CancellationToken ct) =>
        Task.FromResult<(string?, Guid?)>((null, null));
}
