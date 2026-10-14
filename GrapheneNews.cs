using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using HtmlAgilityPack;
using Microsoft.Extensions.Hosting;

namespace GreatLoveImmortalVenerable;

public sealed record GrapheneEntry(string Id, string Title, string Url, string Summary);
public sealed class GrapheneSource
{
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Seen { get; set; } = [];
    public GrapheneEntry? Latest { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public int Failures { get; set; }
    public bool PreviewSent { get; set; }
}
public sealed class GrapheneNews : BackgroundService
{
    public static readonly (string Url, string Name)[] Sources =
    [
        ("https://grapheneos.org/releases.atom", "GrapheneOS Releases"),
        ("https://grapheneos.social/@GrapheneOS.rss", "GrapheneOS News")
    ];
    private readonly HttpClient http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All
    }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient discord = new() { BaseAddress = new Uri("https://discord.com/api/v10/"), Timeout = TimeSpan.FromSeconds(25) };
    private readonly string path;
    private readonly ulong channel;
    private readonly List<GrapheneSource> state;
    public GrapheneNews(BotSettings settings)
    {
        ulong.TryParse(Environment.GetEnvironmentVariable("GRAPHENEOS_CHANNEL_ID"), out channel);
        var directory = LibraryCatalog.DirectoryPath;
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "grapheneos.json");
        state = File.Exists(path) ? JsonSerializer.Deserialize<List<GrapheneSource>>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("GrapheneOS history is invalid.") : [];
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GreatLoveImmortalVenerable/2.0");
        discord.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", settings.Token);
        discord.DefaultRequestHeaders.UserAgent.ParseAdd("GreatLoveImmortalVenerable/2.0");
    }
    private void Save()
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
    private static string Clean(string html)
    {
        var doc = new HtmlDocument(); doc.LoadHtml(html);
        foreach (var node in doc.DocumentNode.SelectNodes("//script|//style")?.ToArray() ?? []) node.Remove();
        foreach (var node in doc.DocumentNode.SelectNodes("//p|//li|//br")?.ToArray() ?? []) node.AppendChild(doc.CreateTextNode("\n"));
        return HtmlEntity.DeEntitize(doc.DocumentNode.InnerText).Trim();
    }
    public static List<GrapheneEntry> Parse(byte[] bytes, bool release)
    {
        using var input = new MemoryStream(bytes);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000 });
        var doc = XDocument.Load(reader);
        if (doc.Root?.Name.LocalName is not ("rss" or "feed")) throw new InvalidOperationException("Invalid official feed.");
        var result = new List<GrapheneEntry>();
        foreach (var node in doc.Descendants().Where(e => e.Name.LocalName == (release ? "entry" : "item")).Take(100))
        {
            string Value(string name) => node.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";
            var raw = node.Elements().FirstOrDefault(e => e.Name.LocalName == "link");
            var url = raw?.Attribute("href")?.Value ?? raw?.Value.Trim() ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0
                || uri.Host != (release ? "grapheneos.org" : "grapheneos.social")) continue;
            var content = node.Elements().FirstOrDefault(e => e.Name.LocalName == (release ? "content" : "description"));
            var markup = content?.HasElements == true ? string.Join("", content.Nodes().Select(n => n.ToString())) : content?.Value ?? "";
            // Release changelogs have their own authoritative feed.
            if (!release && markup.Contains("https://grapheneos.org/releases#", StringComparison.OrdinalIgnoreCase)) continue;
            var text = Clean(markup);
            var title = Value("title");
            if (title.Length == 0) title = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "GrapheneOS News";
            if (release) title = "GrapheneOS — " + title;
            var summary = text;
            if (release && content is not null)
            {
                var lists = content.Descendants().Where(e => e.Name.LocalName == "ul").ToList();
                if (lists.Count > 1) summary = string.Join("\n", lists[1].Elements().Where(e => e.Name.LocalName == "li").Take(5).Select(e => "• " + e.Value.Trim()));
            }
            else if (summary.StartsWith(title, StringComparison.Ordinal)) summary = summary[title.Length..].Trim();
            var id = Value(release ? "id" : "guid");
            result.Add(new(id.Length > 0 ? id : url, AnimeService.Trim(title, 240), url, AnimeService.Trim(summary, 1000)));
        }
        if (result.Count == 0) throw new InvalidOperationException("No official news entries found.");
        return result.DistinctBy(e => e.Id).ToList();
    }
    private async Task<List<GrapheneEntry>> Fetch(string url, CancellationToken token = default)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 1_000_000) throw new InvalidOperationException("Official feed exceeds size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var block = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(block, token)) != 0)
        {
            if (buffer.Length + count > 1_000_000) throw new InvalidOperationException("Official feed exceeds size limit.");
            buffer.Write(block, 0, count);
        }
        return Parse(buffer.ToArray(), url == Sources[0].Url);
    }
    private async Task Publish(GrapheneSource source, GrapheneEntry entry, CancellationToken token = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await discord.PostAsJsonAsync($"channels/{channel}/messages", new
            {
                allowed_mentions = new { parse = Array.Empty<string>() },
                embeds = new[] { new { title = entry.Title, url = entry.Url,
                    description = entry.Summary + $"\n\n**Source:** {source.Name}", color = 6381921 } }
            }, token);
            if ((int)response.StatusCode == 429)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var delay = json.RootElement.TryGetProperty("retry_after", out var retry) ? retry.GetDouble() : 5;
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(delay, 1, 60)), token);
                continue;
            }
            response.EnsureSuccessStatusCode(); return;
        }
        throw new HttpRequestException("Discord rate limit; will retry next check.");
    }
    public async Task Setup()
    {
        if (channel == 0) throw new InvalidOperationException("GrapheneOS channel is not configured.");
        // Fetch both before saving a baseline, so a failed source is never silently initialized.
        foreach (var (url, name) in Sources)
        {
            var entries = await Fetch(url);
            var source = state.FirstOrDefault(s => s.Url == url);
            if (source is null)
            {
                source = new GrapheneSource { Url = url, Name = name, Seen = entries.Select(e => e.Id).ToList(), Latest = entries[0], LastSuccess = DateTimeOffset.UtcNow };
                state.Add(source); Save();
            }
            if (!source.PreviewSent)
            {
                await Publish(source, entries[0]);
                source.PreviewSent = true; Save();
                Console.WriteLine($"GrapheneOS initial preview delivered: {name}");
                await Task.Delay(1500);
            }
        }
    }
    private async Task Check(CancellationToken token)
    {
        foreach (var (url, name) in Sources)
        {
            var source = state.FirstOrDefault(s => s.Url == url);
            try
            {
                var entries = await Fetch(url, token);
                if (source is null)
                {
                    source = new GrapheneSource { Url = url, Name = name, Seen = entries.Select(e => e.Id).ToList() };
                    state.Add(source); // First successful fetch records historical entries silently.
                }
                else foreach (var entry in entries.AsEnumerable().Reverse().Where(e => !source.Seen.Contains(e.Id)).Take(5))
                {
                    await Publish(source, entry, token);
                    source.Seen.Add(entry.Id); Save();
                    await Task.Delay(1500, token);
                }
                source.Latest = entries[0]; source.LastSuccess = DateTimeOffset.UtcNow; source.Failures = 0;
                if (source.Seen.Count > 3000) source.Seen.RemoveRange(0, source.Seen.Count - 3000);
                Save();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                if (source is not null) { source.Failures++; Save(); }
                Console.Error.WriteLine($"GrapheneOS source {name}: {e.GetType().Name}");
            }
        }
    }
    public async Task Test()
    {
        foreach (var (url, name) in Sources)
        {
            var entries = await Fetch(url);
            var seen = entries.Select(e => e.Id).ToHashSet();
            if (entries.Any(e => !seen.Contains(e.Id))) throw new Exception("Baseline deduplication failed.");
            Console.WriteLine($"PASS {name}: {entries.Count} parsed entries; latest: {entries[0].Title}; old entries suppressed.");
        }
        var fixture = System.Text.Encoding.UTF8.GetBytes("<rss><channel><item><guid>release-copy</guid><link>https://grapheneos.social/@GrapheneOS/1</link><description>&lt;a href='https://grapheneos.org/releases#123'&gt;Release&lt;/a&gt;</description></item><item><guid>news</guid><link>https://grapheneos.social/@GrapheneOS/2</link><description>&lt;p&gt;Official news&lt;/p&gt;</description></item></channel></rss>");
        if (Parse(fixture, false).Single().Id != "news") throw new Exception("Cross-source release duplication.");
        Console.WriteLine("PASS Mastodon release copies excluded; news retained.");
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (channel == 0) return;
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Check(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { Console.Error.WriteLine($"GrapheneOS worker: {e.GetType().Name}"); }
            await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
        }
    }
    public override void Dispose() { http.Dispose(); discord.Dispose(); base.Dispose(); }
}

