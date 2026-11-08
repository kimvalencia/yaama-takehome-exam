using Contracts;
using MassTransit;
using Microsoft.Extensions.Options;
using UsersService.Data;
using UsersService.Email;

namespace UsersService.Messaging;

/// <summary>
/// Sends the verification email. Runs from the outbox after the registration committed, so an SMTP outage only delays
/// the email: the message is retried with backoff for about 25 minutes, and the kill switch pauses the endpoint
/// (messages stay queued) while sends keep failing.
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

    private readonly MessagingOptions _options;

    public SendVerificationEmailConsumerDefinition(IOptions<MessagingOptions> options)
    {
        _options = options.Value;
        EndpointName = QueueName;
        ConcurrentMessageLimit = _options.ConcurrentMessageLimit;
    }

    protected override void ConfigureConsumer(IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<SendVerificationEmailConsumer> consumerConfigurator, IRegistrationContext context)
    {
        // An SMTP outage: long backoff per message, and the kill switch pauses the endpoint instead of faulting the rest.
        MessagingResilience.Apply(endpointConfigurator, _options);
        // Inbox: a redelivered message (same MessageId) does not send a second email.
        endpointConfigurator.UseEntityFrameworkOutbox<UsersDbContext>(context);
    }
}
