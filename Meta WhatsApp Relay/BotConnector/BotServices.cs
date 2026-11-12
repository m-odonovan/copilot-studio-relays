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
        public async Task<(string Token, DateTimeOffset ExpiresAtUtc)> GetTokenAsync()
        {
            
       if (string.IsNullOrWhiteSpace(TokenEndPoint))
        throw new InvalidOperationException("Bot token endpoint is missing.");

    var uriBuilder = new UriBuilder(TokenEndPoint)
    {
        Query = $"api-version=2022-03-01-preview&botId={BotId}&tenantId={TenantId}"
    };

    using var request = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);

    // Start the lifetime clock before the request to avoid overstating validity.
    var requestedAtUtc = DateTimeOffset.UtcNow;

    using var response = await s_httpClient.SendAsync(request);
    response.EnsureSuccessStatusCode();

    var json = await response.Content.ReadAsStringAsync();
    var result = SafeJsonConvert.DeserializeObject<DirectLineToken>(json);

    if (result is null ||
        string.IsNullOrWhiteSpace(result.Token) ||
        result.ExpiresIn <= 0)
    {
        throw new InvalidOperationException(
            "Bot token response must contain a token and positive expires_in.");
    }

    var expiresAtUtc = requestedAtUtc.AddSeconds(result.ExpiresIn);

    if (expiresAtUtc <= DateTimeOffset.UtcNow)
        throw new InvalidOperationException("Received an already-expired bot token.");

    return (result.Token, expiresAtUtc);
        }
   }
}
