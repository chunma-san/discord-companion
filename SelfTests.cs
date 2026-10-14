using System.Net;
using System.Text.Json;
using NetCord.Services.ApplicationCommands;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Hosting.Services.ComponentInteractions;
using NetCord.Services.ComponentInteractions;
using NetCord;
namespace GreatLoveImmortalVenerable;

public static class SelfTests
{
    private static void Check(bool pass,string message) {if(!pass)throw new Exception(message);Console.WriteLine("PASS "+message);}
    public static async Task Run()
    {
        ReadingSelfTests.Run();
        var settings=new BotSettings("unused",10,20,"/run/gliv/host.sock");
        Check(settings.IsOwner(10,20)&&!settings.IsOwner(11,20)&&!settings.IsOwner(10,21)&&!settings.IsOwner(10,null),"owner and guild authorization fail closed");
        foreach(var value in new[]{"","x"}) {
            try {new BotSettings(value,0,0,"x").Validate();throw new Exception("Invalid settings accepted");}
            catch(InvalidOperationException){ }
        }
        Check(CalendarUi.ParseDate(null)==DateOnly.FromDateTime(DateTime.UtcNow),"calendar defaults to UTC today");
        try {CalendarUi.ParseDate("2000-01-01");throw new Exception("Old date accepted");}catch(ArgumentException){ }
        Check(AnimeService.Trim(new string('x',5000),4000).Length==4000,"embed content is bounded");
        using var http=new HttpClient(new FixtureHandler());
        var anime=new AnimeService(http);
        Check((await anime.Media("Test anime")).Title=="Test Anime","anime lookup and embed formatting");
        Check((await anime.Media("Test manga",true)).Fields!.Any(x=>x.Name=="Chapters"),"manga uses chapter counts");
        Check((await anime.User("TestUser")).Description!.Contains("Anime: 12"),"AniList user statistics");
        Check((await anime.Calendar(DateOnly.FromDateTime(DateTime.UtcNow))).Description!.Contains("Ep 2"),"calendar follows API pagination");
        Check((await anime.Theme("Test anime")).Description!.Contains("https://v.animethemes.moe/test.webm"),"theme search resolves non-spoiler video links");
        using var broken=new HttpClient(new FixtureHandler(true));
        try {await new AnimeService(broken).Media("Test");throw new Exception("API failure was swallowed");}catch(HttpRequestException){Console.WriteLine("PASS API failure is propagated to the command error handler");}
        var builder=Host.CreateApplicationBuilder();
        builder.Configuration["Discord:Token"]="MTAwMDAwMDAwMDAwMDAwMDAwMA.fixture.unused-for-offline-validation";
        builder.Services.AddSingleton(settings);builder.Services.AddSingleton<CommandGate>();builder.Services.AddSingleton<HostService>();builder.Services.AddSingleton(anime);
        builder.Services.AddDiscordGateway().AddApplicationCommands(o=>o.AutoRegisterCommands=false).AddComponentInteractions<ButtonInteraction,ButtonInteractionContext>();
        using var host=builder.Build();host.AddModules(typeof(SelfTests).Assembly);
        Check(host.Services.GetRequiredService<NetCord.Gateway.GatewayClient>() is not null,"gateway dependencies and token configuration resolve offline");
        Check(typeof(SelfTests).Assembly.GetTypes().Count(t=>t.GetCustomAttributes(typeof(SlashCommandAttribute),false).Length>0)==9,"all nine owner command groups register without starting Discord");
        Console.WriteLine("Offline test suite passed. No Discord requests or voice control actions were sent.");
    }
    public static async Task ApiTest()
    {
        using var http=new HttpClient {Timeout=TimeSpan.FromSeconds(20)};
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GreatLoveImmortalVenerable/1.0");
        var service=new AnimeService(http);
        Check(!string.IsNullOrEmpty((await service.Media("Frieren")).Title),"live AniList anime search");
        Check(!string.IsNullOrEmpty((await service.Media("Berserk",true)).Title),"live AniList manga search");
        Check(!string.IsNullOrEmpty((await service.Calendar(DateOnly.FromDateTime(DateTime.UtcNow))).Title),"live airing calendar");
        Check((await service.Theme("Frieren")).Description!.Contains("https://v.animethemes.moe/"),"live AnimeThemes video search");
        Console.WriteLine("External API checks passed; no Discord token required.");
    }
    public static async Task HostTest()
    {
        var host=new HostService(new BotSettings("unused",1,1,"/run/gliv/host.sock"));
        foreach(var action in new[]{"server","uptime","disk","network","docker","containers","voice-timer","teamspeak","navidrome","immich"}) {
            var data=await host.Request(action);
            Check(data.ValueKind is JsonValueKind.Object or JsonValueKind.Array,"unprivileged host bridge: "+action);
        }
        foreach(var service in Enum.GetNames<AllowedService>())await host.Request("service",service);
        Console.WriteLine("All allowed service lookups passed. No TeamSpeak start/stop actions were sent.");
    }
}
file sealed class FixtureHandler(bool fail=false):HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
    {
        if(fail)return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        object response;
        if(request.Method==HttpMethod.Get) {
            response=request.RequestUri!.AbsolutePath=="/search"
                ?new{search=new{anime=new[]{new{name="Test Anime",slug="test-anime"}}}}
                :new{anime=new{animethemes=new[]{new{slug="OP1",song=new{title="Test song"},animethemeentries=new[]{new{spoiler=false,videos=new[]{new{link="https://v.animethemes.moe/test.webm"}}}}}}}};
        } else {
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var query=body.RootElement.GetProperty("query").GetString()!;
            if(query.Contains("airingSchedules")) {
                var page=body.RootElement.GetProperty("variables").GetProperty("page").GetInt32();
                response=new{data=new{Page=new{pageInfo=new{hasNextPage=page==1},airingSchedules=new[]{new{episode=page,airingAt=1000,media=new{title=new{romaji="Test Anime"},siteUrl="https://anilist.co/anime/1"}}}}}};
            } else if(query.Contains("User(")) {
                response=new{data=new{User=new{name="TestUser",siteUrl="https://anilist.co/user/test",statistics=new{anime=new{count=12,meanScore=80},manga=new{count=3,meanScore=70}}}}};
            } else {
                response=new{data=new{Media=new{title=new{romaji="Test Anime"},siteUrl="https://anilist.co/anime/1",coverImage=new{large="https://example.org/cover.jpg"},averageScore=80,status="FINISHED",episodes=12,chapters=100,genres=new[]{"Action"},description="Test description"}}};
            }
        }
        return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(response))};
    }
}
