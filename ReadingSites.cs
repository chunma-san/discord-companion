using HtmlAgilityPack;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GreatLoveImmortalVenerable;

public static class ReadingSites
{
    private static string HasClass(string name) => $"contains(concat(' ',normalize-space(@class),' '),' {name} ')";
    private static string Text(HtmlNode? node) => Regex.Replace(HtmlEntity.DeEntitize(node?.InnerText ?? ""), @"\s+", " ").Trim();
    private static List<ChapterEntry> ParseKolEditorial(HtmlNode root, Uri source)
    {
        var result = new List<ChapterEntry>();
        foreach(var row in root.SelectNodes($"//div[{HasClass("kol-home-chapter-entry")}]")?.Take(500) ?? [])
        {
            var link=row.SelectSingleNode(".//a[@href]");
            if(link is null || !Uri.TryCreate(source,HtmlEntity.DeEntitize(link.GetAttributeValue("href","")),out var url)
                || url.Scheme!="https" || url.Host!=source.Host || url.UserInfo.Length>0)continue;
            var title=Text(link);if(title.Length==0)continue;
            var image=row.SelectSingleNode("ancestor::article[1]//img");
            var raw=image?.GetAttributeValue("data-src",null) ?? image?.GetAttributeValue("src","");
            string? cover=Uri.TryCreate(source,raw,out var coverUrl) && coverUrl.Scheme=="https" && coverUrl.Host==source.Host ? coverUrl.AbsoluteUri : null;
            var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri)));
            result.Add(new(key,AnimeService.Trim(title,250),url.AbsoluteUri,null,cover));
        }
        if(result.Count==0)throw new ArgumentException("Kol Novel returned no public chapter listings; history preserved.");
        return result.DistinctBy(e=>e.Key).Take(500).ToList();
    }
    public static List<ChapterEntry> Parse(string html, Uri source)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var root = document.DocumentNode;
        if(root.SelectSingleNode("//form[@id='lsrecaptcha-form']") is not null)
            throw new SourceVerificationException("Publisher requires browser verification; delivery history preserved.");
        var host = source.Host.ToLowerInvariant().Replace("www.", "");
        if (host == "kolnovel.com" && root.SelectSingleNode($"//div[{HasClass("kol-home-chapter-entry")}]") is not null)
            return ParseKolEditorial(root, source);
        string container, title, links;
        switch (host)
        {
            case "kolnovel.com":
                container = $"//div[{HasClass("luf")}]";
                title = ".//h3";
                links = ".//ul/li/a[@href]";
                break;
            case "olympustaff.com":
                container = $"//div[{HasClass("last-chapter")}]//div[{HasClass("info")}]";
                title = ".//h3";
                links = ".//ul/li/a[@href and not(@data-bs-toggle)]";
                break;
            case "mangalik.net":
                container = $"//div[@id='loop-content']//div[{HasClass("item-summary")}]";
                title = $".//div[{HasClass("post-title")}]//a";
                links = $".//span[{HasClass("chapter")}]//a[@href]";
                break;
            default:
                throw new ArgumentException("HTML is supported for kolnovel.com, olympustaff.com and mangalik.net. Other sources need RSS/Atom.");
        }
        var result = new List<ChapterEntry>();
        foreach (var node in root.SelectNodes(container)?.Take(200) ?? [])
        {
            var series = Text(node.SelectSingleNode(title));
            if (series.Length == 0) continue;
            var image = node.ParentNode?.SelectSingleNode(".//img");
            string? coverUrl = null;
            foreach (var attribute in new[] { "data-src", "data-lazy-src", "src" })
            {
                var imageUrl = HtmlEntity.DeEntitize(image?.GetAttributeValue(attribute, "") ?? "");
                if (imageUrl.Length > 0 && Uri.TryCreate(source, imageUrl, out var cover)
                    && cover.Scheme == "https" && cover.UserInfo.Length == 0
                    && (!System.Net.IPAddress.TryParse(cover.Host, out var address) || ReadingFeeds.IsPublic(address)))
                { coverUrl = cover.AbsoluteUri; break; }
            }
            foreach (var link in node.SelectNodes(links)?.Take(10) ?? [])
            {
                var raw = HtmlEntity.DeEntitize(link.GetAttributeValue("href", ""));
                if (raw.Length == 0 || raw.StartsWith('#') || !Uri.TryCreate(source, raw, out var target)
                    || target.Scheme != "https" || target.Host != source.Host || target.UserInfo.Length != 0) continue;
                var chapter = Text(link);
                if (chapter.Length == 0) continue;
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.AbsoluteUri)));
                result.Add(new(key, AnimeService.Trim($"{series} — {chapter}", 250), target.AbsoluteUri, null, coverUrl));
            }
        }
        if (result.Count == 0) throw new ArgumentException("No public chapter links found. The source may be unavailable or its layout changed.");
        return result.DistinctBy(e => e.Key).Take(500).ToList();
    }
}
public static class ReadingSetup
{
    public static async Task RefreshKolBaseline()
    {
        using var feeds=new ReadingFeeds();
        var store=new ReadingStore(Environment.GetEnvironmentVariable("READING_DATA_DIR")??"/app/data");
        foreach(var watch in store.Watches.Where(w=>new Uri(w.FeedUrl).Host=="kolnovel.com"))
        {
            var entries=await feeds.Fetch(watch.FeedUrl);
            watch.Seen=watch.Seen.Concat(entries.Select(e=>e.Key)).Distinct().ToList();
            watch.Latest=entries.Take(5).ToList();
            watch.LastChecked=watch.LastSuccess=DateTimeOffset.UtcNow;
            watch.Failures=0;watch.Error="";
        }
        store.Save();
        Console.WriteLine("Kol Novel current entries recorded silently; prior delivery history retained.");
    }

    public static readonly (string Name, ReadingKind Kind, string Url)[] Sources =
    [
        ("Kolnovel • Arabic novels", ReadingKind.Novel, "https://kolnovel.com/"),
        ("Olympus • Arabic comics", ReadingKind.Manhua, "https://olympustaff.com/"),
        ("Mangalek • Arabic comics", ReadingKind.Manhua, "https://mangalik.net/")
    ];
    public static async Task Inspect()
    {
        using var feeds = new ReadingFeeds();
        foreach (var source in Sources)
        {
            var entries = await feeds.Fetch(source.Url);
            Console.WriteLine($"{source.Url} | public chapters: {entries.Count}");
            foreach (var entry in entries.Take(2)) Console.WriteLine($"{entry.Title} | {entry.Url} | cover={entry.CoverUrl}");
        }
    }
    public static async Task Bootstrap()
    {
        using var service = new ChapterService(BotSettings.FromEnvironment());
        foreach (var source in Sources)
            await service.Add(source.Name, source.Kind, source.Url, null);
        Console.WriteLine("Three subscriptions initialized; historical entries recorded silently.");
    }
}


public sealed class SourceVerificationException(string message) : Exception(message);
