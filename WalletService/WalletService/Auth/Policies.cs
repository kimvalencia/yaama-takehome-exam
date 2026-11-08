using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace WalletService.Auth;

/// <summary>Authorization policy names and the Keycloak claim/role names they rely on.</summary>
public static class Policies
{
    public const string Provider = "Provider";
    public const string Player = "Player";
    public const string Admin = "Admin";
    public const string VerifiedPlayer = "VerifiedPlayer";

    public static class Roles
    {
        public const string Provider = "game-provider";
        public const string Player = "player";
        public const string Admin = "admin";
    }

    public static class Claims
    {
        public const string Subject = "sub";
        public const string AuthorizedParty = "azp";
        public const string EmailVerified = "email_verified";
        public const string Roles = "roles";
    }

    /// <summary>The token's <c>sub</c> as a player id, or null when it is missing or not a GUID.</summary>
    public static Guid? PlayerId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(Claims.Subject), out var id) && id != Guid.Empty ? id : null;

    /// <summary>The token's <c>azp</c> (Keycloak client id), which is the provider id for provider tokens.</summary>
    public static string? ClientId(this ClaimsPrincipal user) => user.FindFirstValue(Claims.AuthorizedParty);

    public static void AddWalletPolicies(this AuthorizationOptions options)
    {
        // RequireRole checks membership: Keycloak's flat `roles` claim also carries default-roles-gaming etc.
        options.AddPolicy(Provider, p => p.RequireAuthenticatedUser().RequireRole(Roles.Provider));
        options.AddPolicy(Player, p => p.RequireAuthenticatedUser().RequireRole(Roles.Player));
        options.AddPolicy(Admin, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin));
        options.AddPolicy(VerifiedPlayer, p => p.RequireAuthenticatedUser().RequireRole(Roles.Player)
            .AddRequirements(new EmailVerifiedRequirement()));
    }
}

/// <summary>
/// Requires <c>email_verified=true</c>. A dedicated requirement type (rather than RequireClaim) lets
/// <see cref="ProblemAuthorizationResultHandler"/> recognise this failure and answer <c>EMAIL_NOT_VERIFIED</c>.
/// </summary>
public sealed class EmailVerifiedRequirement : AuthorizationHandler<EmailVerifiedRequirement>, IAuthorizationRequirement
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, EmailVerifiedRequirement requirement)
    {
        // JWT booleans arrive as the string "true"; compare case-insensitively to be tolerant of "True".
        if (context.User.FindAll(Policies.Claims.EmailVerified)
            .Any(c => string.Equals(c.Value, "true", StringComparison.OrdinalIgnoreCase)))
        {
            context.Succeed(requirement);
        }
        return Task.CompletedTask;
    }
}
