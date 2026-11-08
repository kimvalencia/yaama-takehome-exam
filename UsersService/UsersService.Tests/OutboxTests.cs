using Contracts;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using MassTransit.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UsersService.Data;
using UsersService.Domain;
using UsersService.Email;
using UsersService.Messaging;
using UsersService.Registration;

namespace UsersService.Tests;

/// <summary>
/// Registration publishes through the EF bus outbox. These tests run with outbox delivery disabled (see
/// <see cref="NoDeliveryHost"/>) on a database no delivery service touches, so they can assert on the stored outbox
/// rows without racing a delivery that would send and delete them.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class OutboxTests(SqlServerFixture fx) : IAsyncLifetime
{
    private const string BaseUrl = "http://users.test";

    private readonly FakeIdentityProvider _identity = new();
    private readonly FakeEmailSender _email = new();
    private readonly MutableTimeProvider _time = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly InsertSameEmailBeforeSave _race = new();
    private NoDeliveryHost _host = null!;

    public async Task InitializeAsync() =>
        _host = await NoDeliveryHost.StartAsync(fx.NoDeliveryConnectionString, _identity, _email, _time, BaseUrl, _race);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static string NewEmail() => $"outbox-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task Register_writes_PlayerRegistered_and_VerificationEmailRequested_in_the_same_transaction_as_the_user()
    {
        var email = NewEmail();

        var result = await _host.RegisterAsync(new RegisterRequest(email, "Secret123!", "Player"));

        var userId = Assert.NotNull(result.UserId);
        await using var db = fx.CreateNoDeliveryContext();
        var messages = await OutboxRows.ContainingAsync(db, userId.ToString());
        Assert.Equal(2, messages.Count);

        var registered = Assert.Single(messages, m => m.Is<PlayerRegistered>());
        Assert.Equal(userId, registered.Message.GetProperty("playerId").GetGuid());
        Assert.Equal(_time.GetUtcNow(), registered.Message.GetProperty("registeredAt").GetDateTimeOffset());

        var emailRequested = Assert.Single(messages, m => m.Is<VerificationEmailRequested>());
        Assert.Equal(userId, emailRequested.Message.GetProperty("userId").GetGuid());
        Assert.Equal(email, emailRequested.Message.GetProperty("email").GetString());
        var url = emailRequested.Message.GetProperty("verifyUrl").GetString()!;
        Assert.StartsWith($"{BaseUrl}/v1/users/verify?token=", url);
        // The link's raw token is the one whose hash was saved with the user.
        Assert.True(await db.VerificationTokens.AnyAsync(t =>
            t.UserId == userId && t.TokenHash == RegistrationService.Hash(OutboxRows.TokenOf(url))));

        // One OutboxId = one SaveChanges = one DB transaction, shared with the User and VerificationToken rows.
        var outboxId = Assert.Single(messages.Select(m => m.OutboxId).Distinct());
        Assert.NotNull(outboxId);
        Assert.True(await db.Set<OutboxState>().AnyAsync(s => s.OutboxId == outboxId));
        Assert.True(await db.Users.AnyAsync(u => u.Id == userId));

        // Nothing is on the bus yet: the messages leave only through outbox delivery, after the commit.
        Assert.False(_host.Harness.Published.Select<PlayerRegistered>().Any());
        Assert.Empty(_email.SentTo(email));
    }

    [Fact]
    public async Task Failed_registration_transaction_leaves_no_outbox_rows_and_no_messages()
    {
        var email = NewEmail();
        await using var db = fx.CreateNoDeliveryContext();
        var outboxMessagesBefore = await db.Set<OutboxMessage>().CountAsync();
        var outboxStatesBefore = await db.Set<OutboxState>().CountAsync();

        // A concurrent registration of the same email commits between our duplicate check and our SaveChanges,
        // so our transaction fails on the unique Email index.
        _race.Arm(fx.NoDeliveryConnectionString, email);
        var result = await _host.RegisterAsync(new RegisterRequest(email, "Secret123!", "Player"));

        Assert.True(_race.Fired);
        Assert.Equal(ErrorCodes.EmailAlreadyRegistered, result.ErrorCode);
        var loserId = Assert.Single(_identity.IdentitiesFor(email)).Id;
        Assert.Empty(await OutboxRows.ContainingAsync(db, email));
        Assert.Empty(await OutboxRows.ContainingAsync(db, loserId.ToString()));
        Assert.Equal(outboxMessagesBefore, await db.Set<OutboxMessage>().CountAsync());
        Assert.Equal(outboxStatesBefore, await db.Set<OutboxState>().CountAsync());
        Assert.False(await db.VerificationTokens.AnyAsync(t => t.UserId == loserId));
        Assert.False(_host.Harness.Published.Select<PlayerRegistered>().Any());
        Assert.False(_host.Harness.Published.Select<VerificationEmailRequested>().Any());
    }

    /// <summary>Inserts a user with the same email on another connection just before the next registration's save.</summary>
    private sealed class InsertSameEmailBeforeSave : SaveChangesInterceptor
    {
        private string? _connectionString;
        private string? _email;

        public bool Fired { get; private set; }

        public void Arm(string connectionString, string email) => (_connectionString, _email) = (connectionString, email);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_email is { } email && eventData.Context!.ChangeTracker.Entries<User>().Any())
            {
                _email = null;
                Fired = true;
                await using var other = new UsersDbContext(new DbContextOptionsBuilder<UsersDbContext>()
                    .UseSqlServer(_connectionString).Options);
                other.Users.Add(new User
                {
                    Id = Guid.NewGuid(), Email = email, DisplayName = "Winner", CreatedAt = DateTimeOffset.UtcNow,
                });
                await other.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }
}

