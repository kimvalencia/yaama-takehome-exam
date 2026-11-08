using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UsersService.Domain;
using UsersService.Registration;

namespace UsersService.Tests;

[Collection(SqlServerCollection.Name)]
public class RegistrationServiceTests(SqlServerFixture fx)
{
    private const string BaseUrl = "http://users.test";

    private readonly FakeIdentityProvider _identity = new();
    private readonly FakeEmailSender _email = new();
    private readonly MutableTimeProvider _time = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));

    private static string NewEmail() => $"player-{Guid.NewGuid():N}@example.com";

    private async Task<RegisterResult> Register(string email, string password = "Secret123!", string displayName = "Player One")
    {
        await using var db = fx.CreateContext();
        return await NewService(db).RegisterAsync(new RegisterRequest(email, password, displayName), CancellationToken.None);
    }

    private async Task<VerifyResult> Verify(string token)
    {
        await using var db = fx.CreateContext();
        return await NewService(db).VerifyAsync(token, CancellationToken.None);
    }

    private RegistrationService NewService(Data.UsersDbContext db) =>
        new(db, _identity, _email, _time, Options.Create(new UsersOptions { PublicBaseUrl = BaseUrl }),
            NullLogger<RegistrationService>.Instance);

    private async Task<User?> UserById(Guid id)
    {
        await using var db = fx.CreateContext();
        return await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id);
    }

    [Fact]
    public async Task Register_creates_user_and_disabled_identity()
    {
        var email = NewEmail();

        var result = await Register(email, displayName: "Player One");

        Assert.Null(result.ErrorCode);
        var userId = Assert.NotNull(result.UserId);

        var identity = Assert.Single(_identity.IdentitiesFor(email));
        Assert.Equal(userId, identity.Id);
        Assert.False(identity.Enabled);
        Assert.False(identity.EmailVerified);
        Assert.True(identity.IsPlayer);

        var user = Assert.IsType<User>(await UserById(userId));
        Assert.Equal(email, user.Email);
        Assert.Equal("Player One", user.DisplayName);
        Assert.Null(user.EmailVerifiedAt);
        Assert.Equal(_time.GetUtcNow(), user.CreatedAt);

        // Only the hash is stored; the raw token travels in the emailed link.
        var sent = Assert.Single(_email.SentTo(email));
        Assert.StartsWith($"{BaseUrl}/v1/users/verify?token=", sent.VerifyUrl);
        var rawToken = _email.TokenFor(email);
        await using var db = fx.CreateContext();
        var token = await db.VerificationTokens.AsNoTracking().SingleAsync(t => t.UserId == userId);
        Assert.NotEqual(rawToken, token.TokenHash);
        Assert.Equal(64, token.TokenHash.Length);
        Assert.Equal(_time.GetUtcNow().AddHours(24), token.ExpiresAt);
        Assert.Null(token.UsedAt);
    }

    [Fact]
    public async Task Register_same_email_twice_returns_conflict_and_creates_one_identity()
    {
        var email = NewEmail();
        var first = await Register(email);

        var second = await Register(email);

        Assert.NotNull(first.UserId);
        Assert.Equal(ErrorCodes.EmailAlreadyRegistered, second.ErrorCode);
        Assert.Null(second.UserId);
        Assert.Single(_identity.IdentitiesFor(email));
        Assert.Equal(1, _identity.CreateCalls); // the local check rejects before touching Keycloak
        Assert.Single(_email.SentTo(email));
    }

    [Fact]
    public async Task Register_email_matching_is_case_insensitive()
    {
        var email = NewEmail();
        var first = await Register(email);

        var second = await Register("  " + email.ToUpperInvariant() + " ");

        Assert.NotNull(first.UserId);
        Assert.Equal(ErrorCodes.EmailAlreadyRegistered, second.ErrorCode);
        Assert.Single(_identity.IdentitiesFor(email));
        Assert.Equal(1, _identity.CreateCalls);

        var user = Assert.IsType<User>(await UserById(first.UserId!.Value));
        Assert.Equal(email, user.Email);
    }

    [Fact]
    public async Task Register_stores_mixed_case_email_lower_cased()
    {
        var email = NewEmail();

        var result = await Register(email.Replace("player", "PlAyEr"));

        var user = Assert.IsType<User>(await UserById(result.UserId!.Value));
        Assert.Equal(email, user.Email);
    }

    [Fact]
    public async Task Register_reuses_identity_when_previous_attempt_left_one_behind()
    {
        var email = NewEmail();
        var leftoverId = _identity.SeedLeftover(email);

        var result = await Register(email);

        Assert.Equal(leftoverId, result.UserId);
        var identity = Assert.Single(_identity.IdentitiesFor(email));
        Assert.True(identity.IsPlayer);
        Assert.NotNull(await UserById(leftoverId));
        Assert.Single(_email.SentTo(email));
    }

    [Fact]
    public async Task Register_concurrent_duplicates_create_one_user_and_conflict_the_rest()
    {
        var email = NewEmail();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Register(email)));

        Assert.Single(results, r => r.UserId is not null);
        Assert.All(results.Where(r => r.UserId is null), r => Assert.Equal(ErrorCodes.EmailAlreadyRegistered, r.ErrorCode));
        Assert.Single(_identity.IdentitiesFor(email));
        await using var db = fx.CreateContext();
        Assert.Equal(1, await db.Users.CountAsync(u => u.Email == email));
    }

    [Theory]
    [InlineData("not-an-email", "Secret123!", "Player")]
    [InlineData("", "Secret123!", "Player")]
    [InlineData("a@b.c", "short", "Player")]
    [InlineData("a@b.c", "Secret123!", "")]
    [InlineData("a@b.c", "Secret123!", "   ")]
    public async Task Register_rejects_invalid_input_without_creating_an_identity(string email, string password, string displayName)
    {
        var result = await Register(email, password, displayName);

        Assert.Equal(ErrorCodes.ValidationFailed, result.ErrorCode);
        Assert.NotNull(result.Detail);
        Assert.Equal(0, _identity.CreateCalls);
    }

    [Fact]
    public async Task Register_rejects_display_name_over_100_chars()
    {
        var result = await Register(NewEmail(), displayName: new string('x', 101));

        Assert.Equal(ErrorCodes.ValidationFailed, result.ErrorCode);
        Assert.Equal(0, _identity.CreateCalls);
    }

    [Fact]
    public async Task Verify_enables_identity_and_marks_user_verified()
    {
        var email = NewEmail();
        var userId = (await Register(email)).UserId!.Value;
        _time.Advance(TimeSpan.FromHours(1));

        var result = await Verify(_email.TokenFor(email));

        Assert.True(result.Success);
        var identity = Assert.IsType<FakeIdentityProvider.Identity>(_identity.Get(userId));
        Assert.True(identity.Enabled);
        Assert.True(identity.EmailVerified);
        var user = Assert.IsType<User>(await UserById(userId));
        Assert.Equal(_time.GetUtcNow(), user.EmailVerifiedAt);
        await using var db = fx.CreateContext();
        var token = await db.VerificationTokens.AsNoTracking().SingleAsync(t => t.UserId == userId);
        Assert.Equal(_time.GetUtcNow(), token.UsedAt);
    }

    [Fact]
    public async Task Verify_twice_still_succeeds()
    {
        var email = NewEmail();
        var userId = (await Register(email)).UserId!.Value;
        var token = _email.TokenFor(email);
        Assert.True((await Verify(token)).Success);
        var firstVerifiedAt = (await UserById(userId))!.EmailVerifiedAt;

        // Even after the token would have expired, re-using it for an already verified account is a success.
        _time.Advance(TimeSpan.FromDays(3));
        var again = await Verify(token);

        Assert.True(again.Success);
        Assert.Equal(firstVerifiedAt, (await UserById(userId))!.EmailVerifiedAt);
    }

    [Fact]
    public async Task Verify_expired_or_unknown_or_used_token_fails()
    {
        // Expired: the token is valid for 24 h, so at exactly 24 h it no longer works.
        var expiredEmail = NewEmail();
        var expiredUser = (await Register(expiredEmail)).UserId!.Value;
        _time.Advance(TimeSpan.FromHours(24));
        var expired = await Verify(_email.TokenFor(expiredEmail));
        Assert.False(expired.Success);
        Assert.Equal(ErrorCodes.InvalidToken, expired.ErrorCode);
        Assert.False(_identity.Get(expiredUser)!.Enabled);
        Assert.Null((await UserById(expiredUser))!.EmailVerifiedAt);

        // Unknown, empty and garbage tokens.
        foreach (var unknown in new[] { "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "", "   ", "not a token at all %%" })
        {
            var r = await Verify(unknown);
            Assert.False(r.Success);
            Assert.Equal(ErrorCodes.InvalidToken, r.ErrorCode);
        }

        // Used token whose user is (somehow) not verified: invalid, not a silent success.
        var usedEmail = NewEmail();
        var usedUser = (await Register(usedEmail)).UserId!.Value;
        var usedToken = _email.TokenFor(usedEmail);
        Assert.True((await Verify(usedToken)).Success);
        await using (var db = fx.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == usedUser);
            user.EmailVerifiedAt = null;
            await db.SaveChangesAsync();
        }

        var used = await Verify(usedToken);
        Assert.False(used.Success);
        Assert.Equal(ErrorCodes.InvalidToken, used.ErrorCode);
    }
}
