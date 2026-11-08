using WalletService.Data;

namespace WalletService.Transactions;

/// <summary>DEPOSIT credits the wallet; backs the simulated player top-up.</summary>
public sealed class DepositHandler : ITransactionHandler
{
    public const string TypeName = "DEPOSIT";

    public string Type => TypeName;
    public Direction Direction => Direction.Credit;

    public Task<(string? ErrorCode, Guid? RelatedTransactionId)> ValidateAsync(
        TransactionCommand c, WalletDbContext db, CancellationToken ct) =>
        Task.FromResult<(string?, Guid?)>((null, null));
}
