// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License
// original source code for this, is taken from https://github.com/microsoft/CopilotStudioSamples/tree/master/RelayBotSample
// some changes have been made from original


namespace GenericRelayBot.BotConnectorApp
{
    /// <summary>
    /// class for serialization/deserialization DirectLineToken
    /// </summary>
    public class DirectLineToken
    {
        /// <summary>
        /// constructor
        /// </summary>
        /// <param name="token">Directline token string</param>
        public DirectLineToken(string token)
        {
            Token = token;
        }

        public string Token { get; set; }
    }
}