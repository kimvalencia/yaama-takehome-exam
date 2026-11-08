using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WalletService.Auth;
using WalletService.Data;
using WalletService.Domain;
using WalletService.Transactions;

namespace WalletService.Api;

[ApiController]
[Route("v1/players/me")]
[Tags("Player")]
[Authorize(Policy = Policies.Player)]
public sealed class PlayerWalletController(WalletQueries queries) : ControllerBase
{
    [HttpGet("wallets")]
    [EndpointName("GetMyWallets")]
    [EndpointSummary("My wallet balances")]
    [EndpointDescription("All wallets (USD, EUR, BTC) of the caller (token `sub`). Empty until the wallets have been created after registration.")]
    [ProducesResponseType<IReadOnlyList<WalletResponse>>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemResults.ContentType, Description = "Missing or invalid bearer token.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemResults.ContentType, Description = "Not a player token.")]
    public async Task<IActionResult> Wallets(CancellationToken ct)
    {
        if (User.PlayerId() is not { } playerId)
            return ProblemResults.Forbidden("The token's `sub` is not a player id.", HttpContext);
        return Ok(await queries.WalletsAsync(playerId, ct));
    }

    [HttpGet("transactions")]
    [EndpointName("GetMyTransactions")]
    [EndpointSummary("My transaction history")]
    [EndpointDescription(WalletQueries.HistoryDescription)]
    [ProducesResponseType<TransactionPage>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemResults.ContentType, Description = "VALIDATION_FAILED: malformed cursor/limit, unknown currency or type.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemResults.ContentType, Description = "Missing or invalid bearer token.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemResults.ContentType, Description = "Not a player token.")]
    public async Task<IActionResult> Transactions([FromQuery] HistoryQuery query, CancellationToken ct)
    {
        if (User.PlayerId() is not { } playerId)
            return ProblemResults.Forbidden("The token's `sub` is not a player id.", HttpContext);
        return await queries.HistoryResultAsync(playerId, query, this, ct);
    }
}

/// <summary>History query string. Bound as strings/ints so malformed values become 400s, never 500s.</summary>
public sealed class HistoryQuery
{
    /// <summary>Page size, clamped to 1..100 (default 20).</summary>
    [FromQuery(Name = "limit")] public int? Limit { get; init; }

    /// <summary>The `nextCursor` of the previous page.</summary>
    [FromQuery(Name = "cursor")] public string? Cursor { get; init; }

    /// <summary>Only this currency (USD, EUR, BTC; case-insensitive).</summary>
    [FromQuery(Name = "currency")] public string? Currency { get; init; }

    /// <summary>Only this type (BET, WIN, DEPOSIT; case-insensitive).</summary>
    [FromQuery(Name = "type")] public string? Type { get; init; }
}

/// <summary>Read side shared by the player ("me") and admin views.</summary>
public sealed class WalletQueries(WalletDbContext db, IEnumerable<ITransactionHandler> handlers)
{
    public const string HistoryDescription = """
        Newest first. Cursor pagination: pass the previous page's `nextCursor` as `cursor` until it is null.
        `limit` is clamped to 1..100 (default 20). Optional filters `currency` and `type` (case-insensitive).
        """;

    private readonly string[] _types = handlers.Select(h => h.Type).OrderBy(t => t).ToArray();

    public async Task<bool> PlayerExistsAsync(Guid playerId, CancellationToken ct) =>
        await db.Wallets.AnyAsync(w => w.PlayerId == playerId, ct);

    public async Task<IReadOnlyList<WalletResponse>> WalletsAsync(Guid playerId, CancellationToken ct)
    {
        var wallets = await db.Wallets.AsNoTracking()
            .Where(w => w.PlayerId == playerId)
            .Select(w => new { w.Currency, w.Balance })
            .ToListAsync(ct);
        return wallets.OrderBy(w => w.Currency)
            .Select(w => new WalletResponse(w.Currency.ToString(), Contract.FormatMoney(w.Balance, w.Currency)))
            .ToList();
    }

    public async Task<IActionResult> HistoryResultAsync(Guid playerId, HistoryQuery query, ControllerBase controller,
        CancellationToken ct)
    {
        long? before = null;
        if (query.Cursor is not null)
        {
            if (!Contract.TryDecodeCursor(query.Cursor, out var seq))
                return ProblemResults.Validation("`cursor` is not a cursor returned by this API.", controller.HttpContext);
            before = seq;
        }

        Currency? currency = null;
        if (query.Currency is not null)
        {
            if (!Contract.TryParseCurrency(query.Currency, out var c, out var error))
                return ProblemResults.Validation(error!, controller.HttpContext);
            currency = c;
        }

        string? type = null;
        if (query.Type is not null)
        {
            if (!Contract.TryNormaliseType(query.Type, _types, out var t, out var error))
                return ProblemResults.Validation(error!, controller.HttpContext);
            type = t;
        }

        return controller.Ok(await HistoryAsync(playerId, Contract.ClampLimit(query.Limit), before, currency, type, ct));
    }

    private async Task<TransactionPage> HistoryAsync(Guid playerId, int limit, long? beforeSeq, Currency? currency,
        string? type, CancellationToken ct)
    {
        var q = from t in db.Transactions.AsNoTracking()
            join w in db.Wallets.AsNoTracking() on t.WalletId equals w.Id
            where w.PlayerId == playerId
            select new { t, w.Currency };
        if (currency is { } c) q = q.Where(x => x.Currency == c);
        if (type is not null) q = q.Where(x => x.t.Type == type);
        if (beforeSeq is { } s) q = q.Where(x => x.t.Seq < s);

        var rows = await q
            .OrderByDescending(x => x.t.Seq)
            .Take(limit + 1)
            .Select(x => new
            {
                x.t,
                x.Currency,
                BetReference = db.Transactions.Where(r => r.Id == x.t.RelatedTransactionId)
                    .Select(r => r.Reference).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var page = rows.Take(limit).ToList();
        var items = page.Select(x => new TransactionResponse(
                x.t.Id, x.t.Reference, x.t.Type, x.Currency.ToString(),
                Contract.FormatMoney(x.t.Amount, x.Currency), Contract.FormatMoney(x.t.BalanceAfter, x.Currency),
                x.BetReference, x.t.CreatedAt, Replayed: false))
            .ToList();
        var next = rows.Count > limit ? Contract.EncodeCursor(page[^1].t.Seq) : null;
        return new TransactionPage(items, next);
    }
}
