namespace Contracts;

/// <summary>
/// Published by the Users service (through its outbox) when a player registers; the Wallet service creates the
/// player's wallets. Deliberately duplicated in both services, with the same namespace and shape, so the MassTransit
/// message type (<c>urn:message:Contracts:PlayerRegistered</c>) matches while each service builds on its own.
/// </summary>
public sealed record PlayerRegistered(Guid PlayerId, DateTimeOffset RegisteredAt);
