using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace UsersService.Api;

/// <summary>
/// OpenAPI document (served at <c>/openapi/v1.json</c>) and the Scalar explorer (<c>/scalar</c>), with examples.
/// </summary>
public static class OpenApiSetup
{
    private const string Description = """
        Player registration and email verification. Both endpoints are anonymous.

        1. `POST /v1/users` registers a player. The Keycloak account is created **disabled**, so the player cannot
           log in yet, and a verification link (valid 24 hours) is emailed. In the local setup open Mailpit at
           `http://localhost:8025` to read it.
        2. Opening the link (`GET /v1/users/verify?token=...`) or posting the token to `POST /v1/users/verify`
           enables the account. Verifying again is harmless.
        3. The player then logs in through Keycloak (realm `gaming`, public client `wallet-web`) and uses the
           Wallet API with the access token.

        Errors are RFC 7807 `application/problem+json` with a stable `code`: `VALIDATION_FAILED` (400),
        `INVALID_TOKEN` (400), `EMAIL_ALREADY_REGISTERED` (409).
        """;

    public static IServiceCollection AddUsersOpenApi(this IServiceCollection services) =>
        services.AddOpenApi("v1", o =>
        {
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Info = new OpenApiInfo { Title = "Users API", Version = "v1", Description = Description };
                return Task.CompletedTask;
            });
            o.AddOperationTransformer((op, ctx, _) =>
            {
                AddExamples(op, ctx.Description.ActionDescriptor.EndpointMetadata
                    .OfType<IEndpointNameMetadata>().FirstOrDefault()?.EndpointName);
                return Task.CompletedTask;
            });
        });

    public static WebApplication MapUsersOpenApi(this WebApplication app)
    {
        app.MapOpenApi("/openapi/{documentName}.json");
        app.MapScalarApiReference("/scalar", o => o
            .WithTitle("Users API")
            .WithOpenApiRoutePattern("/openapi/{documentName}.json"));
        return app;
    }

    private static JsonNode Problem(int status, string code, string title, string? detail = null) => new JsonObject
    {
        ["type"] = $"https://httpstatuses.io/{status}",
        ["title"] = title,
        ["status"] = status,
        ["detail"] = detail,
        ["code"] = code,
    };

    private static readonly JsonNode InvalidToken = Problem(400, "INVALID_TOKEN",
        "The verification token is invalid or expired.",
        "The token is unknown, expired (links are valid for 24 hours) or was already used.");

    private static void AddExamples(OpenApiOperation op, string? endpoint)
    {
        switch (endpoint)
        {
            case "Register":
                RequestExamples(op, ("register", "Register a player", new JsonObject
                {
                    ["email"] = "jane@example.com", ["password"] = "Secret123!", ["displayName"] = "Jane",
                }));
                ResponseExample(op, "202", ("registered", "Registered",
                    new JsonObject { ["userId"] = "8b6f0c2a-1d4e-4f3a-9c7b-2e5d6a7f8b90" }));
                ResponseExample(op, "400",
                    ("badEmail", "Invalid email", Problem(400, "VALIDATION_FAILED", "The request is invalid.", "`email` must be a valid email address.")),
                    ("shortPassword", "Short password", Problem(400, "VALIDATION_FAILED", "The request is invalid.", "`password` must be 8 to 128 characters.")));
                ResponseExample(op, "409", ("taken", "Email taken", Problem(409, "EMAIL_ALREADY_REGISTERED",
                    "The email address is already registered.", "An account with this email address already exists.")));
                break;
            case "VerifyEmail":
                RequestExamples(op, ("verify", "Token from the email", new JsonObject
                {
                    ["token"] = "q3J0mX9cV1bN7sT2kL5wR8yZ4uA6eH0dG3fP1iO9jKc",
                }));
                ResponseExample(op, "400",
                    ("invalidToken", "Invalid or expired token", InvalidToken),
                    ("missingToken", "Missing token", Problem(400, "VALIDATION_FAILED", "The request is invalid.", "`token` is required.")));
                break;
            case "VerifyEmailLink":
                ResponseExample(op, "200", ("verified", "Verified", new JsonObject { ["status"] = "verified" }));
                ResponseExample(op, "400", ("invalidToken", "Invalid or expired token", InvalidToken));
                break;
        }
    }

    private static void RequestExamples(OpenApiOperation op, params (string Key, string Summary, JsonNode Value)[] examples)
    {
        if (op.RequestBody?.Content is not { } content || !content.TryGetValue("application/json", out var media)) return;
        media.Examples ??= new Dictionary<string, IOpenApiExample>();
        foreach (var (key, summary, value) in examples)
            media.Examples[key] = new OpenApiExample { Summary = summary, Value = value.DeepClone() };
    }

    private static void ResponseExample(OpenApiOperation op, string status, params (string Key, string Summary, JsonNode Value)[] examples)
    {
        if (op.Responses is null || !op.Responses.TryGetValue(status, out var response) || response.Content is null) return;
        foreach (var media in response.Content.Values)
        {
            media.Examples ??= new Dictionary<string, IOpenApiExample>();
            foreach (var (key, summary, value) in examples)
                media.Examples[key] = new OpenApiExample { Summary = summary, Value = value.DeepClone() };
        }
    }
}
