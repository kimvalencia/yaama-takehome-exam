using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace UsersService.Email;

public sealed class SmtpOptions
{
    public const string Section = "Smtp";

    public string Host { get; set; } = "mailpit";
    public int Port { get; set; } = 1025;
    public string From { get; set; } = "no-reply@gaming.local";
}

/// <summary>Plain SMTP without authentication or TLS: Mailpit in compose, inspect mails at its web UI.</summary>
public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    public async Task SendVerificationAsync(string email, string verifyUrl, CancellationToken ct)
    {
        var o = options.Value;
        using var message = new MailMessage(o.From, email)
        {
            Subject = "Verify your email address",
            Body = $"""
                Welcome!

                Please verify your email address to activate your account:

                {verifyUrl}

                The link is valid for 24 hours.
                """,
        };
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
            $"""
            <p>Welcome!</p>
            <p>Please verify your email address to activate your account:</p>
            <p><a href="{WebUtility.HtmlEncode(verifyUrl)}">Verify my email</a></p>
            <p>The link is valid for 24 hours.</p>
            """, null, "text/html"));

        using var client = new SmtpClient(o.Host, o.Port) { EnableSsl = false, UseDefaultCredentials = false };
        await client.SendMailAsync(message, ct);
    }
}
