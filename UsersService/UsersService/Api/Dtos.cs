namespace UsersService.Api;

/// <summary>Registration request. All fields are validated by the service so every rule yields VALIDATION_FAILED.</summary>
/// <param name="Email">Login email; matched case-insensitively and stored lower-cased.</param>
/// <param name="Password">At least 8 characters.</param>
/// <param name="DisplayName">1 to 100 characters (trimmed).</param>
public sealed record RegisterBody(string? Email, string? Password, string? DisplayName);

/// <param name="UserId">The new player's id (also the Keycloak user id and the token's <c>sub</c>).</param>
public sealed record RegisterResponse(Guid UserId);

/// <param name="Token">The token from the verification email link.</param>
public sealed record VerifyBody(string? Token);

/// <param name="Status">Always <c>verified</c>.</param>
public sealed record VerifyResponse(string Status);
