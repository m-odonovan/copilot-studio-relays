// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
// original source code for this, is taken from https://github.com/microsoft/CopilotStudioSamples/tree/master/RelayBotSample
// some changes have been made from original


using Microsoft.Bot.Builder;
using Microsoft.Bot.Connector.DirectLine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DirectLineActivity = Microsoft.Bot.Connector.DirectLine.Activity;
using DirectLineActivityTypes = Microsoft.Bot.Connector.DirectLine.ActivityTypes;
using IConversationUpdateActivity = Microsoft.Bot.Schema.IConversationUpdateActivity;
using IMessageActivity = Microsoft.Bot.Schema.IMessageActivity;

namespace GenericRelayBot.BotConnectorApp.Bots
{
    /// <summary>
    /// This IBot implementation shows how to connect
    /// an external Azure Bot Service channel bot (external bot)
    /// to your Copilot Studio bot
    /// </summary>
    public class RelayBot : ActivityHandler
    {
        private const int WaitForBotResponseMaxMilSec = 5 * 1000;
        private const int PollForBotResponseIntervalMilSec = 1000;
        private static ConversationManager s_conversationManager = ConversationManager.Instance;
        private ResponseConverter _responseConverter;
        private IBotService _botService;

        public RelayBot(IBotService botService, ConversationManager conversationManager)
        {
            _botService = botService;
            _responseConverter = new ResponseConverter();
        }

        // Invoked when a conversation update activity is received from the external Azure Bot Service channel
        // Start a Copilot Studio bot conversation and store the mapping
        protected override async Task OnConversationUpdateActivityAsync(ITurnContext<IConversationUpdateActivity> turnContext, CancellationToken cancellationToken)
        {
            Console.WriteLine("Conversation update activity from user");
            await s_conversationManager.GetOrCreateBotConversationAsync(turnContext.Activity.Conversation.Id, _botService);
        }

        // Invoked when a message activity is received from the user
        // that that message and forward (send) it to Copilot Studio bot and get response
        protected override async Task OnMessageActivityAsync(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
        {
            Console.WriteLine("Received message from end user");
            var currentConversation = await s_conversationManager.GetOrCreateBotConversationAsync(turnContext.Activity.Conversation.Id, _botService);


            Console.WriteLine("User conversation id: " + turnContext.Activity.Conversation.Id);
            Console.WriteLine("Copilot Studio user conversation id: " + currentConversation.ConversationtId);

            using (DirectLineClient client = new DirectLineClient(currentConversation.Token))
            {
                client.BaseUri = new Uri(_botService.GetBotBaseUri());
                // Send user message using directlineClient
                Console.WriteLine("Sending message from user to Copilot Studio");
                await client.Conversations.PostActivityAsync(currentConversation.ConversationtId, new DirectLineActivity()
                {
                    Type = DirectLineActivityTypes.Message,
                    From = new ChannelAccount { Id = turnContext.Activity.From.Id, Name = turnContext.Activity.From.Name },
                    Text = turnContext.Activity.Text,
                    TextFormat = turnContext.Activity.TextFormat,
                    Locale = turnContext.Activity.Locale,
                });

                await RespondPowerVirtualAgentsBotReplyAsync(client, currentConversation, turnContext);
            }

            // Update LastConversationUpdateTime for session management
            currentConversation.LastConversationUpdateTime = DateTime.Now;
        }

        /// <summary>
        /// Wait for response back from Copilot Studio, and take that response and send back to Azure Bot Service user. It doesnt actually wait for response, it polls for a response every X milliseconds, and does this y times.
        /// </summary>
        /// <param name="client"></param>
        /// <param name="currentConversation"></param>
        /// <param name="turnContext"></param>
        /// <returns></returns>
        private async Task RespondPowerVirtualAgentsBotReplyAsync(DirectLineClient client, RelayConversation currentConversation, ITurnContext<IMessageActivity> turnContext)
        {

            var retryMax = WaitForBotResponseMaxMilSec / PollForBotResponseIntervalMilSec;
            for (int retry = 0; retry < retryMax; retry++)
            {
                // Get bot response using directlineClient,
                // response contains whole conversation history including user & bot's message
                Console.WriteLine("Looking for response from Copilot Studio");
                ActivitySet response = await client.Conversations.GetActivitiesAsync(currentConversation.ConversationtId, currentConversation.WaterMark);

                if (response != null)
                {
                    Console.WriteLine("Got a response from Copilot Studio, activity count is " + response.Activities.Count.ToString());

                    // Filter bot's reply message from response
                    //NB: this checks to see if the response is a message, and if it comes from a Copilot agent (bot), with the same friendly name as defined in the appsettings. The bot name
                    // must be the display name of the bot
                    List<DirectLineActivity> botResponses = response?.Activities?.Where(x =>
                        x.Type == DirectLineActivityTypes.Message &&
                            string.Equals(x.From.Name, _botService.GetBotName(), StringComparison.Ordinal)).ToList();

                    if (botResponses?.Count() > 0)
                    {
                        if (int.Parse(response?.Watermark ?? "0") <= int.Parse(currentConversation.WaterMark ?? "0"))
                        {
                            // means user sends new message, should break previous response poll
                            return;
                        }

                        //watermark allows to keep track of number of messages in conversation, and to request only new messages since last watermark, so you dont get all messages back from Copilot Studio bot
                        currentConversation.WaterMark = response.Watermark;
                        Console.WriteLine("Sending response to user");
                        //format the response from Copilot Studio and return to Azure Bot user
                        await turnContext.SendActivitiesAsync(_responseConverter.ConvertToBotSchemaActivities(botResponses).ToArray());
                    }
                }
                //wait before checking for new messages
                Thread.Sleep(PollForBotResponseIntervalMilSec);
            }
        }
    }
}