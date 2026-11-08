using System.Collections.Concurrent;
using System.Text.Json;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UsersService.Data;
using UsersService.Email;
using UsersService.Identity;
using UsersService.Messaging;
using UsersService.Registration;

namespace UsersService.Tests;

/// <summary>
/// <see cref="RegistrationService"/> resolved from DI exactly as the app does (scoped DbContext + scoped
/// <see cref="IPublishEndpoint"/> from the EF bus outbox), on the MassTransit in-memory test harness with outbox
/// delivery disabled: what a registration publishes stays in the outbox tables for the test to inspect.
/// One scope per call, like one HTTP request.
/// </summary>
public sealed class NoDeliveryHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private NoDeliveryHost(ServiceProvider provider) => _provider = provider;

    public ITestHarness Harness => _provider.GetRequiredService<ITestHarness>();

    public static async Task<NoDeliveryHost> StartAsync(string connectionString, IIdentityProvider identity,
        IEmailSender email, TimeProvider time, string publicBaseUrl, params IInterceptor[] interceptors)
    {
        var provider = new ServiceCollection()
            .AddLogging()
            .AddDbContext<UsersDbContext>(o => o.UseSqlServer(connectionString).AddInterceptors(interceptors))
            .AddSingleton(identity)
            .AddSingleton(email)
            .AddSingleton(time)
            .AddSingleton(Options.Create(new UsersOptions { PublicBaseUrl = publicBaseUrl }))
            .AddScoped<RegistrationService>()
            .AddMassTransitTestHarness(x => UsersMessaging.Configure(x, outboxDelivery: false))
            .BuildServiceProvider(true);
        var host = new NoDeliveryHost(provider);
        await host.Harness.Start();
        return host;
    }

    public async Task<RegisterResult> RegisterAsync(RegisterRequest request)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RegistrationService>().RegisterAsync(request, CancellationToken.None);
    }

    public async Task<VerifyResult> VerifyAsync(string token)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RegistrationService>().VerifyAsync(token, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await Harness.Stop();
        await _provider.DisposeAsync();
    }
}

/// <summary>A message as stored in the <c>OutboxMessage</c> table (body = MassTransit JSON envelope).</summary>
public sealed record StoredMessage(Guid? OutboxId, string MessageType, JsonElement Message)
{
    public bool Is<T>() => MessageType.Contains($"urn:message:Contracts:{typeof(T).Name}", StringComparison.Ordinal);
}

public static class OutboxRows
{
    /// <summary>Outbox messages whose body contains <paramref name="text"/> (e.g. a user id or email).</summary>
    public static async Task<List<StoredMessage>> ContainingAsync(UsersDbContext db, string text)
    {
        var rows = await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Body.Contains(text))
            .OrderBy(m => m.SequenceNumber)
            .Select(m => new { m.OutboxId, m.MessageType, m.Body })
            .ToListAsync();
        return rows.Select(r => new StoredMessage(r.OutboxId, r.MessageType,
            JsonDocument.Parse(r.Body).RootElement.GetProperty("message").Clone())).ToList();
    }

    /// <summary>The verify links of the <c>VerificationEmailRequested</c> messages written for <paramref name="email"/>.</summary>
    public static async Task<List<string>> VerifyUrlsForAsync(UsersDbContext db, string email) =>
        (await ContainingAsync(db, email))
        .Where(m => m.Is<Contracts.VerificationEmailRequested>()
                    && string.Equals(m.Message.GetProperty("email").GetString(), email, StringComparison.OrdinalIgnoreCase))
        .Select(m => m.Message.GetProperty("verifyUrl").GetString()!)
        .ToList();

    public static string TokenOf(string verifyUrl) =>
        System.Web.HttpUtility.ParseQueryString(new Uri(verifyUrl).Query)["token"]
        ?? throw new InvalidOperationException("No token in link.");
}

/// <summary>
/// Counts messages whose receive pipeline completed (consumed, skipped as a duplicate, or faulted after all retries),
/// by MessageId, so tests can wait deterministically.
/// </summary>
public sealed class ReceiveProbe : IReceiveObserver
{
    private readonly ConcurrentDictionary<Guid, int> _completed = new();

    public async Task WaitForAsync(Guid messageId, int times = 1, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (_completed.GetValueOrDefault(messageId) < times)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Message {messageId} was not received {times} time(s) in time.");
            await Task.Delay(25);
        }
    }

    private Task Completed(ReceiveContext context)
    {
        if (context.GetMessageId() is { } id) _completed.AddOrUpdate(id, 1, (_, n) => n + 1);
        return Task.CompletedTask;
    }

    public Task PreReceive(ReceiveContext context) => Task.CompletedTask;
    public Task PostReceive(ReceiveContext context) => Completed(context);
    public Task ReceiveFault(ReceiveContext context, Exception exception) => Completed(context);

    public Task PostConsume<T>(ConsumeContext<T> context, TimeSpan duration, string consumerType) where T : class =>
        Task.CompletedTask;

    public Task ConsumeFault<T>(ConsumeContext<T> context, TimeSpan duration, string consumerType, Exception exception)
        where T : class => Task.CompletedTask;
}
