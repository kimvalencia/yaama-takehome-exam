using System.Collections.Concurrent;
using MassTransit;

namespace WalletService.Tests;

/// <summary>
/// Counts messages whose receive pipeline has completed (consumed, skipped by the inbox as a duplicate, or faulted
/// after retries), by MessageId. Lets tests wait deterministically instead of relying on harness inactivity.
/// </summary>
public sealed class ReceiveProbe : IReceiveObserver
{
    private readonly ConcurrentDictionary<Guid, int> _completed = new();

    public async Task WaitForAsync(Guid messageId, int times = 1, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (_completed.GetValueOrDefault(messageId) < times)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Message {messageId} was not received {times} time(s) in time.");
            await Task.Delay(25);
        }
    }

    private Task Completed(ReceiveContext context)
    {
        if (context.GetMessageId() is { } id) _completed.AddOrUpdate(id, 1, (_, n) => n + 1);
        return Task.CompletedTask;
    }

    public Task PreReceive(ReceiveContext context) => Task.CompletedTask;
    public Task PostReceive(ReceiveContext context) => Completed(context);
    public Task ReceiveFault(ReceiveContext context, Exception exception) => Completed(context);

    public Task PostConsume<T>(ConsumeContext<T> context, TimeSpan duration, string consumerType) where T : class =>
        Task.CompletedTask;

    public Task ConsumeFault<T>(ConsumeContext<T> context, TimeSpan duration, string consumerType, Exception exception)
        where T : class => Task.CompletedTask;
}
