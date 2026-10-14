using System.Net.Http.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Hosting;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace GreatLoveImmortalVenerable;

public enum ReadingKind { Manhua, Novel }
public sealed record ChapterEntry(string Key, string Title, string Url, DateTimeOffset? Published, string? CoverUrl = null);
public sealed class ReadingWatch
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    public string FeedUrl { get; set; } = "";
    public ReadingKind Kind { get; set; }
    public string Filter { get; set; } = "";
    public ulong ChannelId { get; set; }
    public List<string> Seen { get; set; } = [];
    public List<ChapterEntry> Latest { get; set; } = [];
    public DateTimeOffset? LastChecked { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public int Failures { get; set; }
    public string Error { get; set; } = "";
}
public sealed class ReadingStore
{
    private readonly string path;
    public List<ReadingWatch> Watches { get; }
    public ReadingStore(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "chapters.json");
        Watches = File.Exists(path)
            ? JsonSerializer.Deserialize<List<ReadingWatch>>(File.ReadAllText(path))
                ?? throw new InvalidOperationException("Reading state is invalid; refusing to reset release history.")
            : [];
    }
    public void Save()
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Watches, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
public sealed class ReadingFeeds : IDisposable
{
    private readonly HttpClient http;
    public ReadingFeeds()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = async (context, cancellation) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellation);
                if (addresses.Length == 0 || addresses.Any(a => !IsPublic(a)))
                    throw new HttpRequestException("Only public internet sources are supported.");
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellation);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); }
                }
                throw new HttpRequestException("Source could not be reached.");
            }
        };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GreatLoveImmortalVenerable/2.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/atom+xml, application/rss+xml, application/xml, text/xml, text/html");
    }
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xe0) == 0x20 && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
        }
        var b = address.GetAddressBytes();
        return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224
            && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            && !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            && !(b[0] == 198 && (b[1] == 18 || b[1] == 19))
            && !(b[0] == 192 && b[1] == 0) && !(b[0] == 198 && b[1] == 51 && b[2] == 100)
            && !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }
    public static Uri SourceUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443
            || uri.UserInfo.Length != 0 || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || value.Length > 1500
            || (IPAddress.TryParse(uri.Host, out var address) && !IsPublic(address)))
            throw new ArgumentException("Use a public HTTPS supported reading site or RSS/Atom feed, without credentials or a custom port.");
        return uri;
    }
    public async Task<List<ChapterEntry>> Fetch(string url, CancellationToken cancellation = default)
    {
        var uri = SourceUri(url);
        for (var redirects = 0; redirects < 5; redirects++)
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = SourceUri(new Uri(uri, location).AbsoluteUri);
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 2_000_000) throw new ArgumentException("Feed exceeds the supported size.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            using var buffer = new MemoryStream();
            var block = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(block, cancellation)) != 0)
            {
                if (buffer.Length + count > 2_000_000) throw new ArgumentException("Feed exceeds the supported size.");
                buffer.Write(block, 0, count);
            }
            return response.Content.Headers.ContentType?.MediaType == "text/html"
                ? ReadingSites.Parse(Encoding.UTF8.GetString(buffer.ToArray()), uri)
                : Parse(buffer.ToArray(), uri);
        }
        throw new ArgumentException("Too many source redirects.");
    }
    public static List<ChapterEntry> Parse(byte[] bytes, Uri baseUri)
    {
        using var input = new MemoryStream(bytes);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000
        });
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName is not ("rss" or "feed" or "RDF"))
            throw new ArgumentException("This is not an RSS/Atom feed. Send the series link so a source adapter can be checked.");
        var result = new List<ChapterEntry>();
        foreach (var node in document.Descendants().Where(n => n.Name.LocalName is "item" or "entry").Take(500))
        {
            string Child(string name) => node.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";
            var link = node.Elements().FirstOrDefault(e => e.Name.LocalName == "link" && (e.Attribute("rel")?.Value is null or "alternate"));
            var raw = link?.Attribute("href")?.Value ?? link?.Value.Trim() ?? "";
            if (raw.Length == 0 || !Uri.TryCreate(baseUri, raw, out var target) || target.Scheme != "https" || target.UserInfo.Length != 0) continue;
            var identity = Child("guid");
            if (identity.Length == 0) identity = Child("id");
            if (identity.Length == 0) identity = target.AbsoluteUri;
            var date = Child("pubDate");
            if (date.Length == 0) date = Child("published");
            if (date.Length == 0) date = Child("updated");
            DateTimeOffset? published = DateTimeOffset.TryParse(date, out var parsed) ? parsed : null;
            var title = Child("title");
            if (title.Length == 0) title = "New chapter";
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            result.Add(new(key, AnimeService.Trim(title, 250), target.AbsoluteUri, published));
        }
        return result.DistinctBy(e => e.Key).ToList();
    }
    public void Dispose() => http.Dispose();
}
public sealed class ChapterService : BackgroundService
{
    private readonly ReadingFeeds feeds = new();
    private readonly HttpClient discord = new() { BaseAddress = new Uri("https://discord.com/api/v10/"), Timeout = TimeSpan.FromSeconds(25) };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ReadingStore store;
    private readonly ulong manhuaChannel, novelChannel;
    public ChapterService(BotSettings settings)
    {
        store = new ReadingStore(Environment.GetEnvironmentVariable("READING_DATA_DIR") ?? "/app/data");
        ulong.TryParse(Environment.GetEnvironmentVariable("MANHUA_CHANNEL_ID"), out manhuaChannel);
        ulong.TryParse(Environment.GetEnvironmentVariable("NOVEL_CHANNEL_ID"), out novelChannel);
        discord.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", settings.Token);
        discord.DefaultRequestHeaders.UserAgent.ParseAdd("GreatLoveImmortalVenerable/2.0");
    }
    public static List<ChapterEntry> Pending(ReadingWatch watch, IEnumerable<ChapterEntry> entries) =>
        entries.Reverse().Where(e => !watch.Seen.Contains(e.Key)).Take(10).ToList();
    private static List<ChapterEntry> Filter(ReadingWatch watch, List<ChapterEntry> entries) =>
        entries.Where(e => watch.Filter.Length == 0 || e.Title.Contains(watch.Filter, StringComparison.OrdinalIgnoreCase)).ToList();
    public async Task<EmbedProperties> Add(string name, ReadingKind kind, string source, string? filter)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || (filter?.Length ?? 0) > 150)
            throw new ArgumentException("Series name: 1-100 characters; optional title filter: up to 150.");
        var canonical = ReadingFeeds.SourceUri(source).AbsoluteUri;
        var channel = kind == ReadingKind.Manhua ? manhuaChannel : novelChannel;
        if (channel == 0) throw new ArgumentException("The reading channel is not configured.");
        await gate.WaitAsync();
        try
        {
            if (store.Watches.Count >= 30) throw new ArgumentException("Maximum 30 subscriptions. Remove an unused source first.");
            if (store.Watches.Any(w => w.FeedUrl == canonical && w.Kind == kind && w.Filter == (filter ?? "")))
                throw new ArgumentException("This source and filter are already tracked.");
            var watch = new ReadingWatch { Name = name.Trim(), Kind = kind, FeedUrl = canonical, Filter = filter ?? "", ChannelId = channel };
            var entries = Filter(watch, await feeds.Fetch(canonical));
            watch.Seen = entries.Select(e => e.Key).ToList();
            watch.Latest = entries.Take(5).ToList();
            watch.LastChecked = watch.LastSuccess = DateTimeOffset.UtcNow;
            store.Watches.Add(watch);
            try { store.Save(); } catch { store.Watches.Remove(watch); throw; }
            return Embeds.Build("Subscription added", $"**{watch.Name}** | {kind}\nID: {watch.Id}\nDestination: <#{channel}>\nExisting entries were recorded without reposting. New entries are checked every 10 minutes.");
        }
        finally { gate.Release(); }
    }
    public async Task<EmbedProperties> List()
    {
        await gate.WaitAsync();
        try
        {
            return Embeds.Build("Chapter subscriptions", store.Watches.Count == 0
                ? "No sources added yet. Use /chapters add with a series RSS/Atom feed."
                : string.Join("\n\n", store.Watches.Select(w => $"**{w.Name}** | {w.Kind} | {w.Id}\n<#{w.ChannelId}> | {(w.Failures == 0 ? "OK" : $"Failed checks: {w.Failures} ({w.Error})")}\nLast successful check: {(w.LastSuccess is { } date ? $"<t:{date.ToUnixTimeSeconds()}:R>" : "not yet")}")));
        }
        finally { gate.Release(); }
    }
    public async Task<EmbedProperties> Remove(string id)
    {
        await gate.WaitAsync();
        try
        {
            var watch = store.Watches.FirstOrDefault(w => w.Id == id) ?? throw new ArgumentException("Unknown subscription ID. Use /chapters list.");
            var index = store.Watches.IndexOf(watch);
            store.Watches.Remove(watch);
            try { store.Save(); } catch { store.Watches.Insert(index, watch); throw; }
            return Embeds.Build("Subscription removed", watch.Name);
        }
        finally { gate.Release(); }
    }
    public async Task<EmbedProperties> Latest(string id)
    {
        await gate.WaitAsync();
        try
        {
            var watch = store.Watches.FirstOrDefault(w => w.Id == id) ?? throw new ArgumentException("Unknown subscription ID. Use /chapters list.");
            return Embeds.Build(watch.Name, string.Join("\n\n", watch.Latest.Select(e => $"**{e.Title}**\n{e.Url}")) is { Length: > 0 } text ? text : "No matching entries yet.");
        }
        finally { gate.Release(); }
    }
    private async Task Publish(ReadingWatch watch, ChapterEntry entry, CancellationToken cancellation, string? messageId = null)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var embed = new Dictionary<string, object>
            {
                ["title"] = entry.Title,
                ["url"] = entry.Url,
                ["description"] = watch.Name.Split('•')[0].Trim(),
                ["color"] = 13280328
            };
            if (Uri.TryCreate(entry.CoverUrl, UriKind.Absolute, out var cover) && cover.Scheme == "https" && cover.UserInfo.Length == 0)
                embed["thumbnail"] = new { url = cover.AbsoluteUri };
            using var request = new HttpRequestMessage(messageId is null ? HttpMethod.Post : HttpMethod.Patch,
                $"channels/{watch.ChannelId}/messages" + (messageId is null ? "" : $"/{messageId}"))
            { Content = JsonContent.Create(new
            {
                allowed_mentions = new { parse = Array.Empty<string>() },
                embeds = new[] { embed }
            }) };
            using var response = await discord.SendAsync(request, cancellation);
            if ((int)response.StatusCode == 429)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
                var seconds = json.RootElement.TryGetProperty("retry_after", out var retry) ? retry.GetDouble() : 5;
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60)), cancellation);
                continue;
            }
            response.EnsureSuccessStatusCode();
            return;
        }
        throw new HttpRequestException("Discord rate limit; retrying on the next check.");
    }
    public async Task Preview()
    {
        foreach (var watch in store.Watches)
        {
            var entries = await feeds.Fetch(watch.FeedUrl);
            using var response = await discord.GetAsync($"channels/{watch.ChannelId}/messages?limit=100");
            response.EnsureSuccessStatusCode();
            using var messages = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (var message in messages.RootElement.EnumerateArray())
            {
                if (!message.TryGetProperty("embeds", out var embeds) || embeds.GetArrayLength() == 0) continue;
                if (!embeds[0].TryGetProperty("url", out var url)) continue;
                var entry = entries.FirstOrDefault(e => e.Url == url.GetString());
                if (entry is null) continue;
                await Publish(watch, entry, CancellationToken.None, message.GetProperty("id").GetString());
                Console.WriteLine($"Existing chapter alert updated: {watch.Name}; cover: {entry.CoverUrl is not null}");
                break;
            }
            await Task.Delay(1500);
        }
    }
    public async Task<EmbedProperties> Check(CancellationToken cancellation = default)
    {
        if (!await gate.WaitAsync(0, cancellation)) return Embeds.Build("Check running", "A check is already running. Try again shortly.");
        var sent = 0; var failures = 0; var checkedCount = 0;
        try
        {
            foreach (var watch in store.Watches)
            {
                if (watch.LastChecked is { } recent && DateTimeOffset.UtcNow - recent < TimeSpan.FromMinutes(2)) continue;
                watch.LastChecked = DateTimeOffset.UtcNow;
                checkedCount++;
                try
                {
                    var entries = Filter(watch, await feeds.Fetch(watch.FeedUrl, cancellation));
                    watch.Latest = entries.Take(5).ToList();
                    foreach (var entry in Pending(watch, entries))
                    {
                        await Publish(watch, entry, cancellation);
                        watch.Seen.Add(entry.Key);
                        if (watch.Seen.Count > 5000) watch.Seen.RemoveRange(0, watch.Seen.Count - 5000);
                        store.Save();
                        sent++;
                        await Task.Delay(1200, cancellation);
                    }
                    watch.LastSuccess = DateTimeOffset.UtcNow; watch.Failures = 0; watch.Error = "";
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception e)
                {
                    watch.Failures++; failures++;
                    watch.Error = e is HttpRequestException h && h.StatusCode is { } status ? $"HTTP {(int)status}" : e.GetType().Name;
                    Console.Error.WriteLine($"Reading source {watch.Id}: {watch.Error}");
                }
                store.Save();
                await Task.Delay(1000, cancellation);
            }
            return Embeds.Build("Chapter check complete", $"Sources checked: {checkedCount}\nNew chapter alerts: {sent}\nFailed sources: {failures}\nSources checked less than two minutes ago are skipped.");
        }
        finally { gate.Release(); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Check(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { Console.Error.WriteLine($"Reading worker: {e.GetType().Name}"); }
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }
    public override void Dispose() { feeds.Dispose(); discord.Dispose(); gate.Dispose(); base.Dispose(); }
}
[SlashCommand("chapters", "Owner: track new manhua and novel chapters", DefaultGuildPermissions = Permissions.Administrator)]
public sealed class ChapterCommands(ChapterService chapters, CommandGate gate) : ApplicationCommandModule<ApplicationCommandContext>
{
    private Task Run(Func<Task<EmbedProperties>> action) => gate.Execute(Context, async () =>
    {
        try { return await action(); }
        catch (ArgumentException e) { return Embeds.Build("Check the subscription", e.Message); }
        catch (XmlException) { return Embeds.Build("Unsupported feed", "This source did not return valid RSS/Atom. Send the series page link so its supported source can be checked."); }
    }, true);
    [SubSlashCommand("add", "Track a supported reading site or RSS/Atom feed")]
    public Task Add(string name, ReadingKind kind, string feed_url, string? title_filter = null) => Run(() => chapters.Add(name, kind, feed_url, title_filter));
    [SubSlashCommand("list", "List tracked sources, destinations and source health")]
    public Task List() => Run(chapters.List);
    [SubSlashCommand("remove", "Stop tracking one source by subscription ID")]
    public Task Remove(string id) => Run(() => chapters.Remove(id));
    [SubSlashCommand("latest", "Show the latest five entries from the last successful check")]
    public Task Latest(string id) => Run(() => chapters.Latest(id));
    [SubSlashCommand("check", "Check tracked sources now, with a two-minute per-source cooldown")]
    public Task Check() => Run(() => chapters.Check());
}

