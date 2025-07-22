# Twilio WhatsApp Relay Service
This is a relay service, which relays incoming messages between Twilio (WhatsApp user), and a Copilot Studio agent. It is designed to be hosted as an Azure Web App Service.
The original code for this, comes from the following repository -> https://www.twilio.com/en-us/blog/add-whatsapp-channel-power-virtual-agents-bot-twilio
I have modified that code slightly, as it didn't work well for several scenario. Here are some example of changes which were made: 

1. When Twilio sends an HTTP messages to the relay, it expects a response to its call within 15 seconds, otherwise it fails. A Copilot Studio agent response can sometimes take longer than this. Therefore, I changed the code so that the relay responds instantly and asyncronously calls the Copilot Studio agent (using Direct Line API). When a response is received from the agent, the relay posts the response message to Twilio, using the Twilio SDK.
2. Added security, so that the relay service only accepts connections from Twilio.

# Some notes / learnings from the solution:

### Twilio API nuances

- Size of messages -> there is a max message size of 1600 characters, so if the response of a message is larger than this, you need to split it into two messages. This sample does do this, but it could be smarter.
- Messages to WhatsApp user may arrive out of order -> if you send 2 messages to Twilio, its possible that the second arrives before the first (out of sequence). This is becuase of message sizes and internet delivery. To work around this, you would need to either edit the code, and have some type of pause between sending messages to Twilio, or in your Copilot Studio topic try and group messages together into a single message e.g. instead of 3 send message blocks directly underneath each other, group them into a single message.
- Message templates and buttons -> this sample doesn't use WhatsApp message templates, it only supports plain text messages. This could be a future enhancement. Therefore adaptive cards don't render as designed in WhatsApp, and buttons appears as plaint text when using a question nodes with options.

### Future - change the code which calls Direct Line to use the "new" M365 Copilot SDK

At the time of coding this this, the new M365 Copilot SDK didn't support S2S authentication, but in the future it will. This SDK seems more robust and should be used instead when calling Copilot Studio (Direct Line). Something similar to this example -> https://github.com/microsoft/Agents/blob/main/samples/basic/copilotstudio-client/dotnet.
Particularly interesting is how it appears you don't need to poll to check for messages from Direct Line, but rather it uses a websocket which receives messages as they arrive. You will notice in my sample it polls for messages from Direct Line, and uses a variable for how how max times it should check for messages, and the interval between the polling in, in milliseconds.

### launchSettings.json

This is for local development, it defines how the local web server will be launched, the security settings for it, the ports used and the mode of the application e.g. Development. This mode is used by other settings configuration to determien which app settings to use. i.e. appsettings.json is always used, but it will also use appsettings.Development.json, if the development mode in launchSettings is set to Development.

### Local Development

To run locally you can either start from VS Code / Visual Studio, and use break points to debug, or you can build via command line and run e..g "dotnet build" will just compile the solution, or "dotnet watch run" will compile and run, but also dynamicaly recompile if you change code.

When launched in development mode, it should launch the swagger test page, so you can test using swagger web form, like shown below.

![Swagger](https://github.com/m-odonovan/copilot-studio-relays/blob/main/Twilio%20WhatsApp%20Relay/images/swagger.png?raw=true "Swagger")

Note format of from paramter value, using whatsapp keyword in before the telephone number

### Publish to Azure App Service

 - First you should compile the service for release and not debug i.e. command line is "dotnet build -c release". This will compile what is need in app service to bin/ver/release/
 - Recommendation: If you use publish instead of build, it will create a publish folder and add compiled solution into there, along with required .Net framework files for self contained exection "dotnet publish -c release -0 ./publish". In my case the only difference was it added the startup.html page also. But this is good. This publish folder is what can be deployed to Azur App Service

I used VS Code, added the Azure App Service Extension, and then chose to publish to App Service, and chose the publish folder created in step above. This ensures only the publish files are uploaded to the web app

### Twilio Config

In the Twilio dashboard, you need to tell Twlio to forward requests to your web app i.e. something like the below. This could also be to your localhost deployment if you are using a devtunnel. If using devtunne;, you would change the hostname to the devtunnel host name.

![Twilio Config](https://github.com/m-odonovan/copilot-studio-relays/blob/main/Twilio%20WhatsApp%20Relay/images/TwilioConfig.png?raw=true "Twilio Config")

Local Development Debugging
You can use NGROK or Azure Dev Tunnel to point the Messaging EndPoint in Twilio to your localhost dev server, and use debugging, breakpoints etc. In Twilio you would configure the endpoint to be the hostname and port Dev Tunnel gives you.

### Environment variables in Azure App Service

There are several settings which the application uses, which are set in the appsettings.json / appsettings.development.json / appsettings.production.json. When hosted in Azure App service, you can still use these files, but better practise to set them in the environment settings part of the Azure App service.

Here is list of settings, as they would appear in the appsettings file:

- "BotId" - Copilot Studio bot id (get from URL of bot in designer)
- "BotTenantId" - get from Copilot Studio settings for agent/bot
- "BotName" - friendly name of Copilot Studio agent / bot
- "BotLocation" - leave empty if US, otherwise set to "europe", or "india". This is for the DirectLine URL
- "BotTokenEndPoint" - directline endpoint e.g "https://example.5b.environment.api.powerplatform.com/powervirtualagents/botsbyschema/cr245_example/directline/token?api-version=2022-03-01-preview"
- "Twilio:AccountSid"- This comes from Twilio service
- "Twilio:AuthToken" - This comes from Twilio service
- "Twilio:FromNumber" - This is the Twilio telephone number, in the form of "+1....."


### Enable logging in web app

If you edit web.config in app service, you can set stdoutLogEnabled="true", this will send all output in the app service e.g. console.writeline and log output to these log files

