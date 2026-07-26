var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddHealthChecks();

builder.Services.AddPlatformMessaging();
builder.Services.AddPlatformModule();

// Built-in behaviors are composed here in the host. Register each as a service so it can be resolved
// and added to the behavior catalog at startup (below).
builder.Services.AddSingleton<EchoBehavior>();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

var app = builder.Build();

if (args.Contains("migrate"))
{
    await app.Migrate();

    return;
}

// Runs before the host starts serving traffic/receivers, so every bot's assigned behavior is already
// present by the time BotUpdateRouter looks it up.
app.RegisterBehaviors();

app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health");

app.MapAdminApi();
app.MapBotWebhook();

await app.RunAsync();