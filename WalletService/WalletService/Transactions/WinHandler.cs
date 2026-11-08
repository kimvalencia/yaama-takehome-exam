using Microsoft.EntityFrameworkCore;
using WalletService.Data;
using WalletService.Domain;

namespace WalletService.Transactions;

/// <summary>
/// WIN credits the wallet. <c>BetReference</c> must name an existing BET of the same provider,
/// player and currency, and a BET can be settled by at most one WIN.
/// The "at most one" rule is also enforced by a filtered unique index, which covers racing WINs.
/// </summary>
public sealed class WinHandler : ITransactionHandler
{
    public const string TypeName = "WIN";

    public string Type => TypeName;
    public Direction Direction => Direction.Credit;

    public async Task<(string? ErrorCode, Guid? RelatedTransactionId)> ValidateAsync(
        TransactionCommand c, WalletDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.BetReference))
            return (ErrorCodes.ValidationFailed, null);

        var betId = await (
                from t in db.Transactions
                join w in db.Wallets on t.WalletId equals w.Id
                where t.ProviderId == c.ProviderId
                      && t.Reference == c.BetReference
                      && t.Type == BetHandler.TypeName
                      && w.PlayerId == c.PlayerId
                      && w.Currency == c.Currency
                select (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);

        if (betId is null)
            return (ErrorCodes.UnknownBet, null);

        var settled = await db.Transactions.AnyAsync(
            t => t.Type == TypeName && t.RelatedTransactionId == betId, ct);

        return settled ? (ErrorCodes.BetAlreadySettled, null) : (null, betId);
    }
}
