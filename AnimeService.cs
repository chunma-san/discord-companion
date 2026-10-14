using System.Net.Http.Json;
using System.Text.Json;
using System.Globalization;
using NetCord.Rest;

namespace GreatLoveImmortalVenerable;

public sealed class AnimeService(HttpClient http)
{
    private static readonly SemaphoreSlim requests = new(1, 1);
    private static DateTime lastRequest = DateTime.MinValue;
    public async Task<JsonElement> Graphql(string query, object variables)
    {
        await requests.WaitAsync();
        try {
            var delay = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - lastRequest);
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
            lastRequest = DateTime.UtcNow;
            using var response = await http.PostAsJsonAsync("https://graphql.anilist.co", new { query, variables });
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (json.RootElement.TryGetProperty("errors", out _)) throw new InvalidOperationException("AniList could not complete this search.");
            return json.RootElement.GetProperty("data").Clone();
        } finally { requests.Release(); }
    }
    public async Task<EmbedProperties> Media(string search, bool manga = false)
    {
        ValidateSearch(search);
        var data = await Graphql("query($search:String,$type:MediaType){Media(search:$search,type:$type,sort:SEARCH_MATCH){title{romaji english}siteUrl coverImage{large}averageScore status episodes chapters genres description(asHtml:false)nextAiringEpisode{episode airingAt}}}", new { search, type = manga ? "MANGA" : "ANIME" });
        var media = data.GetProperty("Media");
        if (media.ValueKind == JsonValueKind.Null) return Embeds.Build("No results found.");
        var embed = Embeds.Build(Text(media.GetProperty("title"), "romaji"), Trim(Text(media,"description"), 1500));
        embed.Url = Text(media,"siteUrl");
        var cover=Text(media.GetProperty("coverImage"),"large");
        if (!string.IsNullOrEmpty(cover)) embed.Thumbnail = new EmbedThumbnailProperties(cover);
        embed.Fields = [
            new() {Name="Score",Value=Text(media,"averageScore")+" / 100",Inline=true},
            new() {Name="Status",Value=Text(media,"status"),Inline=true},
            new() {Name=manga?"Chapters":"Episodes",Value=Text(media,manga?"chapters":"episodes"),Inline=true},
            new() {Name="Genres",Value=Trim(string.Join(", ",media.GetProperty("genres").EnumerateArray().Select(x=>x.GetString())),500)}
        ];
        return embed;
    }
    public async Task<EmbedProperties> User(string name)
    {
        ValidateSearch(name);
        var data=await Graphql("query($name:String){User(name:$name){name siteUrl avatar{large}statistics{anime{count meanScore}manga{count meanScore}}}}",new{name});
        var user=data.GetProperty("User");
        if(user.ValueKind==JsonValueKind.Null)return Embeds.Build("No user found.");
        var stats=user.GetProperty("statistics");
        return Embeds.Build(Text(user,"name"),$"[AniList profile]({Text(user,"siteUrl")})\nAnime: {Text(stats.GetProperty("anime"),"count")} · Mean score: {Text(stats.GetProperty("anime"),"meanScore")}\nManga: {Text(stats.GetProperty("manga"),"count")} · Mean score: {Text(stats.GetProperty("manga"),"meanScore")}");
    }
    public async Task<EmbedProperties> Calendar(DateOnly day)
    {
        var from=new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)).ToUnixTimeSeconds();
        var rows=new List<string>();
        for(var page=1;page<=10;page++) {
            var data=await Graphql("query($from:Int,$to:Int,$page:Int){Page(page:$page,perPage:50){pageInfo{hasNextPage}airingSchedules(airingAt_greater:$from,airingAt_lesser:$to,sort:TIME){episode airingAt media{title{romaji}siteUrl}}}}",new{from=from-1,to=from+86400,page});
            var p=data.GetProperty("Page");
            foreach(var row in p.GetProperty("airingSchedules").EnumerateArray()) {
                var media=row.GetProperty("media");
                rows.Add($"<t:{Text(row,"airingAt")}:t> · [{Text(media.GetProperty("title"),"romaji")}]({Text(media,"siteUrl")}) — Ep {Text(row,"episode")}");
            }
            if(!p.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean())break;
        }
        var displayed=new List<string>();var length=0;
        foreach(var line in rows) {if(length+line.Length>3600)break;displayed.Add(line);length+=line.Length+1;}
        var description=rows.Count==0?"No episodes scheduled.":string.Join("\n",displayed);
        if(displayed.Count<rows.Count)description+=$"\n… {rows.Count-displayed.Count} additional episodes. [Full schedule](https://anilist.co/search/anime)";
        var embed=Embeds.Build($"Anime calendar · {day:yyyy-MM-dd} (UTC day)",description);
        embed.Footer=new EmbedFooterProperties {Text=$"Date: {day:yyyy-MM-dd}"};
        return embed;
    }
    public async Task<EmbedProperties> Theme(string search)
    {
        ValidateSearch(search);
        var searchUrl="https://api.animethemes.moe/search?q="+Uri.EscapeDataString(search)+"&page[limit]=1";
        using var response=await http.GetAsync(searchUrl);response.EnsureSuccessStatusCode();
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var anime=doc.RootElement.GetProperty("search").GetProperty("anime");
        if(anime.GetArrayLength()==0)return Embeds.Build("No anime themes found.");
        var match=anime[0];var slug=Text(match,"slug");
        using var detail=await http.GetAsync("https://api.animethemes.moe/anime/"+Uri.EscapeDataString(slug)+"?include=animethemes.song,animethemes.animethemeentries.videos");
        detail.EnsureSuccessStatusCode();
        using var body=JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var lines=new List<string>();
        foreach(var theme in body.RootElement.GetProperty("anime").GetProperty("animethemes").EnumerateArray()) {
            var title=theme.TryGetProperty("song",out var song)&&song.ValueKind==JsonValueKind.Object?Text(song,"title"):"Theme";
            var video=theme.GetProperty("animethemeentries").EnumerateArray().Where(e=>!e.TryGetProperty("spoiler",out var s)||s.ValueKind!=JsonValueKind.True).SelectMany(e=>e.GetProperty("videos").EnumerateArray()).FirstOrDefault();
            if(video.ValueKind==JsonValueKind.Undefined)continue;
            lines.Add($"**{Text(theme,"slug")}** · [{title}]({Text(video,"link")})");
        }
        return Embeds.Build(Text(match,"name"),Trim(string.Join("\n",lines),3500)+$"\n[AnimeThemes page](https://animethemes.moe/anime/{slug})");
    }
    public static string Text(JsonElement value,string key) => value.TryGetProperty(key,out var p)&&p.ValueKind!=JsonValueKind.Null?p.ToString():"Unknown";
    public static string Trim(string text,int length)=>text.Length>length?text[..(length-1)]+"…":text;
    public static void ValidateSearch(string value) {if(string.IsNullOrWhiteSpace(value)||value.Length>150)throw new ArgumentException("Enter a search between 1 and 150 characters.");}
}

