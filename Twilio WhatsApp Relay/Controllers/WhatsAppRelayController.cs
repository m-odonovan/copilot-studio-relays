using Microsoft.AspNetCore.Mvc;
using Microsoft.Bot.Connector.DirectLine;
using WhatsAppRelay.BotConnectorApp;

namespace WhatsAppRelay.Controllers;

[ApiController]
[Route("[controller]")]
public class WhatsAppRelayController : ControllerBase
{
 private readonly IConfiguration _configuration;
   private static string? _watermark = null;
   private const int _botReplyWaitIntervalInMilSec = 3000;
   private const string _botDisplayName = "Bot";
   private const string _userDisplayName = "You";
   private static string? s_endConversationMessage;
   private static BotService? s_botService;
   public static IDictionary<string, string> s_tokens = new Dictionary<string, string>();
    private const int WaitForBotResponseMaxMilSec = 14 * 1000; //twlio timeout is 15 seconds, so we wait for 13 seconds
    private const int PollForBotResponseIntervalMilSec = 1000;
   
   public WhatsAppRelayController(IConfiguration configuration)
    {
        _configuration = configuration;
        var botId = _configuration.GetValue<string>("BotId") ?? string.Empty;
        var tenantId = _configuration.GetValue<string>("BotTenantId") ?? string.Empty;
        var botTokenEndpoint = _configuration.GetValue<string>("BotTokenEndpoint") ?? string.Empty;
        var botName = _configuration.GetValue<string>("BotName") ?? string.Empty;
        s_botService = new BotService()
        {
            BotName = botName,
            BotId = botId,
            TenantId = tenantId,
            TokenEndPoint = botTokenEndpoint,
        };
        s_endConversationMessage = _configuration.GetValue<string>("EndConversationMessage") ?? "quit";
        if (string.IsNullOrEmpty(botId) || string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(botTokenEndpoint) || string.IsNullOrEmpty(botName))
        {
            Console.WriteLine("Update App.config and start again.");
            Console.WriteLine("Press any key to exit");
            Console.Read();
            Environment.Exit(0);
        }
    }

    [HttpPost]
    [Route("StartBot")]
    [Consumes("application/x-www-form-urlencoded")]
    //public async Task<ActionResult> StartBot(HttpContext req)
    public async Task<ActionResult> StartBot([FromForm] string From, [FromForm] string Body)
    {
        Console.WriteLine("From: " + From);
        Console.WriteLine("Body: " + Body);
        var token = await s_botService.GetTokenAsync();

        //Console.WriteLine("ddddd");
       // Console.WriteLine(token);
        //Console.WriteLine("ddddd");

        if (!s_tokens.ContainsKey(From))
        {
            s_tokens.Add(From, token);
        }
        //Console.WriteLine("s_tokens: " + s_tokens[From]);

        // var response = await StartConversation(Body, token);
        var response = await StartConversation(Body, From, s_tokens[From]);
        Console.WriteLine("response: " + response);
        //return Ok(response);
        
        
        return new ContentResult
            {
                Content = response,
                ContentType = "application/xml",
                StatusCode = 200
            };

   }

   //private static async Task<string> StartConversation(string inputMsg)
   private async Task<string> StartConversation(string inputMsg, string from, string token = "")
   {
       Console.WriteLine("inputMsg: " + inputMsg);
       //Console.WriteLine("token: " + token);
       using (var directLineClient = new DirectLineClient(token))
        {

            directLineClient.BaseUri = new Uri("https://europe.directline.botframework.com");
            //directLineClient.Domain = "https://europe.directline.botframework.com/v3/directline";
            var conversation = await directLineClient.Conversations.StartConversationAsync();
            var conversationtId = conversation.ConversationId;
            //string inputMessage;

            Console.WriteLine("conversationtId : " + conversationtId);
            //while (!string.Equals(inputMessage = , s_endConversationMessage, StringComparison.OrdinalIgnoreCase))

            if (!string.IsNullOrEmpty(inputMsg) && !string.Equals(inputMsg, s_endConversationMessage))
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
                if (responses.Count == 0)
                {
                    Console.WriteLine("No response from Copilot Studio bot.");
                    return "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response><Message>There was no response from agent, your query timed-out. Try again.</Message></Response>";
                }

               
                return BotReplyAsAPIResponse(responses);
            }

            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response><Message>Goodbye. Send 'hello' to start conversation.</Message></Response>";
        }
   }

   private static string BotReplyAsAPIResponse(List<Activity> responses)
   {

        //https://www.twilio.com/docs/messaging/twiml


        string responseStr = "";
       responses?.ForEach(responseActivity =>
       {
           // responseActivity is standard Microsoft.Bot.Connector.DirectLine.Activity
           // See https://github.com/Microsoft/botframework-sdk/blob/master/specs/botframework-activity/botframework-activity.md for reference
           // Showing examples of Text & SuggestedActions in response payload
           Console.WriteLine(responseActivity.Text);
           if (!string.IsNullOrEmpty(responseActivity.Text))
           {
               //responseStr = responseStr + "<Message>" + string.Join(Environment.NewLine, responseActivity.Text) + "</Message>";
               //responseStr = responseStr + "<Message>" + responseActivity.Text + "</Message>";
               
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
                       responseStr += "<Message><![CDATA[" + chunk + "]]></Message>";
                   }
               }
               else
               {
                   Console.WriteLine("Text is within the limit of " + maxLength + " characters.");
                   responseStr += text +"\n\n";
               }


                 

           }

           if (responseActivity.SuggestedActions != null && responseActivity.SuggestedActions.Actions != null)
           {
               var options = responseActivity.SuggestedActions?.Actions?.Select(a => a.Title.Trim()).ToList();
               //responseStr = responseStr + "<Message>" + $"\t{string.Join(" | ", options)}" + "</Message>";
               // responseStr = responseStr + "<Message>" + string.Join(" | ", options) + "</Message>";
               responseStr = responseStr + string.Join(" | ", options); 
           }
       });



        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response><Message><![CDATA[" + responseStr + "]]></Message></Response>";
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
