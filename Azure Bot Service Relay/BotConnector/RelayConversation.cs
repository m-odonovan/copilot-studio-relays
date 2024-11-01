// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
// original source code for this, is taken from https://github.com/microsoft/CopilotStudioSamples/tree/master/RelayBotSample
// some changes have been made from original


using System;

namespace GenericRelayBot.BotConnectorApp
{
    /// <summary>
    /// Data model class for Power Virtual Agent conversation
    /// </summary>
    public class RelayConversation
    {
        public string? ConversationtId { get; set; }

        public string? WaterMark { get; set; }

        public string? Token { get; set; }

        public DateTime LastTokenRefreshTime { get; set; } = DateTime.Now;

        public DateTime LastConversationUpdateTime { get; set; } = DateTime.Now;
    }
}