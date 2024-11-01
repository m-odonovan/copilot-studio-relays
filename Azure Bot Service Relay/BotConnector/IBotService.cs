// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
// original source code for this, is taken from https://github.com/microsoft/CopilotStudioSamples/tree/master/RelayBotSample
// some changes have been made from original


using System.Threading.Tasks;

namespace GenericRelayBot.BotConnectorApp
{
    public interface IBotService
    {
        string GetBotName();

        string GetBotBaseUri();

        Task<string> GetTokenAsync();
    }
}