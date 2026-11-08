using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WalletService.Api;
using WalletService.Auth;
using WalletService.Data;
using WalletService.Domain;
using WalletService.Messaging;
using WalletService.Transactions;

var builder = WebApplication.CreateBuilder(args);

// Read lazily from IConfiguration so environment variables and test hosts can override the connection string.
builder.Services.AddDbContext<WalletDbContext>((sp, o) =>
    o.UseSqlServer(sp.GetRequiredService<IConfiguration>().GetConnectionString("WalletDb")
                   ?? throw new InvalidOperationException("ConnectionStrings:WalletDb is not configured.")));

builder.Services.AddSingleton<ITransactionHandler, BetHandler>();
builder.Services.AddSingleton<ITransactionHandler, WinHandler>();
builder.Services.AddSingleton<ITransactionHandler, DepositHandler>();
builder.Services.AddScoped<TransactionProcessor>();
builder.Services.AddScoped<WalletQueries>();

builder.Services.AddWalletAuth();

// PlayerRegistered consumer (creates the wallets) over RabbitMQ, or in-memory in tests.
builder.Services.AddWalletMessaging(builder.Configuration, builder.Environment);

builder.Services.AddProblemDetails();
builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(o =>
    {
        // Model binding failures (missing fields, wrong JSON types, malformed JSON, non-numeric limit, ...)
        // become 400 VALIDATION_FAILED problems, consistent with the hand-written checks.
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

builder.Services.AddWalletOpenApi();

var app = builder.Build();

if (!app.Environment.IsEnvironment("Testing"))
    await MigrateWithRetryAsync(app);

app.UseExceptionHandler();
app.UseStatusCodePages(); // problem+json bodies for bare 401/404/405 responses

// OpenAPI + Scalar in all environments: the contract is part of the deliverable.
app.MapWalletOpenApi();

app.UseAuthentication();
app.UseAuthorization();
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
            await scope.ServiceProvider.GetRequiredService<WalletDbContext>().Database.MigrateAsync();
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
