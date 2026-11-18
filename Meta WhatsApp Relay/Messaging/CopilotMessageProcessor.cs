using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Bot.Connector.DirectLine;
using WhatsAppRelay.BotConnectorApp;
using WhatsAppRelay.Controllers;

namespace WhatsAppRelay.Messaging;

public interface IMessageProcessor
{
    Task ProcessAsync(
        IncomingMessage message,
        CancellationToken cancellationToken);
}

public sealed class CopilotMessageProcessor : IMessageProcessor
{
    private const int WaitForBotResponseMaxMilliseconds = 30 * 1000;
    private const int PollForBotResponseIntervalMilliseconds = 1500;
    private const int WhatsAppReplyChunkLength = 1600;

    private static readonly HttpClient s_metaHttpClient = new HttpClient();

    private readonly BotService _botService;
    private readonly ILogger<CopilotMessageProcessor> _logger;

    private readonly string _metaGraphVersion;
    private readonly string _metaAccessToken;
    private readonly string _metaPhoneNumberId;

    private readonly ConcurrentDictionary<string, SenderTokenState>
        _senderTokens =
            new ConcurrentDictionary<string, SenderTokenState>(
                StringComparer.Ordinal);

    private sealed class SenderTokenState
    {
        public SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);

        public string? Token { get; set; }

