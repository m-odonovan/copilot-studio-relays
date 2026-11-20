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

/// <summary>
/// Processes incoming WhatsApp messages by sending them to the Copilot bot and handling the bot's responses.
/// </summary>
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


    /// <summary>
    /// this stores state about the sender, their conversation and watermark for their conversation, including saved auth token and its expiration time
    /// </summary>
    private sealed class SenderTokenState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public string? Token { get; set; }

        public DateTimeOffset ExpiresAtUtc { get; set; }

        public string? ConversationId { get; set; }

        public string? Watermark { get; set; }
    }

    public CopilotMessageProcessor(
        IConfiguration configuration,
        ILogger<CopilotMessageProcessor> logger)
    {
        _logger = logger;
        _logger.LogInformation("Initializing CopilotMessageProcessor.");

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

        if (string.IsNullOrWhiteSpace(message.Key))
        {
            throw new ArgumentException(
                "A sender key is required.",
                nameof(message));
        }

        var state = _senderTokens.GetOrAdd(
            message.Key,
            _ => new SenderTokenState());

        await state.Gate.WaitAsync(cancellationToken);

        try
        {
            var token = await GetSenderTokenAsync(
                state,
                cancellationToken);

            using var directLineClient = new DirectLineClient(token);

            directLineClient.BaseUri =
                new Uri(_botService.GetBotBaseUri());

            var conversation =
                await directLineClient.Conversations.StartConversationAsync(
                    cancellationToken: cancellationToken);

            var conversationId = conversation.ConversationId;

            if (string.IsNullOrWhiteSpace(conversationId))
            {
                throw new InvalidOperationException(
                    "Direct Line did not return a conversation ID.");
            }

            if (!string.Equals(
                    state.ConversationId,
                    conversationId,
                    StringComparison.Ordinal))
            {
                state.ConversationId = conversationId;
                state.Watermark = null;

                _logger.LogInformation(
                    "Using conversation {ConversationId} for sender key " +
                    "{SenderKey}; watermark reset.",
                    conversationId,
                    message.Key);
            }

            await directLineClient.Conversations.PostActivityAsync(
                conversationId,
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
                conversationId,
                state,
                cancellationToken);

            await SendBotRepliesAsync(
                responses,
                message.Sender,
                cancellationToken);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async Task<string> GetSenderTokenAsync(
       SenderTokenState state,
        CancellationToken cancellationToken)
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

    private async Task<List<Activity>> GetBotResponseActivitiesAsync(
    DirectLineClient directLineClient,
    string conversationId,
    SenderTokenState state,
    CancellationToken cancellationToken)
    {


        var maximumAttempts =
            WaitForBotResponseMaxMilliseconds /
            PollForBotResponseIntervalMilliseconds;

        for (int attempt = 0; attempt < maximumAttempts; attempt++)
        {

            _logger.LogInformation(
                "Polling for bot response, attempt {Attempt} of {MaximumAttempts}.",
                attempt + 1,
                maximumAttempts);
            var response =
      await directLineClient.Conversations.GetActivitiesAsync(
          conversationId,
          state.Watermark,
          cancellationToken: cancellationToken);

            //TOO: shouldnt this be outside the loop? otherwise we are creating a new list on each attempt

            var botMessages = new List<Activity>();

            if (response is not null)
            {
                _logger.LogInformation(
                    "Received response from bot with activity count of {ActivityCount}.",
                    response.Activities?.Count ?? 0);

                if (response.Watermark is not null)
                {
                    state.Watermark = response.Watermark;
                }

                if (response.Activities is not null)
                {
                    foreach (var activity in response.Activities)
                    {
                        // Ignore activities that are not messages from the bot,
                        //NB: it is important that the botname in the settings for this app service match the botname in Copilot Studio, otherwise you wont see messages
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

            _logger.LogInformation(
                "No bot response received, waiting for {Interval} milliseconds before next attempt.",
                PollForBotResponseIntervalMilliseconds);
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

        // responseActivity is standard Microsoft.Bot.Connector.DirectLine.Activity
        // See https://github.com/Microsoft/botframework-sdk/blob/master/specs/botframework-activity/botframework-activity.md for reference
        // Showing examples of Text & SuggestedActions in response payload
        foreach (var responseActivity in responses)
        {
            //if the response type is text, send to meta as text
            if (!string.IsNullOrEmpty(responseActivity.Text))
            {
                // Split the message into chunks if it exceeds the WhatsApp reply length limit.
                for (
                    int offset = 0;
                    offset < responseActivity.Text.Length;
                    offset += WhatsAppReplyChunkLength)
                {
                    int length = Math.Min(
                        WhatsAppReplyChunkLength,
                        responseActivity.Text.Length - offset);

                    string chunk = responseActivity.Text.Substring(
                        offset,
                        length);

                    await SendWhatsAppTextMessageAsync(
                        recipient,
                        chunk,
                        cancellationToken);
                }
            }
            //if the response contains suggested actions, send them as text options to meta
            // suggested actions are buttons, so we dont send buttons, we send text delimeted by pipe
            if (responseActivity.SuggestedActions?.Actions is not null)
            {
                var titles = new List<string>();

                foreach (var action in responseActivity.SuggestedActions.Actions)
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


    /// <summary>
    /// Sends a text message to a WhatsApp recipient using the Meta Graph API.
    /// </summary>
    /// <param name="recipient">The WhatsApp recipient's phone number in international format</param>
    /// <param name="message">The text message to send</param>
    /// <param name="cancellationToken">The cancellation token to cancel the operation</param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="HttpRequestException"></exception>
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



    /// <summary>
    /// Not currently used, will flesh this out when we need to support template message sending via Meta WhatsApp API
    /// </summary>
    /// <param name="to"></param>
    /// <param name="templateName"></param>
    /// <param name="languageCode"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="HttpRequestException"></exception>
    private async Task SendWhatsAppTemplateMessageViaMeta(
    string to,
    string templateName,
    string languageCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(to))
            throw new ArgumentException("A recipient is required.", nameof(to));

        if (string.IsNullOrWhiteSpace(templateName))
            throw new ArgumentException(
                "A template name is required.", nameof(templateName));

        if (string.IsNullOrWhiteSpace(languageCode))
            throw new ArgumentException(
                "A language code is required.", nameof(languageCode));

        var url =
            $"https://graph.facebook.com/{_metaGraphVersion}/{_metaPhoneNumberId}/messages";

        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to,
            type = "template",
            template = new
            {
                name = templateName,
                language = new { code = languageCode }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", _metaAccessToken);
        request.Content = JsonContent.Create(payload);

        using var response = await s_metaHttpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(
                cancellationToken);

            throw new HttpRequestException(
                $"Meta message request failed ({(int)response.StatusCode}): {error}",
                null,
                response.StatusCode);
        }
    }
}