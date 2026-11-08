using System.Buffers.Text;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UsersService.Data;
using UsersService.Domain;
using UsersService.Email;
using UsersService.Identity;

namespace UsersService.Registration;

public sealed record RegisterRequest(string Email, string Password, string DisplayName);

/// <summary>Either <see cref="UserId"/> (success) or <see cref="ErrorCode"/> (+ <see cref="Detail"/>) is set.</summary>
public sealed record RegisterResult(Guid? UserId, string? ErrorCode, string? Detail = null)
{
    public static RegisterResult Ok(Guid userId) => new(userId, null);
    public static RegisterResult Fail(string code, string detail) => new(null, code, detail);
}

public sealed record VerifyResult(bool Success, string? ErrorCode = null)
{
    public static readonly VerifyResult Ok = new(true);
    public static readonly VerifyResult Invalid = new(false, ErrorCodes.InvalidToken);
}

public sealed class UsersOptions
{
    public const string Section = "Users";

    /// <summary>Base URL players reach this API on; used to build the verification link.</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5001";
}

/// <summary>Player registration and email verification.</summary>
public sealed class RegistrationService(
    UsersDbContext db,
    IIdentityProvider identity,
    IEmailSender email,
    TimeProvider time,
    IOptions<UsersOptions> options,
    ILogger<RegistrationService> logger)
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;
    private const int MaxTokenLength = 128;

    /// <summary>
    /// Registers a player: rejects a known email (case-insensitive), creates (or reuses) the disabled Keycloak identity,
    /// then saves the user and the hashed verification token in one DB transaction, then sends the verification email.
    /// </summary>
    public async Task<RegisterResult> RegisterAsync(RegisterRequest r, CancellationToken ct)
    {
        var emailAddress = NormalizeEmail(r.Email);
        var displayName = r.DisplayName?.Trim() ?? string.Empty;
        if (Validate(emailAddress, r.Password, displayName) is { } invalid)
            return RegisterResult.Fail(ErrorCodes.ValidationFailed, invalid);

        if (await db.Users.AnyAsync(u => u.Email == emailAddress, ct))
            return EmailTaken();

        Guid userId;
        try
        {
            // Keycloak first: if we crash after this, the next attempt for the same email reuses this identity.
            userId = await identity.CreateDisabledUserAsync(emailAddress, r.Password, displayName, ct);
        }
        catch (IdentityRejectedException ex)
        {
            return RegisterResult.Fail(ErrorCodes.ValidationFailed, ex.Message);
        }
        catch (IdentityAlreadyExistsException)
        {
            // An account created outside this service (or already active): never adopt it.
            return EmailTaken();
        }

        var now = time.GetUtcNow();
        var rawToken = NewToken();
        var user = new User { Id = userId, Email = emailAddress, DisplayName = displayName, CreatedAt = now };
        db.Users.Add(user);
        db.VerificationTokens.Add(new VerificationToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = Hash(rawToken),
            ExpiresAt = now.Add(VerificationToken.Lifetime),
        });

        try
        {
            // One SaveChanges = one DB transaction for the user and its token (Task 5 adds the outbox message here).
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent registration for the same email won the race (unique Email index or same Keycloak id).
            return EmailTaken();
        }

        await SendVerificationEmailAsync(user.Email, rawToken, ct);
        return RegisterResult.Ok(userId);
    }

    /// <summary>
    /// Verifies an email with a raw token: enables the Keycloak identity and marks the user verified.
    /// Idempotent: a used token for an already verified user succeeds again. Unknown, expired, or used tokens of a
    /// user that is not verified are <see cref="ErrorCodes.InvalidToken"/>.
    /// </summary>
    public async Task<VerifyResult> VerifyAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
            return VerifyResult.Invalid;

        var hash = Hash(token);
        var match = await db.VerificationTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Join(db.Users, t => t.UserId, u => u.Id, (t, u) => new { Token = t, u.EmailVerifiedAt })
            .SingleOrDefaultAsync(ct);
        if (match is null)
            return VerifyResult.Invalid;

        if (match.Token.UsedAt is not null)
            return match.EmailVerifiedAt is not null ? VerifyResult.Ok : VerifyResult.Invalid;

        var now = time.GetUtcNow();
        if (now >= match.Token.ExpiresAt)
            return VerifyResult.Invalid;

        // Enable in Keycloak first; if that fails nothing is recorded and the same link can be retried.
        await identity.EnableUserAsync(match.Token.UserId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Conditional updates keep the first verification's timestamps when two requests race.
        await db.VerificationTokens.Where(t => t.Id == match.Token.Id && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), ct);
        await db.Users.Where(u => u.Id == match.Token.UserId && u.EmailVerifiedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailVerifiedAt, now), ct);
        await tx.CommitAsync(ct);
        return VerifyResult.Ok;
    }

    /// <summary>
    /// Sends the verification email right after the commit. Task 5 replaces this with an outbox message so the
    /// email survives a crash; until then a failed send is logged and the registration still stands.
    /// </summary>
    private async Task SendVerificationEmailAsync(string to, string rawToken, CancellationToken ct)
    {
        var url = VerifyUrl(options.Value.PublicBaseUrl, rawToken);
        try
        {
            await email.SendVerificationAsync(to, url, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Sending the verification email to {Email} failed.", to);
        }
    }

    public static string VerifyUrl(string publicBaseUrl, string rawToken) =>
        $"{publicBaseUrl.TrimEnd('/')}/v1/users/verify?token={Uri.EscapeDataString(rawToken)}";

    /// <summary>32 random bytes, URL-safe base64 without padding (43 chars).</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Lower-case hex SHA-256 of the raw token; this is all the database ever sees.</summary>
    public static string Hash(string rawToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    public static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    private static string? Validate(string email, string? password, string displayName)
    {
        if (email.Length == 0 || email.Length > User.MaxEmailLength
            || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || !parsed.Host.Contains('.'))
            return "`email` must be a valid email address.";
        if (password is null || password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
            return $"`password` must be {MinPasswordLength} to {MaxPasswordLength} characters.";
        if (displayName.Length is 0 or > User.MaxDisplayNameLength)
            return $"`displayName` must be 1 to {User.MaxDisplayNameLength} characters.";
        return null;
    }

    private static RegisterResult EmailTaken() =>
        RegisterResult.Fail(ErrorCodes.EmailAlreadyRegistered, "An account with this email address already exists.");

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };
}
