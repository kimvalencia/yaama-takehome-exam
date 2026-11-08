namespace UsersService.Domain;

/// <summary>Stable machine-readable error codes returned in problem details.</summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string EmailAlreadyRegistered = "EMAIL_ALREADY_REGISTERED";
    public const string InvalidToken = "INVALID_TOKEN";
}
