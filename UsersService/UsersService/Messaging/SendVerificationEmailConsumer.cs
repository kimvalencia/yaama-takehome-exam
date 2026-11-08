using Contracts;
using MassTransit;
using UsersService.Data;
using UsersService.Email;

namespace UsersService.Messaging;

/// <summary>
/// Sends the verification email. Runs from the outbox after the registration committed, so an SMTP outage only delays
/// the email: the message is retried with backoff (and lands in the <c>_error</c> queue if every retry fails).
/// </summary>
public sealed class SendVerificationEmailConsumer(IEmailSender email, ILogger<SendVerificationEmailConsumer> logger)
    : IConsumer<VerificationEmailRequested>
{
    public async Task Consume(ConsumeContext<VerificationEmailRequested> context)
    {
        var m = context.Message;
        await email.SendVerificationAsync(m.Email, m.VerifyUrl, context.CancellationToken);
        logger.LogInformation("Verification email sent for user {UserId}.", m.UserId);
    }
}

public sealed class SendVerificationEmailConsumerDefinition : ConsumerDefinition<SendVerificationEmailConsumer>
{
    public const string QueueName = "users-verification-email";

    public SendVerificationEmailConsumerDefinition() => EndpointName = QueueName;

    protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<SendVerificationEmailConsumer> consumerConfigurator, IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => MessagingRetry.Configure(r));
        // Inbox: a redelivered message (same MessageId) does not send a second email.
        endpointConfigurator.UseEntityFrameworkOutbox<UsersDbContext>(context);
    }
}
