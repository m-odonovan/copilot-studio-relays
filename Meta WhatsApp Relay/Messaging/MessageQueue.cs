using System.Threading.Channels;
using WhatsAppRelay.Controllers;

namespace WhatsAppRelay.Messaging;

public interface IMessageQueue
{
    ValueTask EnqueueAsync(
        IncomingMessage message,
        CancellationToken cancellationToken);

    IAsyncEnumerable<IncomingMessage> ReadAllAsync(
        CancellationToken cancellationToken);
}

public sealed class InMemoryMessageQueue : IMessageQueue
{
    private readonly Channel<IncomingMessage> _channel;

    public InMemoryMessageQueue()
    {
        var options = new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };

        _channel = Channel.CreateUnbounded<IncomingMessage>(options);
    }

    public ValueTask EnqueueAsync(
        IncomingMessage message,
        CancellationToken cancellationToken)
    {
        return _channel.Writer.WriteAsync(message, cancellationToken);
    }

    public IAsyncEnumerable<IncomingMessage> ReadAllAsync(
        CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }
}