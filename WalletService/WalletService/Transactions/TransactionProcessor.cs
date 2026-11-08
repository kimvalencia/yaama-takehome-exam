using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using WalletService.Data;
using WalletService.Domain;

namespace WalletService.Transactions;

/// <summary>
/// Applies a provider/player transaction to a wallet exactly once.
/// Correctness comes from the database, not from in-process locks:
/// <list type="bullet">
/// <item>the balance changes through one guarded UPDATE (<c>Balance &gt;= @amount</c> for debits), which SQL Server
/// applies atomically and serialises per wallet row, so concurrent requests on any instance cannot overdraw;</item>
/// <item>the unique index on (ProviderId, Reference) makes retries idempotent: a racing duplicate fails the insert,
/// its whole DB transaction (including the debit) rolls back, and it re-reads and replays the winner's row.</item>
/// </list>
/// </summary>
public sealed class TransactionProcessor(WalletDbContext db, IEnumerable<ITransactionHandler> handlers)
{
    // One retry is enough for a lost idempotency race (the second pass sees the committed row);
    // the extra attempt covers a lost race on a secondary unique index such as one-WIN-per-BET.
    private const int MaxAttempts = 3;

    private readonly Dictionary<string, ITransactionHandler> _handlers =
        handlers.ToDictionary(h => h.Type, StringComparer.OrdinalIgnoreCase);

    public async Task<ProcessResult> ProcessAsync(TransactionCommand c, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(c.Type ?? string.Empty, out var handler)
            || string.IsNullOrWhiteSpace(c.ProviderId)
            || string.IsNullOrWhiteSpace(c.Reference)
            || !Enum.IsDefined(c.Currency)
            || !CurrencyRules.IsValidAmount(c.Currency, c.Amount))
        {
            return ProcessResult.Rejected(ErrorCodes.ValidationFailed);
        }

        for (var attempt = 1; ; attempt++)
        {
            var existing = await FindExistingAsync(c, handler, ct);
            if (existing is not null) return existing;

            try
            {
                return await ApplyAsync(c, handler, ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex) && attempt < MaxAttempts)
            {
                // Someone else committed the same reference (or settled the same bet) first.
                // Our DB transaction has been rolled back; start over from the lookup.
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Replay path: an existing row for (ProviderId, Reference) is either the same request or a conflict.</summary>
    private async Task<ProcessResult?> FindExistingAsync(TransactionCommand c, ITransactionHandler handler, CancellationToken ct)
    {
        var found = await (
                from t in db.Transactions.AsNoTracking()
                join w in db.Wallets on t.WalletId equals w.Id
                where t.ProviderId == c.ProviderId && t.Reference == c.Reference
                select new
                {
                    Transaction = t,
                    w.PlayerId,
                    w.Currency,
                    BetReference = db.Transactions
                        .Where(r => r.Id == t.RelatedTransactionId)
                        .Select(r => r.Reference)
                        .FirstOrDefault(),
                })
            .SingleOrDefaultAsync(ct);

        if (found is null) return null;

        var existing = found.Transaction;
        // BetReference only matters for types that link to a related transaction (WIN).
        var betReferenceMatches = existing.RelatedTransactionId is null || found.BetReference == c.BetReference;
        var samePayload = existing.Type == handler.Type
                          && found.PlayerId == c.PlayerId
                          && found.Currency == c.Currency
                          && existing.Amount == c.Amount
                          && betReferenceMatches;

        return samePayload
            ? ProcessResult.Replayed(existing)
            : ProcessResult.Rejected(ErrorCodes.DuplicateReferenceMismatch);
    }

    private async Task<ProcessResult> ApplyAsync(TransactionCommand c, ITransactionHandler handler, CancellationToken ct)
    {
        var walletId = await db.Wallets
            .Where(w => w.PlayerId == c.PlayerId && w.Currency == c.Currency)
            .Select(w => (Guid?)w.Id)
            .SingleOrDefaultAsync(ct);
        if (walletId is null) return ProcessResult.Rejected(ErrorCodes.UnknownPlayer);

        // Default isolation (READ COMMITTED) is sufficient: the guarded UPDATE takes an exclusive row lock that
        // is held to commit, so the balance check and the change are one atomic step and same-wallet writers queue.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var (error, relatedId) = await handler.ValidateAsync(c, db, ct);
        if (error is not null)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            // A racing identical request may have committed while we were inside the transaction.
            // Re-check before returning the handler's rejection so the caller gets a replay, not a spurious error.
            var afterValidationReject = await FindExistingAsync(c, handler, ct);
            if (afterValidationReject is not null) return afterValidationReject;
            return ProcessResult.Rejected(error);
        }

        var amount = c.Amount;
        var id = walletId.Value;
        var updated = handler.Direction == Direction.Debit
            ? await db.Wallets
                .Where(w => w.Id == id && w.Balance >= amount)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.Balance, w => w.Balance - amount), ct)
            : await db.Wallets
                .Where(w => w.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.Balance, w => w.Balance + amount), ct);

        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            // A racing identical request may have been the one that consumed the last balance unit.
            // Re-check: if it committed our reference, replay rather than return INSUFFICIENT_FUNDS.
            var afterUpdateReject = await FindExistingAsync(c, handler, ct);
            if (afterUpdateReject is not null) return afterUpdateReject;
            return ProcessResult.Rejected(ErrorCodes.InsufficientFunds);
        }

        // Same transaction, row still exclusively locked by us: this is exactly the balance our update produced.
        var balanceAfter = await db.Wallets.Where(w => w.Id == id).Select(w => w.Balance).SingleAsync(ct);

        var transaction = new WalletTransaction
        {
            Id = Guid.NewGuid(),
            WalletId = id,
            ProviderId = c.ProviderId,
            Reference = c.Reference,
            Type = handler.Type,
            Amount = amount,
            BalanceAfter = balanceAfter,
            RelatedTransactionId = relatedId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Transactions.Add(transaction);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Undo the balance change together with the failed insert.
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }

        await tx.CommitAsync(ct);
        return ProcessResult.Created(transaction);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };
}
