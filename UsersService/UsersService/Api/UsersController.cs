using Microsoft.AspNetCore.Mvc;
using UsersService.Registration;

namespace UsersService.Api;

[ApiController]
[Route("v1/users")]
[Tags("Registration")]
public sealed class UsersController(RegistrationService registration) : ControllerBase
{
    [HttpPost]
    [Consumes("application/json")]
    [EndpointName("Register")]
    [EndpointSummary("Register a player")]
    [EndpointDescription("""
        Creates the player's account (anonymous). The account is **disabled** until the email address is verified:
        a verification link valid for 24 hours is emailed (Mailpit in the local setup). Emails are matched
        case-insensitively and stored lower-cased. Password: at least 8 characters; display name: 1 to 100 characters.
        """)]
    [ProducesResponseType<RegisterResponse>(StatusCodes.Status202Accepted, "application/json", Description = "Registered; verification email sent.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemResults.ContentType, Description = "VALIDATION_FAILED: missing body, invalid email, short password or bad display name.")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, ProblemResults.ContentType, Description = "EMAIL_ALREADY_REGISTERED: an account with this email (in any letter case) exists.")]
    public async Task<IActionResult> Register([FromBody] RegisterBody body, CancellationToken ct)
    {
        var result = await registration.RegisterAsync(
            new RegisterRequest(body.Email ?? string.Empty, body.Password ?? string.Empty, body.DisplayName ?? string.Empty), ct);
        return result.UserId is { } userId
            ? StatusCode(StatusCodes.Status202Accepted, new RegisterResponse(userId))
            : ProblemResults.Result(result.ErrorCode!, result.Detail, HttpContext);
    }

    [HttpPost("verify")]
    [Consumes("application/json")]
    [EndpointName("VerifyEmail")]
    [EndpointSummary("Verify an email address")]
    [EndpointDescription("""
        Verifies the email with the token from the verification link (anonymous) and enables the account, so the
        player can log in. Idempotent: repeating it for an already verified account succeeds again.
        """)]
    [ProducesResponseType(StatusCodes.Status204NoContent, Description = "Verified (or already verified).")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemResults.ContentType, Description = "INVALID_TOKEN: unknown, expired or already used token; VALIDATION_FAILED: missing body or token.")]
    public async Task<IActionResult> Verify([FromBody] VerifyBody body, CancellationToken ct)
    {
        if (body.Token is null)
            return ProblemResults.Validation("`token` is required.", HttpContext);
        var result = await registration.VerifyAsync(body.Token, ct);
        return result.Success ? NoContent() : ProblemResults.Result(result.ErrorCode!, InvalidTokenDetail, HttpContext);
    }

    [HttpGet("verify")]
    [EndpointName("VerifyEmailLink")]
    [EndpointSummary("Verify an email address from the emailed link")]
    [EndpointDescription("""
        Same as `POST /v1/users/verify`, for the link in the verification email (links are GET requests).
        Returns `{"status":"verified"}`; idempotent.
        """)]
    [ProducesResponseType<VerifyResponse>(StatusCodes.Status200OK, "application/json", Description = "Verified (or already verified).")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, ProblemResults.ContentType, Description = "INVALID_TOKEN: missing, unknown, expired or already used token.")]
    public async Task<IActionResult> VerifyLink([FromQuery] string? token, CancellationToken ct)
    {
        var result = await registration.VerifyAsync(token ?? string.Empty, ct);
        return result.Success ? Ok(new VerifyResponse("verified")) : ProblemResults.Result(result.ErrorCode!, InvalidTokenDetail, HttpContext);
    }

    private const string InvalidTokenDetail =
        "The token is unknown, expired (links are valid for 24 hours) or was already used.";
}
