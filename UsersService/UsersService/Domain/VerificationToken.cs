namespace UsersService.Domain;

/// <summary>
/// An email verification token. Only the SHA-256 hash of the raw token is stored, so a database leak
/// does not reveal usable links.
/// </summary>
public class VerificationToken
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Lower-case hex SHA-256 of the raw (URL-safe base64) token; unique.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}
