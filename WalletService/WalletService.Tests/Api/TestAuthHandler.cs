using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WalletService.Tests.Api;

/// <summary>
/// Test-only authentication: each request chooses its own identity through headers, so tests can
/// exercise every role/claim combination without Keycloak. No <c>X-Test-Sub</c> and no
/// <c>X-Test-Azp</c> header means "anonymous" (the request gets a 401 challenge).
/// Claim names mirror the Keycloak access token: <c>sub</c>, flat <c>roles</c>, <c>azp</c>, <c>email_verified</c>.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string SubHeader = "X-Test-Sub";
    public const string RolesHeader = "X-Test-Roles";
    public const string AzpHeader = "X-Test-Azp";
    public const string EmailVerifiedHeader = "X-Test-EmailVerified";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var sub = Request.Headers[SubHeader].ToString();
        var azp = Request.Headers[AzpHeader].ToString();
        var roles = Request.Headers[RolesHeader].ToString();
        if (sub.Length == 0 && azp.Length == 0 && roles.Length == 0)
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim>();
        if (sub.Length > 0) claims.Add(new Claim("sub", sub));
        if (azp.Length > 0) claims.Add(new Claim("azp", azp));
        foreach (var role in roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            claims.Add(new Claim("roles", role));
        var emailVerified = Request.Headers[EmailVerifiedHeader].ToString();
        if (emailVerified.Length > 0) claims.Add(new Claim("email_verified", emailVerified, ClaimValueTypes.Boolean));

        var identity = new ClaimsIdentity(claims, SchemeName, nameType: "sub", roleType: "roles");
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
