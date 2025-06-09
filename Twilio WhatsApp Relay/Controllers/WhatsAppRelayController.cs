using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Connector.DirectLine;
using WhatsAppRelay.BotConnectorApp;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;


namespace WhatsAppRelay.Controllers;

[ApiController]
[Route("[controller]")]
public class WhatsAppRelayController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private static string? _watermark = null;
    private const int _botReplyWaitIntervalInMilSec = 3000;
    private static BotService? s_botService;
    public static IDictionary<string, string> s_tokens = new Dictionary<string, string>();
    private const int WaitForBotResponseMaxMilSec = 14 * 1000; //twlio timeout is 15 seconds, so we wait for 13 seconds
    private const int PollForBotResponseIntervalMilSec = 1000;

    private static string fromNumber = string.Empty;

    public WhatsAppRelayController(IConfiguration configuration)
    {
        _configuration = configuration;

        //Twilio account details
        // These values should be set in your appsettings.json or environment variables
        var accountSid = _configuration.GetValue<string>("Twilio:AccountSid") ?? string.Empty;
        var authToken = _configuration.GetValue<string>("Twilio:AuthToken") ?? string.Empty;
        fromNumber = _configuration.GetValue<string>("Twilio:FromNumber") ?? string.Empty;

        var botId = _configuration.GetValue<string>("BotId") ?? string.Empty;
        var tenantId = _configuration.GetValue<string>("BotTenantId") ?? string.Empty;
        var botTokenEndpoint = _configuration.GetValue<string>("BotTokenEndpoint") ?? string.Empty;
        var botName = _configuration.GetValue<string>("BotName") ?? string.Empty;

        if (string.IsNullOrEmpty(accountSid) || string.IsNullOrEmpty(authToken) || string.IsNullOrEmpty(fromNumber) || string.IsNullOrEmpty(botId) || string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(botTokenEndpoint) || string.IsNullOrEmpty(botName))
        {
            Console.WriteLine("Update Appsettings.json or environment variables, one or more required params are missing e.g. botid, tenantid, botname, Twilio accound sid, token or fromnumber");
            Console.WriteLine("Ending web application");
            Environment.Exit(0);
        }

        s_botService = new BotService()
        {
            BotName = botName,
            BotId = botId,
            TenantId = tenantId,
            TokenEndPoint = botTokenEndpoint,
        };

        TwilioClient.Init(accountSid, authToken);
    }

    [HttpPost]
    [Route("StartBot")]
    [Consumes("application/x-www-form-urlencoded")]
    //public async Task<ActionResult> StartBot(HttpContext req)
    public async Task<ActionResult> StartBot([FromForm] string From, [FromForm] string Body)
    {


        Console.WriteLine("From: " + From);
        Console.WriteLine("Body: " + Body);

        // Ensure token is available for this user
        var token = await s_botService.GetTokenAsync();
        if (!s_tokens.ContainsKey(From))
        {
            s_tokens.Add(From, token);
        }

        // Process the bot conversation and Twilio messaging in the background
        _ = Task.Run(async () =>
        {
            await StartConversation(Body, From, s_tokens[From]);


        });

        // Respond to Twilio immediately, because otherwise it times out after 15 seconds
        // and we don't want to wait for the bot response to send Twilio response
        var immediateResponse = new ContentResult
        {
            Content = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response></Response>",
            ContentType = "application/xml",
            StatusCode = 200
        };

        return immediateResponse;

    }

    //private static async Task<string> StartConversation(string inputMsg)
    private async Task StartConversation(string inputMsg, string from, string token = "")
    {
        Console.WriteLine("inputMsg: " + inputMsg);
        //Console.WriteLine("token: " + token);
        using (var directLineClient = new DirectLineClient(token))
        {

            //TODO: update to dynamic get locatoin, if agent in Europe you need this
            directLineClient.BaseUri = new Uri("https://europe.directline.botframework.com");
            //directLineClient.Domain = "https://europe.directline.botframework.com/v3/directline";
            var conversation = await directLineClient.Conversations.StartConversationAsync();
            var conversationtId = conversation.ConversationId;
            //string inputMessage;

            Console.WriteLine("conversationtId : " + conversationtId);
            //while (!string.Equals(inputMessage = , s_endConversationMessage, StringComparison.OrdinalIgnoreCase))

            if (!string.IsNullOrEmpty(inputMsg))
            {
                // Send user message using directlineClient
                await directLineClient.Conversations.PostActivityAsync(conversationtId, new Activity()
                {
                    Type = ActivityTypes.Message,
                    From = new ChannelAccount { Id = from, Name = "unknown" },
                    Text = inputMsg,
                    TextFormat = "plain",
                    Locale = "en-Us",
                });

                // Get bot response using directlinClient
                List<Activity> responses = await GetBotResponseActivitiesAsync(directLineClient, conversationtId);

                Console.WriteLine("responsescount: " + responses.Count);
                if (responses.Count > 0)
                    BotReplyAsAPIResponse(responses, from);


            }


        }
    }

    private static void BotReplyAsAPIResponse(List<Activity> responses, string to)
    {




        responses?.ForEach(async responseActivity =>
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
                        await SendWhatsAppTextMessageViaTwilio(to, chunk);
                    }
                }
                else
                {
                    Console.WriteLine("Text is within the limit of " + maxLength + " characters.");
                    await SendWhatsAppTextMessageViaTwilio(to, text);
                }




            }

            string responseStr = "";
            if (responseActivity.SuggestedActions != null && responseActivity.SuggestedActions.Actions != null)
            {
                var options = responseActivity.SuggestedActions?.Actions?.Select(a => a.Title.Trim()).ToList();

                responseStr = responseStr + string.Join(" | ", options);
                await SendWhatsAppTextMessageViaTwilio(to, responseStr);
            }
        });




    }

    /// <summary>
    /// Send WhatsApp text message via Twilio
    /// This method uses Twilio's API to send a WhatsApp message.
    /// </summary>
    /// <param name="to">Must be in +27.... format</param>
    /// <param name="message">Text message as text string, no longer than 1600 characters</param>
    /// <returns></returns>
    private static async Task SendWhatsAppTextMessageViaTwilio(string to, string message)
    {

        var toNumber = $"whatsapp:{to}";

        try
        {


            var sendMessage = await MessageResource.CreateAsync(
            body: message,
            from: new PhoneNumber($"whatsapp:{fromNumber}"), // Your Twilio number
            to: new PhoneNumber(toNumber)    // Recipient's number
                   );

            Console.WriteLine($"Message SID: {sendMessage.Sid}");

        }
        catch (Twilio.Exceptions.ApiException ex)       {


            Console.WriteLine($"Twilio API Error: {ex.Message}");
            Console.WriteLine($"Status Code: {ex.Status}");
            Console.WriteLine($"More Info: {ex.MoreInfo}");
        }


    }

   /// <summary>
   /// Send WhatsApp template message via Twilio
   /// This method uses Twilio's API to send a WhatsApp message with a template.
   /// Not yet implemented, plan to add support in the future
   /// </summary>
   /// <param name="to"></param>
   /// <param name="message"></param>
   /// <param name="templateID"></param>
   /// <returns></returns>
    private static async Task SendWhatsAppTemplateMessageViaTwilio(string to, string message,string templateID)
    {

        var toNumber = $"whatsapp:{to}";

        try
        {

            //change this below to use MessageOptions from Twilio SDK to send template message
            //var messageOptions = new CreateMessageOptions(new PhoneNumber(toNumber));
            //messageOptions.From = new PhoneNumber($"whatsapp:{fromNumber}"); // Your Twilio number
            //messageOptions.Body = message;
            //messageOptions.Template = templateID; // Template ID to use
            //messageOptions.MessagingServiceSid = "your_messaging_service_sid"; // Optional, if using Messaging Service
            //messageOptions.StatusCallback = new Uri("https://your-callback-url.com"); // Optional, for status updates

            var sendMessage = await MessageResource.CreateAsync(
            body: message,
            from: new PhoneNumber($"whatsapp:{fromNumber}"), // Your Twilio number
            to: new PhoneNumber(toNumber)    // Recipient's number
                   );

            Console.WriteLine($"Message SID: {sendMessage.Sid}");

        }
        catch (Twilio.Exceptions.ApiException ex)       {


            Console.WriteLine($"Twilio API Error: {ex.Message}");
            Console.WriteLine($"Status Code: {ex.Status}");
            Console.WriteLine($"More Info: {ex.MoreInfo}");
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
