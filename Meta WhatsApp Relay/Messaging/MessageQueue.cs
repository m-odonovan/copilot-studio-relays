using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;
using WhatsAppRelay.Controllers;

namespace WhatsAppRelay.Messaging;

public interface IMessageQueue
{
    ValueTask<bool> EnqueueAsync(
        IncomingMessage message,
        CancellationToken cancellationToken);

    IAsyncEnumerable<IncomingMessage> ReadAllAsync(
        CancellationToken cancellationToken);

    void MarkHandled(IncomingMessage message);
}

public sealed class InMemoryMessageQueue : IMessageQueue
{
    private static readonly TimeSpan DeduplicationWindow =
        TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly IMemoryCache _seenIds;
    private readonly Channel<IncomingMessage> _channel;

    public InMemoryMessageQueue(IMemoryCache seenIds)
    {
        _seenIds = seenIds;

        var options = new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };

        _channel = Channel.CreateUnbounded<IncomingMessage>(options);
    }

    public ValueTask<bool> EnqueueAsync(
        IncomingMessage message,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = (typeof(InMemoryMessageQueue), message.Id);

            if (_seenIds.TryGetValue(key, out _))
            {
                return new ValueTask<bool>(false);
            }

            // Keep the reservation until the worker finishes.
            // initially this message id was added without a time expiration, and then when its process by markhandled and expiration was added
            // this ensures the message doesnt leave the queueu if the queue is taking 1 hour to empty, but it adds
            // chance that ids stay in list forever if they not marked as handled.
            _seenIds.Set(
                key,
                true,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = DeduplicationWindow,
        Priority = CacheItemPriority.NeverRemove
                });

            // This channel is unbounded, so enqueueing does not need to wait.
            if (!_channel.Writer.TryWrite(message))
            {
                _seenIds.Remove(key);
                throw new ChannelClosedException();
            }

            return new ValueTask<bool>(true);
        }
    }


/// <summary>
/// adds / updates the message id into a list with an expiration 1 hour from now
/// This is so that if this message is seen again later, it doesnt get processed
/// </summary>
/// <param name="message"></param>
    public void MarkHandled(IncomingMessage message)
    {
        lock (_gate)
        {
            var key = (typeof(InMemoryMessageQueue), message.Id);

            _seenIds.Set(
                key,
                true,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow =
                        DeduplicationWindow,
                    Priority = CacheItemPriority.NeverRemove
                });
        }
    }

    public IAsyncEnumerable<IncomingMessage> ReadAllAsync(
        CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAllAsync(cancellationToken);
    }
}