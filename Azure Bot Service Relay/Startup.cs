// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using GenericRelayBot.BotConnectorApp.Bots;
using Microsoft.AspNetCore.Mvc;




namespace GenericRelayBot.BotConnectorApp
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
           // services.AddMvc().SetCompatibilityVersion(CompatibilityVersion.Version_2_1);

           services.AddHttpClient().AddControllers();

           // Register Bot Framework Authentication

            // Create the Bot Framework Authentication to be used with the Bot Adapter.
            services.AddSingleton<BotFrameworkAuthentication, ConfigurationBotFrameworkAuthentication>();


            // Create the Bot Framework Adapter with error handling enabled.
            services.AddSingleton<IBotFrameworkHttpAdapter, AdapterWithErrorHandler>();

            // Create the bot as a transient. In this case the ASP Controller is expecting an IBot.
            services.AddSingleton<IBot, RelayBot>();

            // Create the singleton instance of BotService from CopilotStudio appsettings
            var botService = new BotService();
            Configuration.Bind("CopilotStudio", (object)botService);
            services.AddSingleton<IBotService>(botService);

            // Create the singleton instance of ConversationPool from appsettings
            var conversationManager = new ConversationManager();
            Configuration.Bind("ConversationPool", conversationManager);
            services.AddSingleton(conversationManager);

            
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
          
            Console.WriteLine(env.EnvironmentName);

            if (env.IsDevelopment())
            {
                Console.Write("Development mode");
                app.UseDeveloperExceptionPage();
            }
            else
            {
                 Console.Write("Not development mode");
                app.UseHsts();
                //for prod turn this on
                // app.UseHttpsRedirection();
            }
     

           app.UseDefaultFiles()
                .UseStaticFiles()
                .UseWebSockets()
                .UseRouting()
                .UseAuthorization()
                .UseEndpoints(endpoints =>
                {
                    endpoints.MapControllers();
                });

            //for prod turn this on
             // app.UseHttpsRedirection();
        }
    }
}