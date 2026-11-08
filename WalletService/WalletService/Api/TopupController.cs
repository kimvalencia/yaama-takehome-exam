using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WalletService.Auth;
using WalletService.Transactions;

namespace WalletService.Api;

[ApiController]
[Route("v1/wallet")]
[Tags("Player")]
public sealed class TopupController(TransactionProcessor processor) : ControllerBase
{
    /// <summary>
    /// Provider id under which a player's top-ups are recorded: <c>topup:{playerId}</c> (lower-case GUID with hyphens,
    /// 42 chars). The reference is the raw Idempotency-Key. Scoping the provider id per player keeps idempotency keys
    /// private to each player under the (ProviderId, Reference) unique index, so one player can neither collide with
    /// nor pre-claim another player's keys.
    /// </summary>
    public static string TopupProviderId(Guid playerId) => $"topup:{playerId:D}";

    [HttpPost("topup")]
    [Authorize(Policy = Policies.VerifiedPlayer)]
    [Consumes("application/json")]
    [EndpointName("Topup")]
    [EndpointSummary("Top up one of my wallets (simulated payment)")]
    [EndpointDescription("""
        Credits the caller's wallet with a DEPOSIT. Requires role `player` and `email_verified=true`.
        The `Idempotency-Key` header (max 100 chars) makes retries safe: the same key and payload returns the
        original transaction with **200** and `replayed: true`; the same key with a different payload returns **409**.
        Keys are scoped to the calling player, so different players may use the same key independently.
        """)]
    [ProducesResponseType<TransactionResponse>(StatusCodes.Status201Created, "application/json", Description = "Top-up applied.")]
    [ProducesResponseType<TransactionResponse>(StatusCodes.Status200OK, "application/json", Description = "Replay of an identical earlier top-up.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemResults.ContentType, Description = "VALIDATION_FAILED: missing/too long Idempotency-Key, bad amount or currency, or a balance overflow.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemResults.ContentType, Description = "Missing or invalid bearer token.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemResults.ContentType, Description = "EMAIL_NOT_VERIFIED, or the token is not a player token.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemResults.ContentType, Description = "UNKNOWN_PLAYER: the caller has no wallet in this currency yet.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemResults.ContentType, Description = "DUPLICATE_REFERENCE_MISMATCH: the Idempotency-Key was already used with a different payload.")]
    public async Task<IActionResult> Topup(
        [FromBody] TopupRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (User.PlayerId() is not { } playerId)
            return ProblemResults.Forbidden("The token's `sub` is not a player id.", HttpContext);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > Contract.MaxReferenceLength)
            return ProblemResults.Validation(
                $"The `Idempotency-Key` header is required and at most {Contract.MaxReferenceLength} characters.", HttpContext);
        if (!Contract.TryParseCurrency(request.Currency, out var currency, out var error))
            return ProblemResults.Validation(error!, HttpContext);
        if (!Contract.TryParseAmount(request.Amount, currency, out var amount, out error))
            return ProblemResults.Validation(error!, HttpContext);

        var command = new TransactionCommand(TopupProviderId(playerId), idempotencyKey, DepositHandler.TypeName, playerId,
            currency, amount, null);
        return await TransactionsController.Execute(processor, command, this, ct);
    }
}
