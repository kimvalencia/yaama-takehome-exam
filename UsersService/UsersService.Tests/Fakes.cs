using System.Collections.Concurrent;
using UsersService.Email;
using UsersService.Identity;

namespace UsersService.Tests;

/// <summary>
/// In-memory stand-in for Keycloak with the same semantics the real provider has:
/// one identity per (case-insensitive) email; creating an existing email returns the existing id.
/// </summary>
public sealed class FakeIdentityProvider : IIdentityProvider
{
    public sealed record Identity(Guid Id, string Email, string DisplayName, bool Enabled, bool EmailVerified, bool IsPlayer);

    private readonly ConcurrentDictionary<string, Identity> _byEmail = new(StringComparer.OrdinalIgnoreCase);
    private int _createCalls;

    public int CreateCalls => _createCalls;

    public IReadOnlyCollection<Identity> IdentitiesFor(string email) =>
        _byEmail.Values.Where(i => string.Equals(i.Email, email, StringComparison.OrdinalIgnoreCase)).ToList();

    public Identity? Get(Guid id) => _byEmail.Values.SingleOrDefault(i => i.Id == id);

    /// <summary>Simulates an earlier attempt that created the Keycloak user and then crashed before saving locally.</summary>
    public Guid SeedLeftover(string email)
    {
        var id = Guid.NewGuid();
        _byEmail[email] = new Identity(id, email.ToLowerInvariant(), "leftover", false, false, IsPlayer: false);
        return id;
    }

    public Task<Guid> CreateDisabledUserAsync(string email, string password, string displayName, CancellationToken ct)
    {
        Interlocked.Increment(ref _createCalls);
        var identity = _byEmail.AddOrUpdate(email,
            _ => new Identity(Guid.NewGuid(), email.ToLowerInvariant(), displayName, false, false, IsPlayer: true),
            (_, existing) => existing with { IsPlayer = true });
        return Task.FromResult(identity.Id);
    }

    public Task EnableUserAsync(Guid userId, CancellationToken ct)
    {
        var current = Get(userId) ?? throw new InvalidOperationException($"No identity {userId}.");
        _byEmail[current.Email] = current with { Enabled = true, EmailVerified = true };
        return Task.CompletedTask;
    }
}

public sealed class FakeEmailSender : IEmailSender
{
    public sealed record Sent(string Email, string VerifyUrl);

    private readonly ConcurrentQueue<Sent> _sent = new();

    public IReadOnlyList<Sent> SentTo(string email) =>
        _sent.Where(s => string.Equals(s.Email, email, StringComparison.OrdinalIgnoreCase)).ToList();

    public Task SendVerificationAsync(string email, string verifyUrl, CancellationToken ct)
    {
        _sent.Enqueue(new Sent(email, verifyUrl));
        return Task.CompletedTask;
    }

    /// <summary>The raw token from the last link sent to <paramref name="email"/>.</summary>
    public string TokenFor(string email)
    {
        var url = new Uri(SentTo(email).Last().VerifyUrl);
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        return query["token"] ?? throw new InvalidOperationException("No token in link.");
    }
}

/// <summary>A clock tests can move forward.</summary>
public sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
