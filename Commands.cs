using System.Collections.Concurrent;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace GreatLoveImmortalVenerable;

public sealed class CommandGate(BotSettings settings)
{
    private readonly ConcurrentDictionary<ulong,DateTime> recent=new();
    public async Task<bool> Begin(ApplicationCommandContext context,bool owner=false)
    {
        if(context.Interaction.GuildId!=settings.GuildId || (owner&&!settings.IsOwner(context.User.Id,context.Interaction.GuildId))) {
            await context.Interaction.SendResponseAsync(InteractionCallback.Message(new InteractionMessageProperties {Content="This command is unavailable here or requires the bot owner.",Flags=MessageFlags.Ephemeral}));return false;
        }
        var now=DateTime.UtcNow;
        if(recent.TryGetValue(context.User.Id,out var last)&&now-last<TimeSpan.FromSeconds(2)) {
            await context.Interaction.SendResponseAsync(InteractionCallback.Message(new InteractionMessageProperties {Content="Please wait two seconds between commands.",Flags=MessageFlags.Ephemeral}));return false;
        }
        recent[context.User.Id]=now;
        if(recent.Count>1000)foreach(var pair in recent.Where(x=>now-x.Value>TimeSpan.FromMinutes(5)))recent.TryRemove(pair.Key,out _);
        await context.Interaction.SendResponseAsync(InteractionCallback.DeferredMessage(owner?MessageFlags.Ephemeral:default));
        return true;
    }
    public async Task Execute(ApplicationCommandContext context,Func<Task<EmbedProperties>> action,bool owner=false)
    {
        if(!await Begin(context,owner))return;
        try {
            var embed=await action();
            await context.Interaction.ModifyResponseAsync(p=>p.Embeds=[embed]);
        } catch(Exception e) {
            Console.Error.WriteLine($"Command failed: {e.GetType().Name}");
            await context.Interaction.ModifyResponseAsync(p=>p.Content="The request could not be completed. Check the service connection or try again later.");
        }
    }
}

public sealed class AnimeCommands(AnimeService anime,CommandGate gate):ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("anime","Search anime on AniList")]
    public Task Anime([SlashCommandParameter(Description="Anime title")]string query)=>gate.Execute(Context,()=>anime.Media(query));
    [SlashCommand("manga","Search manga on AniList")]
    public Task Manga([SlashCommandParameter(Description="Manga title")]string query)=>gate.Execute(Context,()=>anime.Media(query,true));
    [SlashCommand("anilist","Look up an AniList user")]
    public Task User([SlashCommandParameter(Description="AniList username")]string name)=>gate.Execute(Context,()=>anime.User(name));
    [SlashCommand("theme","Find anime openings and endings")]
    public Task Theme([SlashCommandParameter(Description="Anime title")]string query)=>gate.Execute(Context,()=>anime.Theme(query));
    [SlashCommand("calendar","Browse the anime airing calendar")]
    public async Task Calendar([SlashCommandParameter(Description="UTC date as YYYY-MM-DD; blank for today")]string? date=null)
    {
        if(!await gate.Begin(Context))return;
        try {
            var day=CalendarUi.ParseDate(date);
            var embed=await anime.Calendar(day);
            await Context.Interaction.ModifyResponseAsync(p=>{p.Embeds=[embed];p.Components=[CalendarUi.Buttons()];});
        }catch(Exception e){Console.Error.WriteLine($"Calendar failed: {e.GetType().Name}");await Context.Interaction.ModifyResponseAsync(p=>p.Content="Use YYYY-MM-DD within 30 days of today. If the date is valid, AniList may be temporarily unavailable.");}
    }
}

public sealed class UtilityCommands(CommandGate gate):ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("ping","Check that the bot responds")]
    public Task Ping()=>gate.Execute(Context,()=>Task.FromResult(Embeds.Build("Pong!","Great Love Immortal Venerable is responding.")));
    [SlashCommand("choose","Choose one item from a comma-separated list")]
    public Task Choose([SlashCommandParameter(Description="Two to twenty choices separated by commas")]string options)=>gate.Execute(Context,()=>{
        var choices=options.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        if(choices.Length<2||choices.Length>20||options.Length>1000)throw new ArgumentException("Provide 2–20 choices, up to 1000 characters.");
        return Task.FromResult(Embeds.Build("My choice",choices[Random.Shared.Next(choices.Length)]));
    });
    [SlashCommand("help","See the available commands")]
    public Task Help()=>gate.Execute(Context,()=>Task.FromResult(Embeds.Build("Great Love Immortal Venerable",
        "**Anime**\n`/anime` · `/manga` · `/anilist` · `/calendar` · `/theme`\n**Saved library (owner only)**\n/library index|browse|search|item|recommended|export\n\n**Reading updates (owner only)**\n/chapters add|list|remove|latest|check\n**Utilities**\n`/ping` · `/choose` · `/help`\n**Owner only · private replies**\n`/server status|uptime|disk|network`\n`/docker status|containers` · `/service status`\n`/voice-timer status|start|stop` · `/teamspeak status`\n`/navidrome status` · `/immich status`\nVoice timer start/stop controls the configured announcement timer; it does not turn TeamSpeak bots on/off.")));
}

