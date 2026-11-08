using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UsersService.Api;
using UsersService.Data;
using UsersService.Domain;
using UsersService.Email;
using UsersService.Identity;
using UsersService.Messaging;
using UsersService.Registration;

var builder = WebApplication.CreateBuilder(args);

// Read lazily from IConfiguration so environment variables and test hosts can override the connection string.
builder.Services.AddDbContext<UsersDbContext>((sp, o) =>
    o.UseSqlServer(sp.GetRequiredService<IConfiguration>().GetConnectionString("UsersDb")
                   ?? throw new InvalidOperationException("ConnectionStrings:UsersDb is not configured.")));

builder.Services.Configure<UsersOptions>(builder.Configuration.GetSection(UsersOptions.Section));
builder.Services.Configure<KeycloakOptions>(builder.Configuration.GetSection(KeycloakOptions.Section));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.Section));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<IIdentityProvider, KeycloakIdentityProvider>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<RegistrationService>();

// EF bus outbox (PlayerRegistered, VerificationEmailRequested) and the email consumer, over RabbitMQ or in-memory.
builder.Services.AddUsersMessaging(builder.Configuration, builder.Environment);

builder.Services.AddProblemDetails();
builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(o =>
    {
        // Model binding failures (missing or malformed JSON, wrong JSON types, ...) become 400 VALIDATION_FAILED
        // problems, consistent with the service's own checks.
        o.InvalidModelStateResponseFactory = ctx =>
        {
            var problem = new ValidationProblemDetails(ctx.ModelState)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "The request is invalid.",
                Type = "https://httpstatuses.io/400",
                Detail = string.Join(" ", ctx.ModelState
                    .Where(e => e.Value?.Errors.Count > 0)
                    .Select(e => $"{(string.IsNullOrEmpty(e.Key) ? "body" : e.Key)}: {e.Value!.Errors[0].ErrorMessage}")),
            };
            problem.Extensions["code"] = ErrorCodes.ValidationFailed;
            problem.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
            return new BadRequestObjectResult(problem) { ContentTypes = { ProblemResults.ContentType } };
        };
    });

builder.Services.AddUsersOpenApi();

var app = builder.Build();

if (!app.Environment.IsEnvironment("Testing"))
    await MigrateWithRetryAsync(app);

app.UseExceptionHandler();
app.UseStatusCodePages(); // problem+json bodies for bare 404/405/415 responses

// OpenAPI + Scalar in all environments: the contract is part of the deliverable.
app.MapUsersOpenApi();

app.MapControllers();

await app.RunAsync();

// SQL Server in compose can take a while to accept connections; retry instead of crashing the container.
static async Task MigrateWithRetryAsync(WebApplication app)
{
    const int attempts = 10;
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<UsersDbContext>().Database.MigrateAsync();
            logger.LogInformation("Database migrated.");
            return;
        }
        catch (Exception ex) when (attempt < attempts)
        {
            logger.LogWarning(ex, "Migration attempt {Attempt}/{Attempts} failed; retrying in 3 s.", attempt, attempts);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }
}

public partial class Program { }
