using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using WalletService.Auth;
using WalletService.Domain;
using WalletService.Transactions;

namespace WalletService.Api;

[ApiController]
[Route("v1/transactions")]
[Tags("Provider")]
public sealed class TransactionsController(TransactionProcessor processor, IEnumerable<ITransactionHandler> handlers)
    : ControllerBase
{
    /// <summary>Types a provider may post. DEPOSIT is reserved for the player top-up endpoint.</summary>
    private readonly string[] _providerTypes = handlers.Select(h => h.Type)
        .Where(t => t != DepositHandler.TypeName).OrderBy(t => t).ToArray();

    [HttpPost]
    [Authorize(Policy = Policies.Provider)]
    [Consumes("application/json")]
    [EndpointName("PostTransaction")]
    [EndpointSummary("Apply a BET or WIN to a player's wallet")]
    [EndpointDescription("""
        The single provider endpoint. Requires a client-credentials token with role `game-provider`;
        the provider id is the token's `azp` (client id).

        Idempotent per (provider, `reference`): repeating a request with the same payload returns the original
        transaction with **200** and `replayed: true`; reusing a reference with a different payload returns **409**.
        A WIN must name, in `betReference`, an existing BET of the same provider, player and currency, and a BET can
        be settled only once.
        """)]
    [ProducesResponseType<TransactionResponse>(StatusCodes.Status201Created, "application/json", Description = "Transaction applied.")]
    [ProducesResponseType<TransactionResponse>(StatusCodes.Status200OK, "application/json", Description = "Replay of an identical earlier request; nothing changed.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemResults.ContentType, Description = "VALIDATION_FAILED: malformed body, bad amount/currency/type, reference or provider id over 100 chars, WIN without betReference, or a credit that would overflow the balance.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemResults.ContentType, Description = "Missing or invalid bearer token.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemResults.ContentType, Description = "Token lacks role `game-provider`, or has no `azp` claim.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemResults.ContentType, Description = "UNKNOWN_PLAYER: the player has no wallet in this currency.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemResults.ContentType, Description = "DUPLICATE_REFERENCE_MISMATCH: reference already used with a different payload.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity, ProblemResults.ContentType, Description = "INSUFFICIENT_FUNDS, UNKNOWN_BET or BET_ALREADY_SETTLED.")]
    public async Task<IActionResult> Post([FromBody] TransactionRequest request, CancellationToken ct)
    {
        var providerId = User.ClientId();
        if (string.IsNullOrWhiteSpace(providerId))
            return ProblemResults.Forbidden("Provider tokens must carry an `azp` (client id) claim.", HttpContext);
        if (providerId.Length > Contract.MaxReferenceLength)
            return ProblemResults.Validation($"Provider id is longer than {Contract.MaxReferenceLength} characters.", HttpContext);

        if (string.IsNullOrWhiteSpace(request.Reference) || request.Reference.Length > Contract.MaxReferenceLength)
            return ProblemResults.Validation($"`reference` is required and at most {Contract.MaxReferenceLength} characters.", HttpContext);
        if (request.PlayerId == Guid.Empty)
            return ProblemResults.Validation("`playerId` is required.", HttpContext);
        if (!Contract.TryNormaliseType(request.Type, _providerTypes, out var type, out var error))
            return ProblemResults.Validation(error!, HttpContext);
        if (!Contract.TryParseCurrency(request.Currency, out var currency, out error))
            return ProblemResults.Validation(error!, HttpContext);
        if (!Contract.TryParseAmount(request.Amount, currency, out var amount, out error))
            return ProblemResults.Validation(error!, HttpContext);
        if (request.BetReference is { Length: > Contract.MaxReferenceLength })
            return ProblemResults.Validation($"`betReference` is at most {Contract.MaxReferenceLength} characters.", HttpContext);
        if (type == WinHandler.TypeName && string.IsNullOrWhiteSpace(request.BetReference))
            return ProblemResults.Validation("A WIN requires `betReference`.", HttpContext);

        var command = new TransactionCommand(providerId, request.Reference, type, request.PlayerId, currency, amount,
            type == WinHandler.TypeName ? request.BetReference : null);
        return await Execute(processor, command, this, ct);
    }

    /// <summary>Runs a command and maps the outcome: 201 created, 200 replay, problem+json otherwise.</summary>
    internal static async Task<IActionResult> Execute(
        TransactionProcessor processor, TransactionCommand command, ControllerBase controller, CancellationToken ct)
    {
        ProcessResult result;
        try
        {
            result = await processor.ProcessAsync(command, ct);
        }
        catch (Exception ex) when (IsArithmeticOverflow(ex))
        {
            // The resulting balance would not fit decimal(28,8); the processor's DB transaction rolled back.
            return ProblemResults.Validation("The amount would overflow the wallet balance.", controller.HttpContext);
        }

        if (result.Status == ProcessStatus.Rejected || result.Transaction is null)
            return ProblemResults.Result(result.ErrorCode ?? ErrorCodes.ValidationFailed, null, controller.HttpContext);

        var t = result.Transaction;
        var replayed = result.Status == ProcessStatus.Replayed;
        var body = new TransactionResponse(t.Id, t.Reference, t.Type, command.Currency.ToString(),
            Contract.FormatMoney(t.Amount, command.Currency), Contract.FormatMoney(t.BalanceAfter, command.Currency),
            t.RelatedTransactionId is null ? null : command.BetReference, t.CreatedAt, replayed);
        return controller.StatusCode(replayed ? StatusCodes.Status200OK : StatusCodes.Status201Created, body);
    }

    private static bool IsArithmeticOverflow(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is SqlException { Number: 8115 }) return true;
        return false;
    }
}
