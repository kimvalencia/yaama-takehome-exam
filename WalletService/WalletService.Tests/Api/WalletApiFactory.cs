using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace WalletService.Tests.Api;

/// <summary>
/// The real Wallet API in-process, in environment <c>Testing</c> (no migrate-on-start; the SQL fixture migrates),
/// pointed at the fixture's database, with JWT bearer replaced by <see cref="TestAuthHandler"/>.
/// </summary>
public sealed class WalletApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:WalletDb", connectionString);
        builder.UseSetting("Messaging:Transport", "InMemory"); // never needs RabbitMQ
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(o =>
                {
                    o.DefaultScheme = TestAuthHandler.SchemeName;
                    o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    o.DefaultForbidScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }
}

/// <summary>Who a test request acts as. Maps to the <see cref="TestAuthHandler"/> headers.</summary>
public sealed record TestCaller(string? Sub = null, string? Roles = null, string? Azp = null, string? EmailVerified = null)
{
    public static TestCaller Provider(string azp) => new(Sub: Guid.NewGuid().ToString(), Roles: "game-provider,default-roles-gaming", Azp: azp);
    public static TestCaller ProviderWithoutAzp() => new(Sub: Guid.NewGuid().ToString(), Roles: "game-provider,default-roles-gaming");
    public static TestCaller Player(Guid id, bool? emailVerified = true) =>
        new(Sub: id.ToString(), Roles: "player,default-roles-gaming,offline_access", Azp: "wallet-web",
            EmailVerified: emailVerified?.ToString().ToLowerInvariant());
    public static TestCaller Admin() => new(Sub: Guid.NewGuid().ToString(), Roles: "admin,default-roles-gaming", Azp: "wallet-web", EmailVerified: "true");

    public void Apply(HttpRequestMessage request)
    {
        if (Sub is not null) request.Headers.Add(TestAuthHandler.SubHeader, Sub);
        if (Roles is not null) request.Headers.Add(TestAuthHandler.RolesHeader, Roles);
        if (Azp is not null) request.Headers.Add(TestAuthHandler.AzpHeader, Azp);
        if (EmailVerified is not null) request.Headers.Add(TestAuthHandler.EmailVerifiedHeader, EmailVerified);
    }
}