[SlashCommand("server","Owner-only home server information",DefaultGuildPermissions=Permissions.Administrator)]
public sealed class ServerCommands(HostService host,CommandGate gate):ApplicationCommandModule<ApplicationCommandContext>
{
    [SubSlashCommand("status","CPU, memory and health overview")]public Task Status()=>gate.Execute(Context,()=>host.Status("server"),true);
    [SubSlashCommand("uptime","Time since boot")]public Task Uptime()=>gate.Execute(Context,()=>host.Status("uptime"),true);
    [SubSlashCommand("disk","Storage mount and space information")]public Task Disk()=>gate.Execute(Context,()=>host.Status("disk"),true);
    [SubSlashCommand("network","Ethernet and private connection state")]public Task Network()=>gate.Execute(Context,()=>host.Status("network"),true);
}
[SlashCommand("docker","Owner-only container information",DefaultGuildPermissions=Permissions.Administrator)]
public sealed class DockerCommands(HostService host,CommandGate gate):ApplicationCommandModule<ApplicationCommandContext>
{
    [SubSlashCommand("status","Count running and stopped containers")]public Task Status()=>gate.Execute(Context,()=>host.Status("docker"),true);
    [SubSlashCommand("containers","List container states")]public Task Containers()=>gate.Execute(Context,()=>host.Status("containers"),true);
}
public enum AllowedService { Docker,BigBrain,Aion2,Tailscale,MusicDownloader,TelegramMusic,TeamSpeakControl,TeamSpeakTimer }
[SlashCommand("service","Owner-only service information",DefaultGuildPermissions=Permissions.Administrator)]
public sealed class ServiceCommands(HostService host,CommandGate gate):ApplicationCommandModule<ApplicationCommandContext>
{
    [SubSlashCommand("status","Check an allowed service")]
    public Task Status(AllowedService service)=>gate.Execute(Context,()=>host.Status("service",service.ToString()),true);
}
[SlashCommand("voice-timer","Control a configured voice announcement timer",DefaultGuildPermissions=Permissions.Administrator)]
public sealed class VoiceTimerCommands(HostService host,CommandGate gate):ApplicationCommandModule<ApplicationCommandContext>
{
    [SubSlashCommand("status","Timer service and TeamSpeak bot states")]public Task Status()=>gate.Execute(Context,()=>host.Status("voice-timer"),true);
    [SubSlashCommand("start","Start jungle announcements; TeamSpeak bots must already be online")]
    public Task Start()=>gate.Execute(Context,()=>host.Status("teamspeak-start"),true);
    [SubSlashCommand("stop","Stop jungle announcements; TeamSpeak bots stay unchanged")]
    public Task Stop()=>gate.Execute(Context,()=>host.Status("teamspeak-stop"),true);
}
[SlashCommand("teamspeak","TeamSpeak bot information",DefaultGuildPermissions=Permissions.Administrator)]
public sealed class TeamSpeakCommands(HostService host,CommandGate gate):ApplicationCommandModule<ApplicationCommandContext>
{[SubSlashCommand("status","Check configured TeamSpeak bots")]public Task Status()=>gate.Execute(Context,()=>host.Status("teamspeak"),true);}
[SlashCommand("navidrome","Music library service information",DefaultGuildPermissions=Permissions.Administrator)]
public sealed class NavidromeCommands(HostService host,CommandGate gate):ApplicationCommandModule<ApplicationCommandContext>
{[SubSlashCommand("status","Check Navidrome")]public Task Status()=>gate.Execute(Context,()=>host.Status("navidrome"),true);}
[SlashCommand("immich","Photo service information",DefaultGuildPermissions=Permissions.Administrator)]
public sealed class ImmichCommands(HostService host,CommandGate gate):ApplicationCommandModule<ApplicationCommandContext>
{[SubSlashCommand("status","Check Immich")]public Task Status()=>gate.Execute(Context,()=>host.Status("immich"),true);}

