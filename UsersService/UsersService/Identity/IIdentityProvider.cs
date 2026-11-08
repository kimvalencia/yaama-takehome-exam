namespace UsersService.Identity;

/// <summary>The identity store (Keycloak) that issues player tokens.</summary>
public interface IIdentityProvider
{
    /// <summary>
    /// Creates a disabled, unverified identity with role <c>player</c> and returns its id.
    /// If an identity with this email already exists (a previous attempt crashed after creating it),
    /// returns that identity's id instead of creating a second one.
    /// </summary>
    /// <exception cref="IdentityRejectedException">The identity provider rejected the input (e.g. its password policy).</exception>
    Task<Guid> CreateDisabledUserAsync(string email, string password, string displayName, CancellationToken ct);

    /// <summary>Enables the identity and marks its email verified, so the player can log in. Idempotent.</summary>
    Task EnableUserAsync(Guid userId, CancellationToken ct);
}

/// <summary>The identity provider refused the registration data (a client error, not an outage).</summary>
public sealed class IdentityRejectedException(string message) : Exception(message);
