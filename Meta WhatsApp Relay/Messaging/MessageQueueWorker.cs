using Microsoft.Rest;
using WhatsAppRelay.Controllers;

namespace WhatsAppRelay.Messaging;

/// <summary>
/// This runs as a background task that continuously reads messages from the queue and processes them.
/// It handles message processing failures and logs them appropriately.
/// </summary>
public sealed class MessageQueueWorker : BackgroundService
{
    private readonly IMessageQueue _messageQueue;
    private readonly IMessageProcessor _messageProcessor;
    private readonly ILogger<MessageQueueWorker> _logger;

    public MessageQueueWorker(
        IMessageQueue messageQueue,
        IMessageProcessor messageProcessor,
        ILogger<MessageQueueWorker> logger)
    {
        _messageQueue = messageQueue;
        _messageProcessor = messageProcessor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        try
        {
            await foreach (
    var message in _messageQueue.ReadAllAsync(stoppingToken))
            {
                await ProcessMessageAsync(message, stoppingToken);

                _messageQueue.MarkHandled(message);
            }
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Message queue worker stopped.");
        }
    }

    private async Task ProcessMessageAsync(
        IncomingMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(
                "Processing queued message {MessageId}.",
                message.Id);

            await _messageProcessor.ProcessAsync(
                message,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is HttpRequestException ||
            exception is HttpOperationException ||
            exception is TimeoutException ||
            (exception is OperationCanceledException &&
             !cancellationToken.IsCancellationRequested))
        {
            _logger.LogError(
                exception,
                "Message {MessageId} failed. No automatic retry is configured.",
                message.Id);
        }
    }
}