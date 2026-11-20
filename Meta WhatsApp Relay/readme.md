# Meta Direct API WhatsApp Relay Service
This service relays incoming messages between Meta (WhatsApp users) and a Copilot Studio standard harness agent. It is designed to be hosted in Azure App Service.

This is a sample, provided "as-is". It hasn't been fully tested and needs some adjustments to make it ready for large-scale enterprise use. Some suggested adjustments are documented below.

This is not ready yet; it is still a work in progress. It needs work around queuing and debugging.

# Architecture

- This service listens for incoming webhook messages from Meta.
- For each JSON payload received, it takes the messages in the JSON and adds them to a queue for processing.
- A separate worker reads messages from this queue and sends each message to the Copilot Studio agent using the Direct Line API.
- The worker then polls the Copilot Studio agent using the Direct Line API to check for responses. If there is a response, it sends it to the user via the Meta API.
- The service maintains sender state (sender, conversation ID, token, etc.) so that subsequent requests from the sender use the same conversation.
- The queue supports message deduplication. For example, if Meta sends the same message several times (which it can for several reasons), we don't process the message again.
- It currently supports text messages only, both received from and sent to users, but it has placeholder logic for other message types and message templates. Buttons appear as text options for now.
- If a message's text is too large to send to Meta in one go, the logic splits it into multiple messages and sends each one.

# Some Notes and Learnings from the Solution

### Interesting Nuances

- Messages to WhatsApp users may arrive out of order. If you send two messages to Meta, it's possible that the second arrives before the first. This is because of message sizes and internet delivery. To work around this, you would need to either edit the code to introduce a pause between sending messages to Meta or group messages into a single message in your Copilot Studio topic. For example, instead of three Send a message nodes directly after each other, group them into a single message.

### Potential Enhancements

- The queue is not durable. Shutdowns, crashes, restarts, and deployments can lose queued or in-progress messages. It's an in-memory queue only. In the future, you could consider moving this to a durable queue, such as Azure Service Bus or a database table. This would also support scaling this application across several instances. Today, it only supports a single instance, as the queue and previously seen message IDs are held only in the running instance's memory.
- The code uses the Direct Line API. Ideally, this should be changed to use the Microsoft 365 Agents SDK, as this is the future for calling Copilot Studio agents. However, as of today, the Microsoft 365 Agents SDK does not support S2S authentication. When you read this, it might, and you may want to change the code, for example, to use the Copilot Studio client object to connect to Copilot Studio.
- Even when using the Direct Line API, you could consider using a WebSocket connection, which the code doesn't do today. A WebSocket remains open, so you don't need to poll for messages from Copilot Studio. However, there are considerations, such as how many connections the architecture would support. If there are 2,000 users, you could end up with 2,000 connections. Can Copilot Studio support the resulting message delivery rate and number of connections? You would need to consider connection timeouts so that connections don't stay open too long.
- If the code remains "as-is", the polling architecture has some issues and concerns. It polls for messages for a configured duration. If it doesn't receive a message from Copilot Studio within this period, the user will not see the message until they send another message later. If Copilot Studio responds with two messages right after each other, for example, from two Send a message nodes in a topic, the polling logic may see the first, send it, and stop processing. You could change the logic to continue polling after seeing the first message, but this could keep the worker busy for longer and delay other messages. Therefore, at this stage, avoid sending messages from Copilot Studio in this way. Send one message to the user in response to one message from the user.
- Only one message is handled by the worker at a time. It reads a message from the queue, sends it to Copilot Studio, and polls for a response. Only once it has finished can it process the next message in the queue. You could consider an architecture with multiple workers so that several messages can be processed in parallel. You need to consider the load on the Copilot Studio Direct Line API and the rate of responses sent to Meta to understand whether this will be an issue.

# Setup and Configuration

## Meta

You will need to follow Meta's guidance to create a WhatsApp app in the [Meta developer portal](https://developers.facebook.com/). There, you will get an access token and configure the webhook URL to point to the hosted App Service (this solution). Set the callback URL to something like `https://<hosted-endpoint>/WhatsAppRelay/messagerelay`, replacing `<hosted-endpoint>` with your hostname. This could be a local dev tunnel if you're building or testing the service on your PC, or a public endpoint in Azure App Service.

