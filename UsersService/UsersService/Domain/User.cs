namespace UsersService.Domain;

/// <summary>A registered player. <see cref="Id"/> is the Keycloak user id, so tokens' <c>sub</c> maps straight to it.</summary>
public class User
{
    public const int MaxEmailLength = 254;
    public const int MaxDisplayNameLength = 100;

    public Guid Id { get; set; }

    /// <summary>Always stored lower-cased and trimmed; unique.</summary>
    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset? EmailVerifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
