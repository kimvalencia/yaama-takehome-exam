namespace UsersService.Identity;

/// <summary>The identity store (Keycloak) that issues player tokens.</summary>
public interface IIdentityProvider
{
    /// <summary>
    /// Creates a disabled, unverified identity with role <c>player</c> and returns its id.
    /// If a <b>disabled and unverified</b> identity with this email already exists (a previous attempt crashed after
    /// creating it), it is reused: its password and name are reset to the new values, <c>player</c> is ensured,
    /// and its id is returned.
    /// </summary>
    /// <exception cref="IdentityRejectedException">The identity provider rejected the input (e.g. its password policy).</exception>
    /// <exception cref="IdentityAlreadyExistsException">
    /// An identity with this email exists and is enabled or verified (a real account, not a leftover). It is left untouched.
    /// </exception>
    Task<Guid> CreateDisabledUserAsync(string email, string password, string displayName, CancellationToken ct);

    /// <summary>Enables the identity and marks its email verified, so the player can log in. Idempotent.</summary>
    Task EnableUserAsync(Guid userId, CancellationToken ct);
}

/// <summary>The identity provider refused the registration data (a client error, not an outage).</summary>
public sealed class IdentityRejectedException(string message) : Exception(message);

/// <summary>The email belongs to an active (enabled or verified) identity, so it must not be adopted.</summary>
public sealed class IdentityAlreadyExistsException(string email)
    : Exception($"An active identity already exists for {email}.");
