using Microsoft.AspNetCore.Mvc;
using UsersService.Domain;

namespace UsersService.Api;

/// <summary>
/// Builds RFC 7807 <c>application/problem+json</c> responses carrying a stable machine-readable <c>code</c>.
/// </summary>
public static class ProblemResults
{
    public const string ContentType = "application/problem+json";

    private static readonly Dictionary<string, (int Status, string Title)> Known = new()
    {
        [ErrorCodes.ValidationFailed] = (StatusCodes.Status400BadRequest, "The request is invalid."),
        [ErrorCodes.InvalidToken] = (StatusCodes.Status400BadRequest, "The verification token is invalid or expired."),
        [ErrorCodes.EmailAlreadyRegistered] = (StatusCodes.Status409Conflict, "The email address is already registered."),
    };

    public static int StatusFor(string code) =>
        Known.TryGetValue(code, out var known) ? known.Status : StatusCodes.Status400BadRequest;

    public static ProblemDetails Create(string code, string? detail = null, HttpContext? http = null)
    {
        var status = StatusFor(code);
        var problem = new ProblemDetails
        {
            Status = status,
            Title = Known.TryGetValue(code, out var known) ? known.Title : code,
            Detail = detail,
            Type = $"https://httpstatuses.io/{status}",
        };
        problem.Extensions["code"] = code;
        if (http is not null) problem.Extensions["traceId"] = http.TraceIdentifier;
        return problem;
    }

    public static ObjectResult Result(string code, string? detail = null, HttpContext? http = null)
    {
        var problem = Create(code, detail, http);
        return new ObjectResult(problem) { StatusCode = problem.Status, ContentTypes = { ContentType } };
    }

    public static ObjectResult Validation(string detail, HttpContext? http = null) =>
        Result(ErrorCodes.ValidationFailed, detail, http);
}
