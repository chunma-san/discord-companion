using HtmlAgilityPack;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GreatLoveImmortalVenerable;

public sealed record GameSource(string Name, string Url, ulong Channel);
public sealed record GameNotice(string Url, string Title, string Revision, bool Update = false);
public sealed class GameHistory
{
    public HashSet<string> Seen { get; set; } = [];
    public HashSet<string> Games { get; set; } = [];
    public DateTimeOffset? LastSuccess { get; set; }
    public string Error { get; set; } = "";
    public bool SteamUpdatesSeeded { get; set; }
}

public sealed class GameUpdates : BackgroundService
{
    private static ulong ConfiguredChannel(string key)=>ulong.TryParse(Environment.GetEnvironmentVariable(key),out var id) && id>0?id:throw new InvalidOperationException("Configure "+key);
    private sealed record CoverAsset(byte[] Bytes,string Mime,string Filename);
    public static readonly GameSource[] Sources = [
        new("Online-Fix", "https://online-fix.me/", ConfiguredChannel("ONLINEFIX_CHANNEL_ID")),
        new("DODI", "https://dodi-repacks.site/", ConfiguredChannel("DODI_CHANNEL_ID")),
        new("FitGirl", "https://fitgirl-repacks.site/", ConfiguredChannel("FITGIRL_CHANNEL_ID")),
        new("SteamRIP", "https://steamrip.com/", ConfiguredChannel("STEAMRIP_CHANNEL_ID"))];
    private readonly HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(8) }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient discord = new() { BaseAddress = new Uri("https://discord.com/api/v10/"), Timeout = TimeSpan.FromSeconds(30) };
    private readonly BotSettings settings;
    private readonly string path;
    private readonly Dictionary<string, GameHistory> histories;
    public GameUpdates(BotSettings settings)
    {
        this.settings = settings;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ChunmaReleaseNotifier/1.0");
        discord.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", settings.Token);
        path = Path.Combine(Environment.GetEnvironmentVariable("READING_DATA_DIR") ?? "/app/data", "game-updates.json");
        histories = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, GameHistory>>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Game history is invalid; refusing to reset it.") : [];
    }
    private static string Text(HtmlNode? node) => Regex.Replace(HtmlEntity.DeEntitize(node?.InnerText ?? ""), @"\s+", " ").Trim();
    private static string Cls(string name) => $"contains(concat(' ',normalize-space(@class),' '),' {name} ')";
    public static string Key(GameNotice entry) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Url + "|" + entry.Revision)));
    private void Save()
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(histories, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
    private async Task<string> Fetch(string value, CancellationToken cancellation)
    {
        var uri = new Uri(value);
        for (var redirects = 0; redirects < 4; redirects++)
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var target = new Uri(uri, location);
                if (target.Scheme != "https" || target.Host != uri.Host || target.UserInfo.Length != 0) throw new HttpRequestException("Unexpected source redirect");
                uri = target; continue;
            }
            response.EnsureSuccessStatusCode();
            using var buffer = new MemoryStream();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            var block = new byte[8192]; int length;
            while ((length = await stream.ReadAsync(block, cancellation)) > 0)
            {
                if (buffer.Length + length > 4_000_000) throw new HttpRequestException("Source page exceeds size limit");
                buffer.Write(block, 0, length);
            }
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"') ?? "utf-8";
            return Encoding.GetEncoding(charset).GetString(buffer.ToArray());
        }
        throw new HttpRequestException("Too many source redirects");
    }
    public static List<GameNotice> Parse(GameSource source, string html, bool updateIndex = false)
    {
        var doc = new HtmlDocument(); doc.LoadHtml(html);
        var root = doc.DocumentNode;
        var result = new List<GameNotice>();
        void Add(HtmlNode? link, string title, string revision, bool update = false)
        {
            var raw = HtmlEntity.DeEntitize(link?.GetAttributeValue("href", "") ?? "");
            var origin = new Uri(source.Url);
            if (raw.Length == 0 || !Uri.TryCreate(origin, raw, out var url) || url.Scheme != "https" || url.Host != origin.Host || url.UserInfo.Length > 0 || title.Length == 0) return;
            result.Add(new(url.GetLeftPart(UriPartial.Path), AnimeService.Trim(title, 240), revision, update));
        }
        if (source.Name == "Online-Fix")
        {
            foreach (var article in root.SelectNodes("//article[" + Cls("news") + "]")?.AsEnumerable() ?? [])
            {
                var heading = article.SelectSingleNode(".//h2");
                Add(article.SelectSingleNode(".//a[" + Cls("big-link") + "]"), Text(heading), Text(heading));
            }
            foreach (var box in root.SelectNodes("//div[" + Cls("horizontal-slider") + "]")?.AsEnumerable() ?? [])
            {
                if (Text(box.SelectSingleNode("./div[" + Cls("title") + "]")) != "Обновления") continue;
                foreach (var link in box.SelectNodes(".//a[@href]")?.AsEnumerable() ?? [])
                    Add(link, Text(link.SelectSingleNode("./p[1]")), Text(link.SelectSingleNode("./p[last()]")), true);
            }
        }
        else if (source.Name == "SteamRIP" && updateIndex)
        {
            foreach(var link in root.SelectNodes("//a["+Cls("updated-card")+"][@href]")?.AsEnumerable()??[])
            {
                var title=Text(link.SelectSingleNode(".//*["+Cls("updated-card-title")+"]"));
                var build=Text(link.SelectSingleNode(".//*["+Cls("updated-list-build")+"]"));
                Add(link,title,title+"|"+build,true);
            }
        }
        else if (source.Name == "FitGirl" && updateIndex)
        {
            foreach (var box in root.SelectNodes("//div[" + Cls("su-spoiler") + "]")?.AsEnumerable() ?? [])
            {
                var title = Text(box.SelectSingleNode("./div[" + Cls("su-spoiler-title") + "]"));
                var content = box.SelectSingleNode("./div[" + Cls("su-spoiler-content") + "]");
                if (content is null) continue;
                var link = content.SelectNodes(".//a[@href]")?.FirstOrDefault(x => x.GetAttributeValue("href", "").StartsWith(source.Url, StringComparison.Ordinal));
                // Stable patch labels, not download URLs, comments, views or relative dates.
                var labels = content.SelectNodes(".//a[@href]")?.Select(Text).Where(x => x.Length > 0 && !x.Equals("Repack page", StringComparison.OrdinalIgnoreCase)).Distinct().Order().ToArray() ?? [];
                if (labels.Length > 0) Add(link, title, string.Join("|", labels), true);
            }
        }
        else
        {
            var headings = source.Name == "SteamRIP"
                ? "//h2[" + Cls("post-title") + " or " + Cls("thumb-title") + "]/a[@href]"
                : "//*[self::h1 or self::h2 or self::h3][" + Cls("entry-title") + "]/a[@href]";
            foreach (var link in root.SelectNodes(headings)?.AsEnumerable() ?? [])
            {
                var title = Text(link);
                if (Regex.IsMatch(title, @"^(Upcoming Repacks|Updates Digest|Weekly|Donation|Site News)", RegexOptions.IgnoreCase)) continue;
                Add(link, title, title,Regex.IsMatch(title,@"\brepack\s+updated\b",RegexOptions.IgnoreCase));
            }
        }
        if (result.Count == 0) throw new InvalidOperationException(source.Name + ": no release metadata found; previous history preserved");
        return result.DistinctBy(x => (x.Url, x.Revision)).Take(2500).ToList();
    }
    private async Task<List<GameNotice>> Read(GameSource source, CancellationToken cancellation)
    {
        var items = Parse(source, await Fetch(source.Url, cancellation));
        if (source.Name == "SteamRIP") items.AddRange(Parse(source, await Fetch(source.Url + "updated-games/", cancellation), true));
        if (source.Name == "FitGirl") items.AddRange(Parse(source, await Fetch(source.Url + "updates-list/", cancellation), true));
        return items.DistinctBy(Key).ToList();
    }
    public static string? ParseCover(string pageUrl, string html)
    {
        var doc = new HtmlDocument(); doc.LoadHtml(html);
        var candidates = new List<string>();
        foreach (var name in new[] { "og:image", "og:image:secure_url", "twitter:image" })
            foreach (var meta in doc.DocumentNode.SelectNodes("//meta[@content]")?.AsEnumerable() ?? [])
                if (meta.GetAttributeValue("property", meta.GetAttributeValue("name", "")).Equals(name, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(meta.GetAttributeValue("content", ""));
        foreach (var img in doc.DocumentNode.SelectNodes("//*[contains(@class,'entry-content') or contains(@class,'full-story') or contains(@class,'fullstory')]//img")?.AsEnumerable() ?? [])
            candidates.Add(img.GetAttributeValue("data-src", img.GetAttributeValue("src", "")));
        foreach (var raw in candidates)
        {
            if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(new Uri(pageUrl), HtmlEntity.DeEntitize(raw), out var uri)) continue;
            if (uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.IsLoopback || !uri.IsDefaultPort || Uri.CheckHostName(uri.Host) != UriHostNameType.Dns || !uri.Host.Contains('.')) continue;
            if (Regex.IsMatch(uri.AbsolutePath, @"(logo|avatar|icon|placeholder)", RegexOptions.IgnoreCase)) continue;
            return uri.AbsoluteUri;
        }
        return null;
    }
    private async Task<string?> Cover(GameNotice entry, CancellationToken cancellation)
    {
        try {
            var html=await Fetch(entry.Url,cancellation);
            var cover=ParseCover(entry.Url,html);
            if(cover is not null)return cover;
            var linked=RelatedGamePage(entry.Url,html);
            return linked is null?null:ParseCover(linked,await Fetch(linked,cancellation));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error) { Console.Error.WriteLine("Game cover unavailable: " + error.GetType().Name); return null; }
    }
    public static string? RelatedGamePage(string pageUrl,string html)
    {
        var origin=new Uri(pageUrl);
        if(origin.Host!="fitgirl-repacks.site" || !origin.AbsolutePath.Contains("repack-updated"))return null;
        var doc=new HtmlDocument();doc.LoadHtml(html);
        foreach(var a in doc.DocumentNode.SelectNodes("//div["+Cls("entry-content")+"]//a[@href]")?.AsEnumerable() ?? [])
            if(Text(a).Contains("repack",StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(origin,HtmlEntity.DeEntitize(a.GetAttributeValue("href","")),out var u) && u.Scheme=="https" && u.Host==origin.Host && u.UserInfo.Length==0 && u.AbsolutePath!=origin.AbsolutePath)
                return u.GetLeftPart(UriPartial.Path);
        return null;
    }
    public static bool AllowedImage(string value)
    {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var u)||u.Scheme!="https"||u.UserInfo.Length!=0||!u.IsDefaultPort)return false;
        return new[]{"imageban.ru","wp.com","steamrip.com","online-fix.me","img.youtube.com","steamstatic.com","steamcdn-a.akamaihd.net"}.Any(h=>u.Host==h||u.Host.EndsWith("."+h,StringComparison.Ordinal));
    }
    private async Task<CoverAsset?> DownloadCover(string? url,CancellationToken cancellation)
    {
        if(url is null)return null;
        try
        {
            for(var redirects=0;redirects<4;redirects++)
            {
                if(!AllowedImage(url))return null;
                using var response=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,cancellation);
                if((int)response.StatusCode is >=300 and <400 && response.Headers.Location is {} location){url=new Uri(new Uri(url),location).AbsoluteUri;continue;}
                if(response.StatusCode==HttpStatusCode.NotFound && new Uri(url).Host=="img.youtube.com" && url.EndsWith("/maxresdefault.jpg",StringComparison.Ordinal)){url=url.Replace("/maxresdefault.jpg","/hqdefault.jpg",StringComparison.Ordinal);continue;}
                response.EnsureSuccessStatusCode();
                var mime=response.Content.Headers.ContentType?.MediaType;
                if(mime is not ("image/jpeg" or "image/png" or "image/webp"))return null;
                await using var stream=await response.Content.ReadAsStreamAsync(cancellation);using var buffer=new MemoryStream();var block=new byte[8192];int n;
                while((n=await stream.ReadAsync(block,cancellation))>0){if(buffer.Length+n>2_000_000)return null;buffer.Write(block,0,n);}
                var bytes=buffer.ToArray();
                var valid=mime=="image/jpeg"?bytes.Length>3&&bytes[0]==255&&bytes[1]==216:mime=="image/png"?bytes.Length>8&&bytes[0]==137&&bytes[1]==80:bytes.Length>12&&Encoding.ASCII.GetString(bytes,0,4)=="RIFF"&&Encoding.ASCII.GetString(bytes,8,4)=="WEBP";
                if(!valid)return null;
                return new(bytes,mime,"cover."+(mime=="image/png"?"png":mime=="image/webp"?"webp":"jpg"));
            }
        }
        catch(OperationCanceledException)when(cancellation.IsCancellationRequested){throw;}
        catch(Exception e){Console.Error.WriteLine("Cover attachment unavailable: "+e.GetType().Name);}
        return null;
    }
    private static HttpContent MessageContent(object payload,CoverAsset? image)
    {
        if(image is null)return JsonContent.Create(payload);
        var node=JsonSerializer.SerializeToNode(payload)!;
        node["attachments"]=new JsonArray(new JsonObject{["id"]=0,["filename"]=image.Filename,["description"]="Game cover"});
        var form=new MultipartFormDataContent();form.Add(new StringContent(node.ToJsonString(),Encoding.UTF8,"application/json"),"payload_json");
        var data=new ByteArrayContent(image.Bytes);data.Headers.ContentType=new MediaTypeHeaderValue(image.Mime);form.Add(data,"files[0]",image.Filename);return form;
    }
    public async Task RepairCovers(CancellationToken cancellation=default)
    {
        using var identity=await discord.GetAsync("users/@me",cancellation);identity.EnsureSuccessStatusCode();
        using var me=JsonDocument.Parse(await identity.Content.ReadAsStringAsync(cancellation));var own=me.RootElement.GetProperty("id").GetString();
        foreach(var source in Sources)
        {
            await ValidateChannel(source,cancellation);
            using var response=await discord.GetAsync($"channels/{source.Channel}/messages?limit=50",cancellation);response.EnsureSuccessStatusCode();
            using var messages=JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));var fixedCount=0;
            foreach(var m in messages.RootElement.EnumerateArray())
            {
                if(m.GetProperty("author").GetProperty("id").GetString()!=own || m.GetProperty("attachments").GetArrayLength()>0 || m.GetProperty("embeds").GetArrayLength()!=1)continue;
                var e=m.GetProperty("embeds")[0];
                if(e.TryGetProperty("image",out var existingImage) && existingImage.TryGetProperty("url",out var existingUrl) && Uri.TryCreate(existingUrl.GetString(),UriKind.Absolute,out var hosted) && hosted.Host=="cdn.discordapp.com" && hosted.AbsolutePath.StartsWith("/attachments/",StringComparison.Ordinal))continue;
                if(!e.TryGetProperty("url",out var link)||!Uri.TryCreate(link.GetString(),UriKind.Absolute,out var uri)||uri.Host!=new Uri(source.Url).Host)continue;
                var cover=await Cover(new GameNotice(uri.AbsoluteUri,e.GetProperty("title").GetString()??"","repair"),cancellation);
                var asset=await DownloadCover(cover,cancellation);if(asset is null)continue;
                var embed=JsonNode.Parse(e.GetRawText())!.AsObject();embed.Remove("type");embed["image"]=new JsonObject{["url"]="attachment://"+asset.Filename};
                var payload=new{allowed_mentions=new{parse=Array.Empty<string>()},embeds=new[]{embed}};
                for(var attempt=0;;attempt++)
                {
                    using var request=new HttpRequestMessage(HttpMethod.Patch,$"channels/{source.Channel}/messages/{m.GetProperty("id").GetString()}"){Content=MessageContent(payload,asset)};
                    using var edited=await discord.SendAsync(request,cancellation);
                    if((int)edited.StatusCode==429 && attempt<4)
                    {
                        using var rate=JsonDocument.Parse(await edited.Content.ReadAsStringAsync(cancellation));var wait=rate.RootElement.GetProperty("retry_after").GetDouble()+0.5;
                        if(wait>60)throw new HttpRequestException("Discord repair cooldown exceeds one minute; rerun later (completed edits are skipped).");
                        await Task.Delay(TimeSpan.FromSeconds(wait),cancellation);continue;
                    }
                    edited.EnsureSuccessStatusCode();break;
                }
                fixedCount++;await Task.Delay(2200,cancellation);
            }
            Console.WriteLine(source.Name+": existing game embeds repaired with attachments: "+fixedCount);
        }
    }
    public async Task TestAll(CancellationToken cancellation=default)
    {
        foreach(var source in Sources)
        {
            try {
                await ValidateChannel(source,cancellation);
                var entry=(await Read(source,cancellation)).First();
                var cover=await Cover(entry,cancellation);var asset=await DownloadCover(cover,cancellation);
                var payload=new{allowed_mentions=new{parse=Array.Empty<string>()},embeds=new[]{new{
                    title=entry.Title,url=entry.Url,color=0xe8b254,
                    description="TEST • Latest public listing\n"+entry.Revision,
                    image=cover is null?null:new{url=asset is null?cover:"attachment://"+asset.Filename},
                    author=new{name=source.Name+" · TEST"},footer=new{text="Preview only · notification history unchanged"}
                }}};
                using var body=MessageContent(payload,asset);
                using var response=await discord.PostAsync($"channels/{source.Channel}/messages",body,cancellation);response.EnsureSuccessStatusCode();
                using var result=JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
                Console.WriteLine(JsonSerializer.Serialize(new{source=source.Name,status="sent",id=result.RootElement.GetProperty("id").GetString()}));
            }catch(Exception error){Console.WriteLine(JsonSerializer.Serialize(new{source=source.Name,status="failed",error=error.GetType().Name}));}
            await Task.Delay(1500,cancellation);
        }
    }
    public async Task TestSteam(CancellationToken cancellation=default)
    {
        var source=Sources.Single(x=>x.Name=="SteamRIP");
        await ValidateChannel(source,cancellation);
        var entry=Parse(source,await Fetch(source.Url+"updated-games/",cancellation),true).First();
        var cover=await Cover(entry,cancellation);
        var asset=await DownloadCover(cover,cancellation);
        var message=new{allowed_mentions=new{parse=Array.Empty<string>()},embeds=new[]{new{
            title=entry.Title,url=entry.Url,color=0xe8b254,
            description="TEST notification using a currently listed SteamRIP update.\n"+entry.Revision,
            image=cover is null?null:new{url=asset is null?cover:"attachment://"+asset.Filename},
            author=new{name="SteamRIP · TEST update"},footer=new{text="Preview only · notification history unchanged"}
        }}};
        using var body=MessageContent(message,asset);
        using var response=await discord.PostAsync($"channels/{source.Channel}/messages",body,cancellation);
        response.EnsureSuccessStatusCode();
        using var result=JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        Console.WriteLine(JsonSerializer.Serialize(new{id=result.RootElement.GetProperty("id").GetString(),title=entry.Title,uploadedCover=asset is not null}));
    }
    public async Task Inspect(CancellationToken cancellation = default)
    {
        foreach (var source in Sources)
        {
            var entries = await Read(source, cancellation);
            Console.WriteLine($"{source.Name}: {entries.Count} public listing/update records; destination {source.Channel}");
            Console.WriteLine(string.Join("\n", entries.Take(2).Select(x => x.Title)));
            foreach (var entry in entries.Take(2)) Console.WriteLine(entry.Url + " Cover: " + (await Cover(entry, cancellation) ?? "unavailable"));
        }
    }
    private async Task ValidateChannel(GameSource source, CancellationToken cancellation)
    {
        using var response = await discord.GetAsync($"channels/{source.Channel}", cancellation);
        response.EnsureSuccessStatusCode();
        using var channel = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        if (channel.RootElement.GetProperty("guild_id").GetString() != settings.GuildId.ToString()) throw new InvalidOperationException("Game channel is in an unexpected guild");
    }
    public async Task Cycle(bool seedOnly = false, CancellationToken cancellation = default)
    {
        foreach (var source in Sources)
        {
            try
            {
                await ValidateChannel(source, cancellation);
                var entries = await Read(source, cancellation);
                if (!histories.TryGetValue(source.Name, out var history))
                {
                    histories[source.Name] = new GameHistory { Seen = entries.Select(Key).ToHashSet(), Games = entries.Select(x => x.Url).ToHashSet(), LastSuccess = DateTimeOffset.UtcNow, SteamUpdatesSeeded = source.Name == "SteamRIP" };
                    Save(); Console.WriteLine($"{source.Name}: silent baseline saved ({entries.Count})"); continue;
                }
                if(source.Name=="SteamRIP"&&!history.SteamUpdatesSeeded)
                {
                    var baseline=entries.Where(x=>x.Update).ToList();
                    foreach(var item in baseline){history.Seen.Add(Key(item));history.Games.Add(item.Url);}
                    history.SteamUpdatesSeeded=true;Save();
                    Console.WriteLine($"SteamRIP: update index silent baseline saved ({baseline.Count})");
                }
                Console.WriteLine($"{source.Name}: metadata={entries.Count}, pending={entries.Count(x=>!history.Seen.Contains(Key(x)))}");
                if (seedOnly) continue;
                foreach (var entry in entries.AsEnumerable().Reverse())
                {
                    var key = Key(entry);
                    if (history.Seen.Contains(key)) continue;
                    var changed = entry.Update || history.Games.Contains(entry.Url);
                    var cover = await Cover(entry, cancellation);
                    var asset = await DownloadCover(cover,cancellation);
                    var message = new { allowed_mentions = new { parse = Array.Empty<string>() }, embeds = new[] { new {
                        title = entry.Title, url = entry.Url, color = changed ? 0xe8b254 : 0x61cfa3,
                        image = cover is null ? null : new { url = asset is null?cover:"attachment://"+asset.Filename },
                        author = new { name = source.Name + (changed ? " · Update listing changed" : " · New game listing") },
                        footer = new { text = "Public latest listings · checked every 10 minutes" } } } };
                    for (var attempt = 0; ; attempt++)
                    {
                        using var body=MessageContent(message,asset);
                        using var response = await discord.PostAsync($"channels/{source.Channel}/messages", body, cancellation);
                        if ((int)response.StatusCode == 429 && attempt < 4)
                        {
                            using var limit = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
                            await Task.Delay(TimeSpan.FromSeconds(limit.RootElement.GetProperty("retry_after").GetDouble() + .2), cancellation); continue;
                        }
                        response.EnsureSuccessStatusCode(); break;
                    }
                    history.Seen.Add(key); history.Games.Add(entry.Url); Save();
                    await Task.Delay(1000, cancellation);
                }
                history.LastSuccess = DateTimeOffset.UtcNow; history.Error = ""; Save();
                Console.WriteLine(source.Name + ": check successful");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                if (histories.TryGetValue(source.Name, out var state)) { state.Error = error.Message; Save(); }
                Console.Error.WriteLine($"GameUpdates {source.Name}: {error.GetType().Name}: {error.Message}");
                if (seedOnly) throw;
            }
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Cycle(cancellation: stoppingToken);
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }
    public override void Dispose() { http.Dispose(); discord.Dispose(); base.Dispose(); }
}