/// <summary>The verification email consumer on the test harness (real definition: retry + inbox).</summary>
[Collection(SqlServerCollection.Name)]
public sealed class SendVerificationEmailConsumerTests(SqlServerFixture fx) : IAsyncLifetime
{
    private readonly FakeEmailSender _email = new();
    private readonly ReceiveProbe _probe = new();
    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _provider = new ServiceCollection()
            .AddLogging()
            .AddDbContext<UsersDbContext>(o => o.UseSqlServer(fx.NoDeliveryConnectionString))
            .AddSingleton<IEmailSender>(_email)
            .Configure<MessagingOptions>(TestMessaging.FastRetry)
            .AddMassTransitTestHarness(x =>
            {
                x.SetTestTimeouts(testTimeout: TimeSpan.FromSeconds(60));
                UsersMessaging.Configure(x, outboxDelivery: false);
            })
            .BuildServiceProvider(true);
        _harness = _provider.GetRequiredService<ITestHarness>();
        await _harness.Start();
        _harness.Bus.ConnectReceiveObserver(_probe);
    }

    public async Task DisposeAsync()
    {
        await _harness.Stop();
        await _provider.DisposeAsync();
    }

    private async Task<Guid> PublishAsync(VerificationEmailRequested message, Guid? messageId = null)
    {
        var id = messageId ?? NewId.NextGuid();
        await _harness.Bus.Publish(message, ctx => ctx.MessageId = id);
        return id;
    }

    [Fact]
    public async Task Email_consumer_sends_the_verify_link_from_the_message()
    {
        var email = $"mail-{Guid.NewGuid():N}@example.com";
        const string url = "http://users.test/v1/users/verify?token=abc";

        await _probe.WaitForAsync(await PublishAsync(new VerificationEmailRequested(Guid.NewGuid(), email, url)));

        var sent = Assert.Single(_email.SentTo(email));
        Assert.Equal(url, sent.VerifyUrl);
    }

    [Fact]
    public async Task Email_consumer_retries_after_sender_failures_then_succeeds()
    {
        var email = $"retry-{Guid.NewGuid():N}@example.com";
        const string url = "http://users.test/v1/users/verify?token=retry";
        // SMTP down for 6 attempts: more than the old 5-retry policy survived, fewer than the 10 retries configured.
        const int failures = 6;
        Assert.True(failures < new MessagingOptions().Retry.Limit);
        _email.FailNext(failures);

        var messageId = await PublishAsync(new VerificationEmailRequested(Guid.NewGuid(), email, url));
        var sent = await _email.WaitForAsync(email, TimeSpan.FromSeconds(30));
        await _probe.WaitForAsync(messageId);

        Assert.Equal(url, sent.VerifyUrl);
        Assert.Single(_email.SentTo(email));
        Assert.Equal(failures + 1, _email.Attempts);
        // Not faulted, so not moved to users-verification-email_error.
        Assert.False(_harness.Published.Select<Fault<VerificationEmailRequested>>().Any());
    }

    [Fact]
    public void Email_endpoint_defaults_limit_concurrency_and_retry_for_long_outages()
    {
        var definition = new SendVerificationEmailConsumerDefinition(Options.Create(new MessagingOptions()));
        var defaults = new MessagingOptions();

        Assert.Equal(SendVerificationEmailConsumerDefinition.QueueName, ((IConsumerDefinition)definition).GetEndpointName(DefaultEndpointNameFormatter.Instance));
        Assert.Equal(8, definition.ConcurrentMessageLimit);
        Assert.Equal(10, defaults.Retry.Limit);
        Assert.Equal(TimeSpan.FromMinutes(5), defaults.Retry.MaxInterval);
        Assert.Equal(10, defaults.KillSwitch.ActivationThreshold);
        Assert.Equal(0.15, defaults.KillSwitch.TripThreshold);
    }

    [Fact]
    public async Task Redelivered_email_request_with_same_message_id_sends_one_email()
    {
        var email = $"dup-{Guid.NewGuid():N}@example.com";
        var message = new VerificationEmailRequested(Guid.NewGuid(), email, "http://users.test/v1/users/verify?token=dup");
        var messageId = NewId.NextGuid();

        await _probe.WaitForAsync(await PublishAsync(message, messageId));
        await _probe.WaitForAsync(await PublishAsync(message, messageId), times: 2);

        Assert.Single(_email.SentTo(email));
    }
}
