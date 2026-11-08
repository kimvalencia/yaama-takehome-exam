namespace Contracts;

/// <summary>
/// Written to the outbox with the new user and consumed inside the Users service, so a failed SMTP send is retried
/// instead of lost. <see cref="VerifyUrl"/> carries the raw token; only its hash is stored in the Users table.
/// </summary>
public sealed record VerificationEmailRequested(Guid UserId, string Email, string VerifyUrl);
