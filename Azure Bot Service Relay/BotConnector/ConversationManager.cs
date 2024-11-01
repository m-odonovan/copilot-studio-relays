// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
// original source code for this, is taken from https://github.com/microsoft/CopilotStudioSamples/tree/master/RelayBotSample
// some changes have been made from original


using Microsoft.Bot.Connector.DirectLine;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Timers;

namespace GenericRelayBot.BotConnectorApp
{
    /// <summary>
    /// class for manage lifecycle of all conversations,
    /// including mapping of external Azure Bot Service channel conversation to your Power Virtual Agents bot converstaion,
    /// creating/ending conversations and refreshing tokens
    /// </summary>
    public class ConversationManager
    {
        private static readonly object s_padlock = new object();
        private static ConversationManager? s_singleton = null;

        public static Dictionary<string, RelayConversation> ConversationRouter { get; private set; } = new Dictionary<string, RelayConversation>();

        public static double TokenRefreshCheckIntervalInMinute { get; set; }

        public static double TokenRefreshIntervalInMinute { get; set; }

        public static double ConversationEndAfterIdleTimeInMinute { get; set; }

        public static double ConversationEndCheckIntervalInMinute { get; set; }

        /// <summary>
        /// Singleton instance of ConversationManager. This is a single static instance so that the dictionary object which holds the mapping
        /// between Bot Service conversation ID and Copilot Studio conversation ID (and other stuff), is shared across all incoming requests.
        /// </summary>
        public static ConversationManager Instance
        {
            get
            {
                lock (s_padlock)
                {
                    if (s_singleton == null)
                    {
                        // Initialize token refresh check timer and set interval.
                        System.Timers.Timer s_TokenRefreshTimer = new System.Timers.Timer();
                        s_TokenRefreshTimer.Interval = TokenRefreshCheckIntervalInMinute * 60 * 1000;

                        // Hook up the Elapsed event for the timer.
                        s_TokenRefreshTimer.Elapsed += OnTokenRefreshCheckEvent;

                        // Have the timer fire repeated events (true is the default)
                        s_TokenRefreshTimer.AutoReset = true;

                        // Start the timer
                        s_TokenRefreshTimer.Enabled = true;

                        // Initialize conversation idle check timer and set interval.
                        System.Timers.Timer s_ConversationIdleCheckTimer = new System.Timers.Timer();
                        s_ConversationIdleCheckTimer.Interval = ConversationEndCheckIntervalInMinute * 60 * 1000;

                        // Hook up the Elapsed event for the timer.
                        s_ConversationIdleCheckTimer.Elapsed += OnConversationIdleCheckEvent;

                        // Have the timer fire repeated events (true is the default)
                        s_ConversationIdleCheckTimer.AutoReset = true;

                        // Start the timer
                        s_ConversationIdleCheckTimer.Enabled = true;

                        s_singleton = new ConversationManager();
                    }

                    return s_singleton;
                }
            }
        }

        /// <summary>
        /// Search if an external Azure Bot Service channel conversation is
        /// connected to an existing Copilot Studio bot conversation
        /// </summary>
        /// <returns>true if conversation mapping exists, otherwiser false</returns>
        /// <param name="externalCID">external Azure Bot Service channel conversation ID</param>
        public bool ConversationExists(string externalCID)
        {
            return ConversationRouter.ContainsKey(externalCID);
        }

        /// <summary>
        /// Start a new Copilot Studio bot conversation using Direct Line to Copilot Studio
        /// Onece this is created, store the details of the mapping between Bot Service conversation and Copilot Studio Conversation into an object 
        /// called RelayConversation, and add to the dictionary. Key for dictionary records is the conversationID from Bot Service conversation
        /// </summary>
        /// <returns>Created Copilot Studi bot conversation</returns>
        /// <param name="externalCID">external Azure Bot Service channel conversation ID</param>
        public async Task<RelayConversation> StartBotConversationAsync(string externalCID, IBotService botService)
        {
            string token = await botService.GetTokenAsync();
            if (string.IsNullOrEmpty(token))
            {
                throw new TaskCanceledException("Exception caught: Failed to retrieve Copilot Studio agent token, it is blank.");
            }

            using (var directLineClient = new DirectLineClient(token))
            {
                Console.WriteLine("Starting new conversation with Copilot Studio");
                Console.WriteLine(botService.GetBotBaseUri());
                directLineClient.BaseUri = new Uri(botService.GetBotBaseUri());
                var conversation = await directLineClient.Conversations.StartConversationAsync();
                string conversationId = conversation?.ConversationId ?? string.Empty; //added the ?? string.empty
                if (string.IsNullOrEmpty(conversationId))
                {
                    throw new TaskCanceledException("Exception caught: directline failed to create conversation using retrieved token. Tried connecting to direct line base URI of " + botService.GetBotBaseUri());
                }


                var newBotConversation = new RelayConversation()
                {
                    Token = token,
                    ConversationtId = conversationId,
                    WaterMark = null
                };
                ConversationRouter[externalCID] = newBotConversation;
            }

            return ConversationRouter[externalCID];
        }

        /// <summary>
        /// Retrive or start a Copilot Studio bot conversation
        /// for a given external Azure Bot Service channel conversation
        /// </summary>
        /// <returns>Copilot Studio bot conversation</returns>
        /// <param name="externalCID">external Azure Bot Service channel conversation ID</param>
        public async Task<RelayConversation> GetOrCreateBotConversationAsync(string externalCID, IBotService botService)
        {
            //this is going to look in a dictionary for a record which holds a mapping between the bot service conversation and the 
            //Copilot Studio conversation. If one is found, return that, otherwise create a new conversation with Copilot Studio and add those details into the dictionary
            return ConversationRouter.TryGetValue(externalCID, out var botConversation) ?
                botConversation : await StartBotConversationAsync(externalCID, botService);
        }

        private static void OnTokenRefreshCheckEvent(object? source, ElapsedEventArgs e)
        {
            foreach (var conversation in ConversationRouter.Values)
            {
                if (DateTime.Now - conversation.LastTokenRefreshTime >=
                    TimeSpan.FromMinutes(TokenRefreshIntervalInMinute))
                {
                    // last token refresh TokenRefreshIntervalInMinute ago, refresh token
                    conversation.LastTokenRefreshTime = DateTime.Now;
                    using (var client = new DirectLineClient(conversation.Token))
                    {
                        conversation.Token = client.Tokens.RefreshToken().Token;
                    }
                }
            }
        }

        private static void OnConversationIdleCheckEvent(object? source, ElapsedEventArgs e)
        {
            foreach (var externalConversationId in ConversationRouter.Keys)
            {
                var conversation = ConversationRouter[externalConversationId];
                if (DateTime.Now - conversation.LastConversationUpdateTime >
                    TimeSpan.FromMinutes(ConversationEndAfterIdleTimeInMinute))
                {
                    // conversation inactive for > ConversationEndAfterIdleTimeInMinute, removing from s_conversationRouter
                    // If same external conversation active again, a new bot conversation will be created
                    conversation = null;
                    ConversationRouter.Remove(externalConversationId);
                }
            }
        }
    }
}