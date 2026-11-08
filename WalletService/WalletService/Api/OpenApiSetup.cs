using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using WalletService.Auth;

namespace WalletService.Api;

/// <summary>
/// OpenAPI document (served at <c>/openapi/v1.json</c>) and the Scalar explorer (<c>/scalar</c>):
/// API description with "How to authenticate", OAuth2 security schemes, per-operation security and examples.
/// </summary>
public static class OpenApiSetup
{
    public const string TokenUrl = "http://localhost:8080/realms/gaming/protocol/openid-connect/token";
    public const string ProviderScheme = "provider";
    public const string PlayerScheme = "player";

    private const string Description = $$"""
        Mini wallet: USD, EUR and BTC wallets per player, a single idempotent provider endpoint for BET/WIN,
        player balances, history and a simulated top-up, plus admin views of any player.

        Money is always a JSON **string** (`"12.50"`): USD/EUR take at most 2 decimals, BTC at most 8, amounts are
        positive. Currency and type are case-insensitive and returned upper-case. Errors are RFC 7807
        `application/problem+json` with a stable `code`: `VALIDATION_FAILED` (400), `EMAIL_NOT_VERIFIED` (403),
        `UNKNOWN_PLAYER` (404), `DUPLICATE_REFERENCE_MISMATCH` (409), `INSUFFICIENT_FUNDS`, `UNKNOWN_BET`,
        `BET_ALREADY_SETTLED` (422).

        ## How to authenticate

        All endpoints need a Keycloak (realm `gaming`) access token sent as `Authorization: Bearer <token>`.
        In this explorer, open **Authentication**, pick a scheme and request a token; or use `curl`:

        **Game provider** (client credentials; the client id becomes the provider id):

        ```bash
        curl -s -X POST {{TokenUrl}} \
          -d grant_type=client_credentials \
          -d client_id=provider-demo \
          -d client_secret=provider-secret
        ```

        **Player or admin** (password grant on the public `wallet-web` client; the player must have verified their email):

        ```bash
        curl -s -X POST {{TokenUrl}} \
          -d grant_type=password \
          -d client_id=wallet-web \
          -d username=admin@example.com \
          -d password='Admin123!'
        ```

        Then call the API with the `access_token` from the response (`WALLET_URL` is this API's base URL):

        ```bash
        TOKEN=$(curl -s -X POST {{TokenUrl}} -d grant_type=client_credentials \
          -d client_id=provider-demo -d client_secret=provider-secret | jq -r .access_token)
        curl -s -X POST "$WALLET_URL/v1/transactions" -H "Authorization: Bearer $TOKEN" \
          -H "Content-Type: application/json" \
          -d '{"reference":"round-42-bet","type":"BET","playerId":"<player id>","currency":"USD","amount":"12.50"}'
        ```

        Tokens are issued for `http://localhost:8080/realms/gaming`, so request them through `localhost:8080`.

        Roles: `game-provider` for `POST /v1/transactions`, `player` for the `me` routes (top-up also needs
        `email_verified=true`), `admin` for `/v1/players/{playerId}/...`.
        """;

    public static IServiceCollection AddWalletOpenApi(this IServiceCollection services) =>
        services.AddOpenApi("v1", o =>
        {
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Info = new OpenApiInfo { Title = "Mini Wallet API", Version = "v1", Description = Description };
                doc.Components ??= new OpenApiComponents();
                doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
                doc.Components.SecuritySchemes[ProviderScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.OAuth2,
                    Description = "Game providers: OAuth2 client credentials (e.g. `provider-demo` / `provider-secret`). Role `game-provider`.",
                    Flows = new OpenApiOAuthFlows
                    {
                        ClientCredentials = new OpenApiOAuthFlow
                        {
                            TokenUrl = new Uri(TokenUrl),
                            Scopes = new Dictionary<string, string>(),
                        },
                    },
                };
                doc.Components.SecuritySchemes[PlayerScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.OAuth2,
                    Description = "Players and admins: OAuth2 password grant on public client `wallet-web`.",
                    Flows = new OpenApiOAuthFlows
                    {
                        Password = new OpenApiOAuthFlow
                        {
                            TokenUrl = new Uri(TokenUrl),
                            Scopes = new Dictionary<string, string>(),
                        },
                    },
                };
                return Task.CompletedTask;
            });

            o.AddOperationTransformer((op, ctx, _) =>
            {
                var policies = ctx.Description.ActionDescriptor.EndpointMetadata
                    .OfType<IAuthorizeData>().Select(a => a.Policy).ToHashSet();
                var scheme = policies.Contains(Policies.Provider) ? ProviderScheme
                    : policies.Count > 0 ? PlayerScheme : null;
                if (scheme is not null)
                {
                    op.Security =
                    [
                        new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(scheme, ctx.Document)] = [] },
                    ];
                }