        public DateTimeOffset ExpiresAtUtc { get; set; }
    }

    public CopilotMessageProcessor(
        IConfiguration configuration,
        ILogger<CopilotMessageProcessor> logger)
    {
        _logger = logger;

        _metaGraphVersion =
            configuration.GetValue<string>("Meta:GraphVersion")
            ?? string.Empty;

        _metaAccessToken =
            configuration.GetValue<string>("Meta:AccessToken")
            ?? string.Empty;

        _metaPhoneNumberId =
            configuration.GetValue<string>("Meta:PhoneNumberId")
            ?? string.Empty;

        if (string.IsNullOrEmpty(_metaGraphVersion) ||
            string.IsNullOrEmpty(_metaAccessToken) ||
            string.IsNullOrEmpty(_metaPhoneNumberId))
        {
            throw new InvalidOperationException(
                "Required Meta settings are missing: " +
                "GraphVersion, AccessToken, or PhoneNumberId.");
        }

        var botId =
            configuration.GetValue<string>("BotId")
            ?? string.Empty;

        var tenantId =
            configuration.GetValue<string>("BotTenantId")
            ?? string.Empty;

        var tokenEndpoint =
            configuration.GetValue<string>("BotTokenEndpoint")
            ?? string.Empty;

        var botName =
            configuration.GetValue<string>("BotName")
            ?? string.Empty;

        var botLocation =
            configuration.GetValue<string>("BotLocation")
            ?? string.Empty;

        if (string.IsNullOrEmpty(botId) ||
            string.IsNullOrEmpty(tenantId) ||
            string.IsNullOrEmpty(tokenEndpoint) ||
            string.IsNullOrEmpty(botName) ||
            string.IsNullOrEmpty(botLocation))
        {
            throw new InvalidOperationException(
                "Required Copilot Studio settings are missing: " +
                "BotId, BotTenantId, BotTokenEndpoint, " +
                "BotName, or BotLocation.");
        }

        _botService = new BotService
        {
            BotId = botId,
            TenantId = tenantId,
            TokenEndPoint = tokenEndpoint,
            BotName = botName,
            BotLocation = botLocation
        };
    }

    public async Task ProcessAsync(
        IncomingMessage message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(message.Text))
        {
            _logger.LogWarning(
                "Skipping message {MessageId}: media-only and " +
                "unsupported messages do not currently have text to process.",
                message.Id);

            return;
        }

        var token = await GetSenderTokenAsync(
            message.Key,
            cancellationToken);

        using var directLineClient = new DirectLineClient(token);

        directLineClient.BaseUri =
            new Uri(_botService.GetBotBaseUri());

        var conversation =
            await directLineClient.Conversations.StartConversationAsync(
                cancellationToken: cancellationToken);

        await directLineClient.Conversations.PostActivityAsync(
            conversation.ConversationId,
            new Activity
            {
                Type = ActivityTypes.Message,
                From = new ChannelAccount
                {
                    Id = message.Sender,
                    Name = "unknown"
                },
                Text = message.Text,
                TextFormat = "plain",
                Locale = "en-Us"
            },
            cancellationToken: cancellationToken);

        var responses = await GetBotResponseActivitiesAsync(
            directLineClient,
            conversation.ConversationId,
            cancellationToken);

        await SendBotRepliesAsync(
            responses,
            message.Sender,
            cancellationToken);
    }

    private async Task<string> GetSenderTokenAsync(
        string senderKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(senderKey))
        {
            throw new ArgumentException(
                "A sender key is required.",
                nameof(senderKey));
        }

        var state = _senderTokens.GetOrAdd(
            senderKey,
            _ => new SenderTokenState());

        await state.Gate.WaitAsync(cancellationToken);

        try
        {
            if (state.Token is not null &&
                DateTimeOffset.UtcNow < state.ExpiresAtUtc)
            {
                return state.Token;
            }

            var result = await _botService.GetTokenAsync(
                cancellationToken);

            state.Token = result.Token;
            state.ExpiresAtUtc = result.ExpiresAtUtc;

            return result.Token;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async Task<List<Activity>> GetBotResponseActivitiesAsync(
        DirectLineClient directLineClient,
        string conversationId,
        CancellationToken cancellationToken)
    {
        string? watermark = null;

        var maximumAttempts =
            WaitForBotResponseMaxMilliseconds /
            PollForBotResponseIntervalMilliseconds;

        for (int attempt = 0; attempt < maximumAttempts; attempt++)
        {
            var response =
                await directLineClient.Conversations.GetActivitiesAsync(
                    conversationId,
                    watermark,
                    cancellationToken: cancellationToken);

            var botMessages = new List<Activity>();

            if (response is not null)
            {
                watermark = response.Watermark;

                if (response.Activities is not null)
                {
                    foreach (var activity in response.Activities)
                    {
                        if (activity.Type == ActivityTypes.Message &&
                            activity.From is not null &&
                            string.Equals(
                                activity.From.Name,
                                _botService.BotName,
                                StringComparison.Ordinal))
                        {
                            botMessages.Add(activity);
                        }
                    }
                }
            }

            if (botMessages.Count > 0)
            {
                return botMessages;
            }

            await Task.Delay(
                PollForBotResponseIntervalMilliseconds,
                cancellationToken);
        }

        throw new TimeoutException(
            "Copilot Studio did not return a message " +
            "within the configured polling attempts.");
    }

    private async Task SendBotRepliesAsync(
        List<Activity> responses,
        string recipient,
        CancellationToken cancellationToken)
    {
        foreach (var response in responses)
        {
            if (!string.IsNullOrEmpty(response.Text))
            {
                for (
                    int offset = 0;
                    offset < response.Text.Length;
                    offset += WhatsAppReplyChunkLength)
                {
                    int length = Math.Min(
                        WhatsAppReplyChunkLength,
                        response.Text.Length - offset);

                    string chunk = response.Text.Substring(
                        offset,
                        length);

                    await SendWhatsAppTextMessageAsync(
                        recipient,
                        chunk,
                        cancellationToken);
                }
            }

            if (response.SuggestedActions?.Actions is not null)
            {
                var titles = new List<string>();

                foreach (var action in response.SuggestedActions.Actions)
                {
                    if (!string.IsNullOrWhiteSpace(action.Title))
                    {
                        titles.Add(action.Title.Trim());
                    }
                }

                if (titles.Count > 0)
                {
                    string options = string.Join(" | ", titles);

                    await SendWhatsAppTextMessageAsync(
                        recipient,
                        options,
                        cancellationToken);
                }
            }
        }
    }

    private async Task SendWhatsAppTextMessageAsync(
        string recipient,
        string message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(recipient))
        {
            throw new ArgumentException(
                "A recipient is required.",
                nameof(recipient));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException(
                "Message text is required.",
                nameof(message));
        }

        if (message.Length > 4096)
        {
            throw new ArgumentException(
                "Message text cannot exceed 4096 characters.",
                nameof(message));
        }

        var url =
            $"https://graph.facebook.com/{_metaGraphVersion}/" +
            $"{_metaPhoneNumberId}/messages";

        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = recipient,
            type = "text",
            text = new
            {
                body = message
            }
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            url);

        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                _metaAccessToken);

        request.Content = JsonContent.Create(payload);

        using var response = await s_metaHttpClient.SendAsync(
            request,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(
                cancellationToken);

            throw new HttpRequestException(
                $"Meta message request failed " +
                $"({(int)response.StatusCode}): {error}",
                null,
                response.StatusCode);
        }
    }
}