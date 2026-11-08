using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WalletService.Auth;
using WalletService.Domain;

namespace WalletService.Api;

/// <summary>
/// The same views as <c>/v1/players/me/...</c> for any player. Admin only: a player calling these routes gets 403,
/// even with their own id (players use the <c>me</c> routes).
/// </summary>
[ApiController]
[Route("v1/players/{playerId:guid}")]
[Tags("Admin")]
[Authorize(Policy = Policies.Admin)]
public sealed class AdminWalletController(WalletQueries queries) : ControllerBase
{
    [HttpGet("wallets")]
    [EndpointName("GetPlayerWallets")]
    [EndpointSummary("A player's wallet balances (admin)")]
    [EndpointDescription("Requires role `admin`.")]
    [ProducesResponseType<IReadOnlyList<WalletResponse>>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemResults.ContentType, Description = "Missing or invalid bearer token.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemResults.ContentType, Description = "Caller is not an admin.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemResults.ContentType, Description = "UNKNOWN_PLAYER: no wallets exist for this player id.")]
    public async Task<IActionResult> Wallets(Guid playerId, CancellationToken ct)
    {
        var wallets = await queries.WalletsAsync(playerId, ct);
        return wallets.Count == 0 ? ProblemResults.Result(ErrorCodes.UnknownPlayer, null, HttpContext) : Ok(wallets);
    }

    [HttpGet("transactions")]
    [EndpointName("GetPlayerTransactions")]
    [EndpointSummary("A player's transaction history (admin)")]
    [EndpointDescription("Requires role `admin`. " + WalletQueries.HistoryDescription)]
    [ProducesResponseType<TransactionPage>(StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemResults.ContentType, Description = "VALIDATION_FAILED: malformed cursor/limit, unknown currency or type.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized, ProblemResults.ContentType, Description = "Missing or invalid bearer token.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, ProblemResults.ContentType, Description = "Caller is not an admin.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, ProblemResults.ContentType, Description = "UNKNOWN_PLAYER: no wallets exist for this player id.")]
    public async Task<IActionResult> Transactions(Guid playerId, [FromQuery] HistoryQuery query, CancellationToken ct)
    {
        if (!await queries.PlayerExistsAsync(playerId, ct))
            return ProblemResults.Result(ErrorCodes.UnknownPlayer, null, HttpContext);
        return await queries.HistoryResultAsync(playerId, query, this, ct);
    }
}
