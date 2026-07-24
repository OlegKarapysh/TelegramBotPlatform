namespace TelegramBotPlatform.WebApi;

public static class ServiceInstaller
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddPlatformMessaging()
        {
            return services.AddMassTransit(config =>
            {
                config.SetKebabCaseEndpointNameFormatter();

                // The platform's only consumer is BotUpdateRouter: it resolves each update's bot behavior
                // and dispatches to it. A behavior that itself uses the bus (e.g. its own commands) would
                // add its consumers here too.
                config.AddConsumers(typeof(BotUpdateRouter).Assembly);

                config.UsingInMemory((context, configurator) =>
                {
                    // Sets the bot for the current message BEFORE its consumer (and any constructor-injected,
                    // bot-scoped ITelegramBotClient) is resolved from the DI scope — see BotScopeFilter.
                    configurator.UseBotScopeFilter(context);
                    configurator.ConfigureEndpoints(context);
                });
            });
        }
    }
}