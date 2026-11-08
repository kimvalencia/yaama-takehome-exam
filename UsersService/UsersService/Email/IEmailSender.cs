namespace UsersService.Email;

public interface IEmailSender
{
    /// <summary>Sends the "verify your email" message containing <paramref name="verifyUrl"/>.</summary>
    Task SendVerificationAsync(string email, string verifyUrl, CancellationToken ct);
}
