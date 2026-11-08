using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WalletService.Api;
using WalletService.Domain;

namespace WalletService.Auth;

public static class AuthExtensions
{
    public const string DefaultMetadataAddress = "http://keycloak:8080/realms/gaming/.well-known/openid-configuration";
    public const string DefaultValidIssuer = "http://localhost:8080/realms/gaming";

    /// <summary>
    /// Keycloak JWT bearer validation plus the wallet policies.
    /// Signing keys are fetched from <c>Auth:MetadataAddress</c> (the in-network Keycloak URL), while the accepted
    /// issuer is <c>Auth:ValidIssuer</c>: the host-visible URL that clients use to obtain tokens.
    /// </summary>
    public static IServiceCollection AddWalletAuth(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured from IConfiguration lazily so test hosts and environment variables can override it.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IConfiguration>((o, config) =>
            {
                o.MetadataAddress = config["Auth:MetadataAddress"] ?? DefaultMetadataAddress;
                var authority = config["Auth:Authority"];
                if (!string.IsNullOrWhiteSpace(authority)) o.Authority = authority;
                o.RequireHttpsMetadata = false; // Keycloak runs over plain HTTP inside compose.
                o.MapInboundClaims = false;     // keep `sub`, `azp`, `roles`, `email_verified` as issued.
                o.TokenValidationParameters.ValidIssuer = config["Auth:ValidIssuer"] ?? DefaultValidIssuer;
                o.TokenValidationParameters.ValidateIssuer = true;
                // Deliberate dev simplification: Keycloak access tokens carry `aud: account` unless an audience
                // mapper is configured per client, so the audience is not validated. Roles and issuer still are.
                o.TokenValidationParameters.ValidateAudience = false;
                o.TokenValidationParameters.RoleClaimType = Policies.Claims.Roles;
                o.TokenValidationParameters.NameClaimType = Policies.Claims.Subject;
            });

        services.AddAuthorization(o => o.AddWalletPolicies());
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();
        return services;
    }
}

/// <summary>
/// Turns authorization failures into problem+json bodies. A failure on <see cref="EmailVerifiedRequirement"/> alone
/// (the caller is a player, just not verified) is reported as 403 with code <c>EMAIL_NOT_VERIFIED</c>.
/// </summary>
public sealed class ProblemAuthorizationResultHandler(IOptions<JsonOptions> json) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden && authorizeResult.AuthorizationFailure is { } failure)
        {
            var failed = failure.FailedRequirements.ToList();
            var problem = failed.Count > 0 && failed.All(r => r is EmailVerifiedRequirement)
                ? ProblemResults.Create(ErrorCodes.EmailNotVerified,
                    "Verify your email address before topping up.", StatusCodes.Status403Forbidden, context)
                : new ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "Forbidden",
                    Detail = "The access token does not grant access to this endpoint.",
                    Type = "https://httpstatuses.io/403",
                };

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(problem, json.Value.JsonSerializerOptions,
                ProblemResults.ContentType, context.RequestAborted);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
