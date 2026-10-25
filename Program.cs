using GreatLoveImmortalVenerable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Hosting.Services.ComponentInteractions;
using NetCord.Services.ComponentInteractions;
using NetCord.Services.ApplicationCommands;
using NetCord;

if (args.Contains("--library-test")) { LibrarySelfTests.Run(); return; }
if (args.Contains("--self-test")) { await SelfTests.Run(); return; }
if (args.Contains("--api-test")) { await SelfTests.ApiTest(); return; }
if (args.Contains("--host-test")) { await SelfTests.HostTest(); return; }
if (args.Contains("--reading-refresh-baseline")) { await ReadingSetup.RefreshKolBaseline(); return; }
if (args.Contains("--reading-api-test")) { await ReadingSetup.Inspect(); return; }
if (args.Contains("--reading-bootstrap")) { await ReadingSetup.Bootstrap(); return; }
if (args.Contains("--reading-preview")) { using var chapters = new ChapterService(BotSettings.FromEnvironment()); await chapters.Preview(); return; }
if (args.Contains("--grapheneos-test")) { using var news = new GrapheneNews(BotSettings.FromEnvironment()); await news.Test(); return; }
if (args.Contains("--grapheneos-setup")) { using var news = new GrapheneNews(BotSettings.FromEnvironment()); await news.Setup(); return; }
if(args.Contains("--cenele-bootstrap")){using var service=new ChapterService(BotSettings.FromEnvironment());try{await service.Add("Cenele • روايات عربية",ReadingKind.Novel,"https://cenele.com/",null);}catch(ArgumentException e)when(e.Message=="This source and filter are already tracked."){}return;}
if(args.Contains("--notification-test-all")){var testSettings=BotSettings.FromEnvironment();await NotificationPreview.SendReading(testSettings);using var gameTests=new GameUpdates(testSettings);await gameTests.TestAll();return;}
var builder = Host.CreateApplicationBuilder(args);
var settings = BotSettings.FromEnvironment();
settings.Validate();
builder.Configuration["Discord:Token"] = settings.Token;
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<CommandGate>();
builder.Services.AddHttpClient<AnimeService>(client => {
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("GreatLoveImmortalVenerable/1.0");
});
builder.Services.AddSingleton<HostService>();
builder.Services.AddSingleton<GrapheneNews>();
builder.Services.AddHostedService(p => p.GetRequiredService<GrapheneNews>());
builder.Services.AddHostedService<GameUpdates>();
builder.Services.AddSingleton<ChapterService>();
builder.Services.AddHostedService(p => p.GetRequiredService<ChapterService>());
builder.Services.AddDiscordGateway(options => options.Intents = GatewayIntents.Guilds)
    .AddApplicationCommands(o=>{o.AutoRegisterCommands=false;o.DefaultContexts=[InteractionContextType.Guild];o.DefaultIntegrationTypes=[ApplicationIntegrationType.GuildInstall];})
    .AddComponentInteractions<ButtonInteraction, ButtonInteractionContext>();
var host = builder.Build();
host.AddModules(typeof(Program).Assembly);
var gateway=host.Services.GetRequiredService<GatewayClient>();
var commandService=host.Services.GetRequiredService<ApplicationCommandService<ApplicationCommandContext>>();
var localCount=commandService.GetCommands().Count;
if(localCount==0)throw new InvalidOperationException("No local command definitions; registration cancelled.");
var currentBot=await gateway.Rest.GetCurrentUserAsync();
var synced=await commandService.RegisterCommandsAsync(gateway.Rest,currentBot.Id,settings.GuildId);
if(synced.Count!=localCount)throw new InvalidOperationException("Guild command registration count does not match local definitions.");
Console.WriteLine($"Registered {synced.Count} commands directly to the configured guild.");
await gateway.Rest.BulkOverwriteGlobalApplicationCommandsAsync(currentBot.Id,[]);
Console.WriteLine("Removed global copies after successful guild registration. Local command definitions preserved.");
await host.RunAsync();
