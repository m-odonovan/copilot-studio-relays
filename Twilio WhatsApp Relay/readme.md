# Twilio WhatsApp Relay Service
This is a relay service, which relays incoming messages from Twilio (WhatsApp user), and a Copilot Studio agent. It is designed to be hosted as an Azure Web App Service.
The original code for this, comes from the following repository -> https://www.twilio.com/en-us/blog/add-whatsapp-channel-power-virtual-agents-bot-twilio
I have modified that code slightly, as it didnt work well as documented. Some example of changes: 

1. When Twilio sends an HTTP messages to the relay, it expects an HTTP response with 15 seconds, otherwise it fails. Therefore, I changed the code so that a response is instant, and then asyncronously call Copilot Studio agent, and based on this response, call Twilio as a seperate HTTP call. So, if the Copilot Studio response takes a long time, it still works.
2. Use the Twilio SDK to call Twilio HTTP endpoint.
3. Added security, so that the relay service only accepts connections from Twilio

# Some notes / learnings from the solution:

###The Twilio API nuances, TwiML and order of messages
https://www.twilio.com/docs/messaging/twiml

- Order of messages to end user -> if you send 2 messages e.g. agent sends 2 messages one after each other, its possible that the second arrives before the first (out of sequence). This is becuase of message sizes and internet delivery. To work around this you could either have a pause between sending messages, or in your topic try and group messages together, instead of one after each other. You need to be a bit clever here.
- Size of messages -> there is a max message size of 1600 characters, so if the response of a message is larger than this, you need to split it into two messages. This sample does do this, but it could be smarter.
- Message templates and buttons -> this sample doesnt use message templates, but it could if needed. Future enhancement.

### Future - change the code which calls Direct Line to use the "new" M365 Copilot SDK

At the time of coding this this new SDK didn't support S2S authentication, but in the future it will. This SDK seems more robust and should be used instead when calling Copilot Studio (Direct Line). Something similar to this example -> https://github.com/microsoft/Agents/blob/main/samples/basic/copilotstudio-client/dotnet.
Particularly interesting is how it appears you dont need to poll to see for return messages from Direct Line, but rather its a websocket which receives messages as they arrive. You will notice in this sample it has a setting of how max times it should check for messages, and the interval between the polling in MS.

TODO: the code is currently forcing a region of directline to be Europe. I must change this to be more like the Azure Bot Service relay example, and make this a setting for the region. If you region is not Europe for your Copilot Studio agent, then you must for now just edit the code. Its commented for clarity.

### launchSettings.json

This is for local development, it defines how the local web server will be launched, the security settings for it, the ports used and the mode of the application e.g. Development. This mode is used by other settings configuration to determien which app settings to use. i.e. appsettings.json is always used, but it will also use appsettings.Development.json, if the development mode in launchSettings is set to Development.

### Local Development

To run locally you can either start from VS Code / Visual Studio, and use break points to debug, or you can build via command line and run e..g "dotnet build" will just compile the solution, or "dotnet watch run" will compile and run, but also dynamicaly recompile if you change code.

### Publish to Azure App Service

 - First you should compile the service for release and not debug i.e. command line is "dotnet build -c release". This will compile what is need in app service to bin/ver/release/
 - Recommendation: If you use publish instead of build, it will create a publish folder and add compiled solution into there, along with required .Net framework files for self contained exection "dotnet publish -c release -0 ./publish". In my case the only difference was it added the startup.html page also. But this is good. This publish folder is what can be deployed to Azur App Service

I used VS Code, added the Azure App Service Extension, and then chose to publish to App Service, and chose the publish folder created in step above. This ensures only the publish files are uploaded to the web app

### Environment variables in Azure App Service

There are several settings which the application uses, which are set in the appsettings.json / appsettings.development.json / appsettings.production.json. When hosted in Azure App service, you can still use these files, but better practise to set them in the environment settings part of the Azure App service.

Here is list of settings, as they would appear in the appsettings file:

- "BotId" - Copilot Studio bot id (get from URL of bot in designer)
- "BotTenantId" - get from Copilot Studio settings for agent/bot
- "BotName":" friendly name of Copilot Studio agent / bot
- "BotTokenEndPoint": directline endpoint e.g "https://example.5b.environment.api.powerplatform.com/powervirtualagents/botsbyschema/cr245_example/directline/token?api-version=2022-03-01-preview"
- "Twilio:AccountSid": This comes from Twilio service
- "Twilio:AuthToken": This comes from Twilio service
- "Twilio:FromNumber": This is the Twilio telephone number, in the form of "+1....."

Local Development Debugging
You can use NGROK or Azure Dev Tunnel to point the Messaging EndPoint in Twilio to your localhost dev server, and use debugging, breakpoints etc. In Twilio you would configure the endpoint to be the hostname and port Dev Tunnel gives you. Do note, if you do this, you will need to comment out the security validation section where the code only accepts connections from Twilio, as this will not work in this context.


### Enable logging in web app

If you edit web.config in app service, you can set stdoutLogEnabled="true", this will send all output in the app service e.g. console.writeline and log output to these log files

