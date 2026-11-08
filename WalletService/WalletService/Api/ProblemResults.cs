using Microsoft.AspNetCore.Mvc;
using WalletService.Domain;

namespace WalletService.Api;

/// <summary>
/// Builds RFC 7807 <c>application/problem+json</c> responses carrying a stable machine-readable <c>code</c>.
/// </summary>
public static class ProblemResults
{
    public const string ContentType = "application/problem+json";

    private static readonly Dictionary<string, (int Status, string Title)> Known = new()
    {
        [ErrorCodes.ValidationFailed] = (StatusCodes.Status400BadRequest, "The request is invalid."),
        [ErrorCodes.UnknownPlayer] = (StatusCodes.Status404NotFound, "The player or wallet does not exist."),
        [ErrorCodes.DuplicateReferenceMismatch] = (StatusCodes.Status409Conflict, "The reference was already used with a different payload."),
        [ErrorCodes.InsufficientFunds] = (StatusCodes.Status422UnprocessableEntity, "The wallet balance is too low."),
        [ErrorCodes.UnknownBet] = (StatusCodes.Status422UnprocessableEntity, "The referenced bet does not exist."),
        [ErrorCodes.BetAlreadySettled] = (StatusCodes.Status422UnprocessableEntity, "The referenced bet is already settled."),
        [ErrorCodes.EmailNotVerified] = (StatusCodes.Status403Forbidden, "The email address is not verified."),
    };

    /// <summary>HTTP status used for a given error code (unknown codes are treated as 422 business rejections).</summary>
    public static int StatusFor(string code) =>
        Known.TryGetValue(code, out var known) ? known.Status : StatusCodes.Status422UnprocessableEntity;

    public static ProblemDetails Create(string code, string? detail = null, int? status = null, HttpContext? http = null)
    {
        var resolvedStatus = status ?? StatusFor(code);
        var problem = new ProblemDetails
        {
            Status = resolvedStatus,
            Title = Known.TryGetValue(code, out var known) ? known.Title : code,
            Detail = detail,
            Type = $"https://httpstatuses.io/{resolvedStatus}",
        };
        problem.Extensions["code"] = code;
        if (http is not null) problem.Extensions["traceId"] = http.TraceIdentifier;
        return problem;
    }

    public static ObjectResult Result(string code, string? detail = null, HttpContext? http = null)
    {
        var problem = Create(code, detail, http: http);
        return new ObjectResult(problem) { StatusCode = problem.Status, ContentTypes = { ContentType } };
    }

    public static ObjectResult Validation(string detail, HttpContext? http = null) =>
        Result(ErrorCodes.ValidationFailed, detail, http);

    /// <summary>Plain 401/403 problem without a domain code (authentication/authorization failures).</summary>
    public static ObjectResult Forbidden(string detail, HttpContext? http = null)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Forbidden",
            Detail = detail,
            Type = "https://httpstatuses.io/403",
        };
        if (http is not null) problem.Extensions["traceId"] = http.TraceIdentifier;
        return new ObjectResult(problem) { StatusCode = problem.Status, ContentTypes = { ContentType } };
    }
}
