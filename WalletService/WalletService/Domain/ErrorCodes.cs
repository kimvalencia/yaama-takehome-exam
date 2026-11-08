namespace WalletService.Domain;

/// <summary>Stable machine-readable error codes returned in problem details.</summary>
public static class ErrorCodes
{
    public const string InsufficientFunds = "INSUFFICIENT_FUNDS";
    public const string UnknownPlayer = "UNKNOWN_PLAYER";
    public const string UnknownBet = "UNKNOWN_BET";
    public const string BetAlreadySettled = "BET_ALREADY_SETTLED";
    public const string DuplicateReferenceMismatch = "DUPLICATE_REFERENCE_MISMATCH";
    public const string EmailNotVerified = "EMAIL_NOT_VERIFIED";
    public const string ValidationFailed = "VALIDATION_FAILED";
}
