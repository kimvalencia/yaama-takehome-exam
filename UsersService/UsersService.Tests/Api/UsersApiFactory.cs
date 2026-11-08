using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UsersService.Email;
using UsersService.Identity;

namespace UsersService.Tests.Api;

/// <summary>
/// The real Users API in-process, in environment <c>Testing</c> (no migrate-on-start; the SQL fixture migrates),
/// pointed at the fixture's database, with Keycloak and SMTP replaced by in-memory fakes.
/// </summary>
public sealed class UsersApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public const string PublicBaseUrl = "http://users.test";

    public FakeIdentityProvider Identity { get; } = new();
    public FakeEmailSender Email { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:UsersDb", connectionString);
        builder.UseSetting("Users:PublicBaseUrl", PublicBaseUrl);
        // In-memory bus with outbox delivery on: registration -> outbox -> email consumer -> FakeEmailSender.
        builder.UseSetting("Messaging:Transport", "InMemory");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IIdentityProvider>();
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IIdentityProvider>(Identity);
            services.AddSingleton<IEmailSender>(Email);
        });
    }
}
