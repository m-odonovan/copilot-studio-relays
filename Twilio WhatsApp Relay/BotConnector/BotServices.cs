// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Rest.Serialization;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace WhatsAppRelay.BotConnectorApp
{
   /// <summary>
   /// Bot Service class to interact with bot
   /// </summary>
   public class BotService
   {
       private static readonly HttpClient s_httpClient = new HttpClient();

       public string? BotName { get; set; }

       public string? BotId { get; set; }

       public string? TenantId { get; set; }

       public string? TokenEndPoint { get; set; }
       
       public string BotLocation { get; set; } = string.Empty;


   public string GetBotBaseUri()
        {
            return string.IsNullOrEmpty(BotLocation) ? "https://directline.botframework.com" : $"https://{BotLocation.ToLower()}.directline.botframework.com";
        }
        /// <summary>
        /// Get directline token for connecting bot
        /// </summary>
        /// <returns>directline token as string</returns>
        public async Task<string> GetTokenAsync()
        {
            string token;
            using (var httpRequest = new HttpRequestMessage())
            {
                httpRequest.Method = HttpMethod.Get;
                UriBuilder uriBuilder = new UriBuilder(TokenEndPoint);

                uriBuilder.Query = $"api-version=2022-03-01-preview&botId={BotId}&tenantId={TenantId}";
                httpRequest.RequestUri = uriBuilder.Uri;
                using (var response = await s_httpClient.SendAsync(httpRequest))
                {
                    var responseString = await response.Content.ReadAsStringAsync();
                    token = SafeJsonConvert.DeserializeObject<DirectLineToken>(responseString).Token;
                }
            }

            return token;
        }
   }
}
