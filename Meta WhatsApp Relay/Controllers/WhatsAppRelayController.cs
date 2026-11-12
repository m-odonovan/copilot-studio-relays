using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Connector.DirectLine;
using WhatsAppRelay.BotConnectorApp;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;


namespace WhatsAppRelay.Controllers;

[ApiController]
[Route("[controller]")]
public class WhatsAppRelayController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private static string? _watermark = null;

    private static BotService? s_botService;
    //public static IDictionary<string, string> s_tokens = new Dictionary<string, string>();
    private const int WaitForBotResponseMaxMilSec = 30 * 1000;
    private const int PollForBotResponseIntervalMilSec = 1500;


    private static string metaAppSecret = string.Empty;
    private static string metaPhoneNumberId = string.Empty;
    private static string metaWabaId = string.Empty;

    private static string metaVerifyToken = string.Empty;

    private static string metaAccessToken = string.Empty;

    private static string metaGraphVersion = string.Empty;

    private static readonly HttpClient s_metaHttpClient = new();


    private static readonly ConcurrentDictionary<string, SenderTokenState> s_tokens =
        new(StringComparer.Ordinal);

    private sealed class SenderTokenState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string? Token { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }



    public WhatsAppRelayController(IConfiguration configuration)
    {
        _configuration = configuration;

        //read values from configuration for Meta settings i.e. appsettings json or Azure App Service configuration
        metaGraphVersion = _configuration.GetValue<string>("Meta:GraphVersion") ?? string.Empty;
        metaAccessToken = _configuration.GetValue<string>("Meta:AccessToken") ?? string.Empty;
        metaAppSecret = _configuration.GetValue<string>("Meta:AppSecret") ?? string.Empty;
        metaVerifyToken = _configuration.GetValue<string>("Meta:VerifyToken") ?? string.Empty;
        metaPhoneNumberId = _configuration.GetValue<string>("Meta:PhoneNumberId") ?? string.Empty;
        metaWabaId = _configuration.GetValue<string>("Meta:WabaId") ?? string.Empty;

        if (string.IsNullOrEmpty(metaGraphVersion) || string.IsNullOrEmpty(metaAccessToken) || string.IsNullOrEmpty(metaAppSecret) || string.IsNullOrEmpty(metaVerifyToken) || string.IsNullOrEmpty(metaPhoneNumberId) || string.IsNullOrEmpty(metaWabaId))
        {
            Console.WriteLine("Update Appsettings.json or environment variables, one or more Meta required params are missing e.g. GraphVersion, AccessToken, AppSecret, VerifyToken, PhoneNumberId, WabaId");
            Console.WriteLine("Ending web application");
            Environment.Exit(0);
        }


        var botId = _configuration.GetValue<string>("BotId") ?? string.Empty;
        var tenantId = _configuration.GetValue<string>("BotTenantId") ?? string.Empty;
        var botTokenEndpoint = _configuration.GetValue<string>("BotTokenEndpoint") ?? string.Empty;
        var botName = _configuration.GetValue<string>("BotName") ?? string.Empty;
        var botLocation = _configuration.GetValue<string>("BotLocation") ?? string.Empty;

        if (string.IsNullOrEmpty(botId) || string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(botTokenEndpoint) || string.IsNullOrEmpty(botName) || string.IsNullOrEmpty(botLocation))
        {
            Console.WriteLine("Update Appsettings.json or environment variables, one or more required Copilot Studio agent params are missing e.g. botid, tenantid, botname, botlocation, bottokenendpoint");
            Console.WriteLine("Ending web application");
            Environment.Exit(0);
        }


        //this is used later for obtaining bot tokens and interacting with the Copilot Studio agent
        s_botService = new BotService()
        {
            BotName = botName,
            BotId = botId,
            TenantId = tenantId,
            TokenEndPoint = botTokenEndpoint,
            BotLocation = botLocation
        };


    }





    private static async Task<string> GetSenderTokenAsync(string? sender)
    {
        if (string.IsNullOrWhiteSpace(sender))
            throw new ArgumentException("A sender is required.", nameof(sender));

        var state = s_tokens.GetOrAdd(sender, _ => new SenderTokenState());

        await state.Gate.WaitAsync();
        try
        {
            if (state.Token is not null &&
                DateTimeOffset.UtcNow < state.ExpiresAtUtc)
            {
                return state.Token;
            }

            var botService = s_botService
                ?? throw new InvalidOperationException("Bot service is not initialized.");

            var result = await botService.GetTokenAsync();

            state.Token = result.Token;
            state.ExpiresAtUtc = result.ExpiresAtUtc;

            return result.Token;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <summary>
    /// This is what is called by Meta to verify the webhook during setup of Meta webhook. Meta calls the endpoint using http get, and suppliers these params.
    /// </summary>
    /// <param name="mode">Will always be subscribe</param>
    /// <param name="challenge">Random string that should be echoed back to Meta for verification</param>
    /// <param name="verifyToken">Token that should match the one configured for Meta webhook verification</param>
    /// <returns></returns>
    [HttpGet("messagerelay")]
    public async Task<ActionResult> VerifyWebhook(
    [FromQuery(Name = "hub.mode")] string? mode,
    [FromQuery(Name = "hub.challenge")] string? challenge,
    [FromQuery(Name = "hub.verify_token")] string? verifyToken)
    {
        var expectedToken = metaVerifyToken;

        //do validation, and it not valid return 403 Forbidden
        if (mode != "subscribe" ||
            string.IsNullOrEmpty(expectedToken) ||
            !string.Equals(verifyToken, expectedToken, StringComparison.Ordinal) ||
            string.IsNullOrEmpty(challenge))
        {
            return new ContentResult
            {
                Content = "Invalid request",
                ContentType = "text/plain",
                StatusCode = 403
            };
        }

        //respond to Meta  with the challenge to complete verification
        return new ContentResult
        {
            Content = challenge,
            ContentType = "text/plain",
            StatusCode = 200
        };


    }



    /// <summary>
    /// This is what Meta calls when it sends a message to this relay endpoint.
    /// </summary>
    /// <param name="signature">The X-Hub-Signature-256 header from Meta for verifying the request</param>
    /// <param name="cancellationToken">Cancellation token for the request</param>
    /// <returns></returns>
    [HttpPost]
    [Route("messagerelay")]
    [Consumes("application/json")]
    public async Task<ActionResult> MessageRelay(
    [FromHeader(Name = "X-Hub-Signature-256")] string? signature, CancellationToken cancellationToken)
    {

        //if we in development mode, maybe we need to do something different, this is just a placeholder for now
        var env = HttpContext.RequestServices.GetService(typeof(IWebHostEnvironment)) as IWebHostEnvironment;
        if (env == null || !env.IsDevelopment())
        {
            //do nothing differently for now.
        }

        using var body = new MemoryStream();
        await Request.Body.CopyToAsync(body, cancellationToken);
        var payload = body.ToArray();

        if (!IsMetaRequestValid(payload, signature, out var errorMessage))
        {
            return Unauthorized(errorMessage);
        }


        //if we get to this point, all validation passed
        MetaEvent[] events;
        //now take the JSON object from the request body and parse it into array of Meta event objects
        // each object in the array is a message to process or a status notification from Meta.
        try
        {
            using var document = JsonDocument.Parse(payload);

            events = MetaWebhookParser.Parse(
                document.RootElement,
                metaWabaId,
                metaPhoneNumberId);
        }
        catch (JsonException)
        {
            return BadRequest("Invalid JSON.");
        }
        catch (RelayException ex)
        {
            //TODO: should I not rather hide the error so caller doesnt see the issue?
            // yes, I should probably log the error internally for debugging purposes.
            return BadRequest(new { error = ex.Code, message = ex.Message });
        }

        IncomingMessage[] messages = events.OfType<IncomingMessage>().ToArray();
        Console.WriteLine("Incoming messages count: " + messages.Length);

        DeliveryStatus[] statuses = events.OfType<DeliveryStatus>().ToArray();
        Console.WriteLine("Delivery statuses count: " + statuses.Length);

        //loop through all delivery status events and log them
        foreach (var status in statuses)
        {
            Console.WriteLine(status);
        }


        //todo: Meta might the same post several times, if it thinks it wasnt previously successful, so we need to cater for this. It says our server should cater
        // for deduplication of repeated posts. So we need to be clever when we respond with a 200 400 if error. If 400 it will retry
        // and consider using a queue to handle incoming messages asynchronously instead of processing them immediately

        //meta can send more than 1 message in a JSON payload, so now we loop through each message and send it to Copilot Studio agent
        foreach (var message in messages)
        {
            var userKey = message.Key;
            var userMessage = message.Text;
            var fromNumber = message.Sender;

            Console.WriteLine("User key: " + userKey);
            Console.WriteLine("Body: " + userMessage);
            Console.WriteLine("From: " + fromNumber);

            var token = await GetSenderTokenAsync(userKey);

            _ = Task.Run(async () =>
            {
                await StartConversation(userMessage, fromNumber, userKey, token);
            });
        }

        // Respond to Meta as soon as possible, because otherwise it times out after 15 seconds
        // and we don't want to wait for the bot response to send Meta response
        // todo: this might need to be moved above the code block above, and will be when we rather add the messages to a queue
        //respond to Meta immediately with the challenge to complete verification
        return new ContentResult
        {
            Content = "",
            ContentType = "text/plain",
            StatusCode = 200
        };


    }

    /// <summary>
    /// Check if incoming request is valid from meta, if not, it sets the error message and returns false.
    /// </summary>
    /// <param name="payload"></param>
    /// <param name="signature"></param>
    /// <param name="errorMessage"></param>
    /// <returns></returns>
    private static bool IsMetaRequestValid(
    byte[] payload,
    string? signature,
    out string errorMessage)
    {
        errorMessage = string.Empty;

        if (signature == null ||
            signature.Length != 71 ||
            !signature.StartsWith("sha256=", StringComparison.Ordinal))
        {
            errorMessage = "Missing or invalid webhook signature.";
            return false;
        }

        byte[] suppliedHash;
        try
        {
            suppliedHash = Convert.FromHexString(signature[7..]);
        }
        catch (FormatException)
        {
            errorMessage = "Invalid webhook signature.";
            return false;
        }

        using var hmac =
            new HMACSHA256(Encoding.UTF8.GetBytes(metaAppSecret));

        var expectedHash = hmac.ComputeHash(payload);

        if (!CryptographicOperations.FixedTimeEquals(
                expectedHash, suppliedHash))
        {
            errorMessage = "Invalid webhook signature.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Starts a conversation with the Copilot Studio agent (bot) using Direct Line and sends the input message.
    /// </summary>
    /// <param name="inputMsg"></param>
    /// <param name="fromNumber"></param>
    /// <param name="userKey"></param>
    /// <param name="token"></param>
    /// <returns>A Task representing the asynchronous operation.</returns>
    private async Task StartConversation(string inputMsg, string fromNumber, string userKey, string token = "")
    {
        Console.WriteLine("inputMsg: " + inputMsg);
        //Console.WriteLine("token: " + token);
        using (var directLineClient = new DirectLineClient(token))
        {

            //get uri to directline
            directLineClient.BaseUri = new Uri(s_botService.GetBotBaseUri());

            var conversation = await directLineClient.Conversations.StartConversationAsync();
            var conversationtId = conversation.ConversationId;

            Console.WriteLine("conversationtId : " + conversationtId);
            //while (!string.Equals(inputMessage = , s_endConversationMessage, StringComparison.OrdinalIgnoreCase))

            if (!string.IsNullOrEmpty(inputMsg))
            {
                // Send user message using directlineClient
                await directLineClient.Conversations.PostActivityAsync(conversationtId, new Activity()
                {
                    Type = ActivityTypes.Message,
                    From = new ChannelAccount { Id = fromNumber, Name = "unknown" },
                    Text = inputMsg,
                    TextFormat = "plain",
                    Locale = "en-Us",
                });

                // Get bot response using directlinClient
                List<Activity> responses = await GetBotResponseActivitiesAsync(directLineClient, conversationtId);

                Console.WriteLine("responsescount: " + responses.Count);
                if (responses.Count > 0)
                    await BotReplyAsAPIResponse(responses, fromNumber);


            }


        }
    }

    private static async Task  BotReplyAsAPIResponse(List<Activity> responses, string to)
    {

        foreach (var responseActivity in responses)
                {
                    // responseActivity is standard Microsoft.Bot.Connector.DirectLine.Activity
                    // See https://github.com/Microsoft/botframework-sdk/blob/master/specs/botframework-activity/botframework-activity.md for reference
                    // Showing examples of Text & SuggestedActions in response payload
                    Console.WriteLine(responseActivity.Text);
                    if (!string.IsNullOrEmpty(responseActivity.Text))
                    {


                        const int maxLength = 1600;
                        string text = responseActivity.Text;
                        if (text.Length > maxLength)
                        {
                            // Split the text into chunks of maxLength and wrap each chunk in <Message> tags
                            // This is to avoid exceeding the maximum length for a single message
                            // and to ensure that the response is well-formed XML
                            Console.WriteLine("Splitting text into chunks of " + maxLength + " characters.");
                            for (int i = 0; i < text.Length; i += maxLength)
                            {
                                int length = Math.Min(maxLength, text.Length - i);
                                string chunk = text.Substring(i, length);
                                await SendWhatsAppTextMessageViaMeta(to, chunk);
                            }
                        }
                        else
                        {
                            Console.WriteLine("Text is within the limit of " + maxLength + " characters.");
                            await SendWhatsAppTextMessageViaMeta(to, text);
                        }




                    }

                    string responseStr = "";
                    if (responseActivity.SuggestedActions != null && responseActivity.SuggestedActions.Actions != null)
                    {
                        var options = responseActivity.SuggestedActions?.Actions?.Select(a => a.Title.Trim()).ToList();

                        responseStr = responseStr + string.Join(" | ", options);
                        await SendWhatsAppTextMessageViaMeta(to, responseStr);
                    }
                }




    }



    private static async Task SendWhatsAppTemplateMessageViaMeta(
    string to,
    string templateName = "hello_world",
    string languageCode = "en_US")
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
            $"https://graph.facebook.com/{metaGraphVersion}/{metaPhoneNumberId}/messages";

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
            new AuthenticationHeaderValue("Bearer", metaAccessToken);
        request.Content = JsonContent.Create(payload);

        using var response = await s_metaHttpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();

            throw new HttpRequestException(
                $"Meta message request failed ({(int)response.StatusCode}): {error}",
                null,
                response.StatusCode);
        }
    }

    private static async Task SendWhatsAppTextMessageViaMeta(
        string to,
        string message)
    {
        if (string.IsNullOrWhiteSpace(to))
            throw new ArgumentException("A recipient is required.", nameof(to));

        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Message text is required.", nameof(message));

        if (message.Length > 4096)
            throw new ArgumentException(
                "Message text cannot exceed 4096 characters.", nameof(message));

        var url =
            $"https://graph.facebook.com/{metaGraphVersion}/{metaPhoneNumberId}/messages";

        var payload = new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to,
            type = "text",
            text = new
            {
                body = message
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", metaAccessToken);
        request.Content = JsonContent.Create(payload);

        using var response = await s_metaHttpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();

            throw new HttpRequestException(
                $"Meta text message request failed ({(int)response.StatusCode}): {error}",
                null,
                response.StatusCode);
        }
    }


    /// <summary>
    /// Use directlineClient to get bot response
    /// </summary>
    /// <returns>List of DirectLine activities</returns>
    /// <param name="directLineClient">directline client</param>
    /// <param name="conversationtId">current conversation ID</param>
    /// <param name="botName">name of bot to connect to</param>
    private static async Task<List<Activity>> GetBotResponseActivitiesAsync(DirectLineClient directLineClient, string conversationtId)
    {

        Console.WriteLine(s_botService.BotName);

        ActivitySet response = null;
        List<Activity> result = new List<Activity>();

        var retryMax = WaitForBotResponseMaxMilSec / PollForBotResponseIntervalMilSec;

        for (int retry = 0; retry < retryMax; retry++)
        {
            // Get bot response using directlineClient,
            // response contains whole conversation history including user & bot's message
            Console.WriteLine("Looking for response from Copilot Studio");
            Console.WriteLine("Attempt number :" + retry);
            response = await directLineClient.Conversations.GetActivitiesAsync(conversationtId, _watermark);

            if (response != null)
            {
                Console.WriteLine("Got a response from Copilot Studio, activity count is " + response.Activities.Count.ToString());

                //watermark allows to keep track of number of messages in conversation, and to request only new messages since last watermark, so you dont get all messages back from Copilot Studio bot
                _watermark = response?.Watermark;

                //todo: need to figure out how to rather compare on botid instead of botname, because if botname changes in Copilot Studio, person might forget to change settings JSON botname
                result = response?.Activities?.Where(x =>
                    x.Type == ActivityTypes.Message &&
                    string.Equals(x.From.Name, s_botService.BotName, StringComparison.Ordinal)).ToList();

                //Console.WriteLine(result);
                if (result != null && result.Any())

                {
                    Console.WriteLine(result);
                    return result;
                }
            }


            Console.WriteLine("No bot response yet. Waiting for " + PollForBotResponseIntervalMilSec + " milliseconds.");
            Thread.Sleep(PollForBotResponseIntervalMilSec);
        }

        return new List<Activity>();
    }

}
