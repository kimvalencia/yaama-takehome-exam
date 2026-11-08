using WalletService.Data;

namespace WalletService.Transactions;

public enum Direction
{
    Debit,
    Credit,
}

/// <summary>
/// One handler per transaction type. Adding a type (REFUND, BONUS, ...) means adding a handler;
/// the schema does not change because the type is stored as a string.
/// </summary>
public interface ITransactionHandler
{
    /// <summary>Upper-case type name as sent in the contract, e.g. "BET".</summary>
    string Type { get; }

    Direction Direction { get; }

    /// <summary>
    /// Type-specific rules, run inside the processor's DB transaction.
    /// Returns an error code to reject, and optionally the id of a related transaction to link.
    /// </summary>
    Task<(string? ErrorCode, Guid? RelatedTransactionId)> ValidateAsync(
        TransactionCommand c, WalletDbContext db, CancellationToken ct);
}
