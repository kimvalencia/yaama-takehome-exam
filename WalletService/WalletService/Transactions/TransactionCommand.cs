using WalletService.Domain;

namespace WalletService.Transactions;

public record TransactionCommand(
    string ProviderId,
    string Reference,
    string Type,
    Guid PlayerId,
    Currency Currency,
    decimal Amount,
    string? BetReference);

public enum ProcessStatus
{
    Created,
    Replayed,
    Rejected,
}

public record ProcessResult(ProcessStatus Status, WalletTransaction? Transaction, string? ErrorCode)
{
    public static ProcessResult Created(WalletTransaction t) => new(ProcessStatus.Created, t, null);
    public static ProcessResult Replayed(WalletTransaction t) => new(ProcessStatus.Replayed, t, null);
    public static ProcessResult Rejected(string errorCode) => new(ProcessStatus.Rejected, null, errorCode);
}