You will also add your own verification token (a complex string you create). Add this same string to the WhatsApp Relay Service configuration.

### Local Development

To run locally, you can start from VS Code or Visual Studio and use breakpoints to debug, or build and run from the command line. For example, `dotnet build` compiles the solution, while `dotnet watch run` compiles and runs it, and dynamically recompiles when you change the code.

When launched in development mode, it should open the Swagger test page so that you can test using the Swagger web form.

If you're running locally and want Meta to call the service on your PC, you will need to use a dev tunnel or something equivalent. For example:

```powershell
devtunnel host -p 7016 --protocol https --allow-anonymous
```

This returns a hostname you can use in the webhook configuration in the Meta developer portal.

#### [launchSettings.json](Properties/launchSettings.json)

This file is for local development. It defines how the local web server will be launched, its security settings, the ports used, and the application's environment, such as `Development`. This environment determines which app settings are used. [appsettings.json](appsettings.json) is always used, but [appsettings.Development.json](appsettings.Development.json) is also used if the environment is set to `Development`. When hosted in Azure App Service, ensure the `ASPNETCORE_ENVIRONMENT` environment variable is set to `Production` so that the solution runs in production mode rather than development mode.

### Publish to Azure App Service

- First, compile the service in Release rather than Debug mode using `dotnet build -c Release`. The compiled output is placed under `bin\Release\`.
- Recommendation: use `dotnet publish -c Release -o .\publish` to create a publish folder containing the compiled application and its dependencies. In my case, publishing also added the `startup.html` file. This publish folder can be deployed to Azure App Service.

I used VS Code, added the Azure App Service extension, chose to publish to App Service, and selected the publish folder created in the previous step. This ensures that only the published files are uploaded to the web app.

### Environment Variables: Local vs. Azure App Service

Application settings can be set in [appsettings.json](appsettings.json), [appsettings.Development.json](appsettings.Development.json), or an environment-specific file such as `appsettings.Production.json`. When hosted in Azure App Service, you can still use these files, but it is better practice to configure these values in the environment settings in Azure App Service.

Note: a sample configuration is provided in [appsettings.json.example](appsettings.json.example). You will need to edit its contents and rename it to `appsettings.json`.

Here is a list of settings as they would appear in the app settings file. All are required:

- `BotId` - Copilot Studio bot ID (get this from the bot's URL in the designer).
- `BotTenantId` - get this from the Copilot Studio settings for the agent or bot.
- `BotName` - friendly name of the Copilot Studio agent or bot.
- `BotLocation` - leave empty for the US; otherwise, set to `europe` or `india`. This is used for the Direct Line URL.
- `BotTokenEndPoint` - Direct Line token endpoint; see this [example endpoint](https://example.5b.environment.api.powerplatform.com/powervirtualagents/botsbyschema/cr245_example/directline/token?api-version=2022-03-01-preview).
- `Meta:GraphVersion` - version of the Meta API to use, for example, `v25.0`.
- `Meta:AccessToken` - get this from the app setup in Meta.
- `Meta:AppSecret` - get this from the app setup in Meta.
- `Meta:VerifyToken` - complex string you added to the webhook settings.
- `Meta:PhoneNumberId` - phone number ID from Meta, not the actual phone number.
- `Meta:WabaId` - WhatsApp ID from Meta.

### Enable Logging in the Web App

The logging level is configured in [appsettings.json](appsettings.json). You can change the level of logging output using standard ASP.NET Core logging.

### References

- [WhatsApp relay sample for Copilot Studio](https://github.com/microsoft/CopilotStudioSamples/tree/adilei-whatsapp-upstream-pr/extensibility/agents-sdk/whatsapp-relay), where each user authenticates through WhatsApp as themselves.
- [Meta developer documentation for sending and receiving messages](https://developers.facebook.com/documentation/business-messaging/whatsapp/get-started#step-3-send-and-receive-messages).
- [Meta webhook documentation](https://developers.facebook.com/documentation/business-messaging/whatsapp/webhooks/overview).