                AddExamples(op, ctx.Description.ActionDescriptor.EndpointMetadata
                    .OfType<IEndpointNameMetadata>().FirstOrDefault()?.EndpointName);
                return Task.CompletedTask;
            });
        });

    public static WebApplication MapWalletOpenApi(this WebApplication app)
    {
        app.MapOpenApi("/openapi/{documentName}.json");
        app.MapScalarApiReference("/scalar", o =>
        {
            o.WithTitle("Mini Wallet API")
                .WithOpenApiRoutePattern("/openapi/{documentName}.json")
                .AddPreferredSecuritySchemes(ProviderScheme, PlayerScheme)
                .AddClientCredentialsFlow(ProviderScheme, f =>
                {
                    f.ClientId = "provider-demo";
                    f.ClientSecret = "provider-secret"; // demo client from the imported realm, not a real secret
                })
                .AddPasswordFlow(PlayerScheme, f => f.ClientId = "wallet-web");
        });
        return app;
    }

    private static readonly string SampleTransactionId = "3f1c2b8e-6a51-4d0e-9a3b-7c2f5e9d1a04";
    private static readonly string SamplePlayerId = "8b6f0c2a-1d4e-4f3a-9c7b-2e5d6a7f8b90";

    private static JsonNode Transaction(string reference, string type, string currency, string amount,
        string balanceAfter, string? betReference = null, bool replayed = false) => new JsonObject
    {
        ["id"] = SampleTransactionId,
        ["reference"] = reference,
        ["type"] = type,
        ["currency"] = currency,
        ["amount"] = amount,
        ["balanceAfter"] = balanceAfter,
        ["betReference"] = betReference,
        ["createdAt"] = "2026-11-09T12:00:00+00:00",
        ["replayed"] = replayed,
    };

    private static JsonNode Problem(int status, string code, string title, string? detail = null) => new JsonObject
    {
        ["type"] = $"https://httpstatuses.io/{status}",
        ["title"] = title,
        ["status"] = status,
        ["detail"] = detail,
        ["code"] = code,
    };

    private static void AddExamples(OpenApiOperation op, string? endpoint)
    {
        switch (endpoint)
        {
            case "PostTransaction":
                RequestExamples(op,
                    ("bet", "BET 12.50 USD", new JsonObject
                    {
                        ["reference"] = "round-42-bet", ["type"] = "BET", ["playerId"] = SamplePlayerId,
                        ["currency"] = "USD", ["amount"] = "12.50",
                    }),
                    ("win", "WIN settling that bet", new JsonObject
                    {
                        ["reference"] = "round-42-win", ["type"] = "WIN", ["playerId"] = SamplePlayerId,
                        ["currency"] = "USD", ["amount"] = "30.00", ["betReference"] = "round-42-bet",
                    }));
                ResponseExample(op, "201", ("created", "Applied", Transaction("round-42-bet", "BET", "USD", "12.50", "87.50")));
                ResponseExample(op, "200", ("replayed", "Replay", Transaction("round-42-bet", "BET", "USD", "12.50", "87.50", replayed: true)));
                ResponseExample(op, "400", ("validation", "Bad amount", Problem(400, "VALIDATION_FAILED", "The request is invalid.", "USD amounts allow at most 2 decimal places.")));
                ResponseExample(op, "404", ("unknownPlayer", "Unknown player", Problem(404, "UNKNOWN_PLAYER", "The player or wallet does not exist.")));
                ResponseExample(op, "409", ("mismatch", "Reference reused", Problem(409, "DUPLICATE_REFERENCE_MISMATCH", "The reference was already used with a different payload.")));
                ResponseExample(op, "422",
                    ("insufficientFunds", "Insufficient funds", Problem(422, "INSUFFICIENT_FUNDS", "The wallet balance is too low.")),
                    ("unknownBet", "Unknown bet", Problem(422, "UNKNOWN_BET", "The referenced bet does not exist.")),
                    ("betAlreadySettled", "Bet already settled", Problem(422, "BET_ALREADY_SETTLED", "The referenced bet is already settled.")));
                break;
            case "Topup":
                RequestExamples(op, ("topup", "Top up 50 EUR", new JsonObject { ["currency"] = "EUR", ["amount"] = "50.00" }));
                ResponseExample(op, "201", ("created", "Applied", Transaction("7d0b7d3c-topup-key", "DEPOSIT", "EUR", "50.00", "50.00")));
                ResponseExample(op, "200", ("replayed", "Replay", Transaction("7d0b7d3c-topup-key", "DEPOSIT", "EUR", "50.00", "50.00", replayed: true)));
                ResponseExample(op, "400", ("validation", "Missing key", Problem(400, "VALIDATION_FAILED", "The request is invalid.", "The `Idempotency-Key` header is required and at most 100 characters.")));
                ResponseExample(op, "403", ("emailNotVerified", "Email not verified", Problem(403, "EMAIL_NOT_VERIFIED", "The email address is not verified.", "Verify your email address before topping up.")));
                ResponseExample(op, "409", ("mismatch", "Key reused", Problem(409, "DUPLICATE_REFERENCE_MISMATCH", "The reference was already used with a different payload.")));
                break;
            case "GetMyWallets" or "GetPlayerWallets":
                ResponseExample(op, "200", ("wallets", "Balances", new JsonArray(
                    new JsonObject { ["currency"] = "USD", ["balance"] = "87.50" },
                    new JsonObject { ["currency"] = "EUR", ["balance"] = "50.00" },
                    new JsonObject { ["currency"] = "BTC", ["balance"] = "0.00120000" })));
                break;
            case "GetMyTransactions" or "GetPlayerTransactions":
                ResponseExample(op, "200", ("page", "A page", new JsonObject
                {
                    ["items"] = new JsonArray(
                        Transaction("round-42-win", "WIN", "USD", "30.00", "117.50", "round-42-bet"),
                        Transaction("round-42-bet", "BET", "USD", "12.50", "87.50")),
                    ["nextCursor"] = Contract.EncodeCursor(1041),
                }));
                ResponseExample(op, "400", ("badCursor", "Bad cursor", Problem(400, "VALIDATION_FAILED", "The request is invalid.", "`cursor` is not a cursor returned by this API.")));
                break;
        }
    }

    private static void RequestExamples(OpenApiOperation op, params (string Key, string Summary, JsonNode Value)[] examples)
    {
        if (op.RequestBody?.Content is not { } content || !content.TryGetValue("application/json", out var media)) return;
        media.Examples ??= new Dictionary<string, IOpenApiExample>();
        foreach (var (key, summary, value) in examples)
            media.Examples[key] = new OpenApiExample { Summary = summary, Value = value };
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
