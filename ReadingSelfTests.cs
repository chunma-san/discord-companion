using System.Net;
using System.Text;
namespace GreatLoveImmortalVenerable;
public static class ReadingSelfTests
{
    private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
    public static void Run()
    {
        try { ReadingSites.Parse("<title>Bot Verification</title><form id='lsrecaptcha-form'></form>",new Uri("https://mangalik.net/"));throw new Exception("Verification page accepted"); }
        catch(SourceVerificationException){Check(true,"Publisher verification pages identified without treating them as empty listings");}
        var editorial=ReadingSites.Parse("<article><img src='/cover.gif'/><div class='kol-home-chapter-entry'><a href='/chapter-3/'>Series — Chapter 3</a></div><div class='kol-home-chapter-entry'><a href='https://example.com/trap'>Trap</a></div></article>",new Uri("https://kolnovel.com/"));
        Check(editorial.Count==1 && editorial[0].CoverUrl=="https://kolnovel.com/cover.gif" && editorial[0].Title=="Series — Chapter 3","Kol editorial listings preserve covers and reject external links");

        var kol = ReadingSites.Parse("<div class='luf'><h3>Novel &amp; Title</h3><ul><li><a href='/chapter-2/'>Chapter 2</a></li><li><a href='https://evil.example/ch'>bad</a></li></ul></div>", new Uri("https://kolnovel.com/"));
        Check(kol.Count == 1 && kol[0].Title == "Novel & Title — Chapter 2", "Kolnovel title, chapter and same-site link");
        var olympus = ReadingSites.Parse("<div class='last-chapter'><div class='info'><h3>Series</h3><ul><li><a href='#' data-bs-toggle='modal'>Paid</a></li><li><a href='/series/name/2'>Chapter 2</a></li></ul></div></div>", new Uri("https://olympustaff.com/"));
        Check(olympus.Count == 1 && olympus[0].Url.EndsWith("/2"), "Olympus excludes purchase-only chapter links");
        var manga = ReadingSites.Parse("<div id='loop-content'><div class='item-summary'><div class='post-title'><a>Series</a></div><span class='chapter font-meta'><a href='/manga/name/4/'>4</a></span></div></div>", new Uri("https://mangalik.net/"));
        Check(manga.Single().Title == "Series — 4", "Mangalek associates series with chapter");
        try { ReadingSites.Parse("<html>Changed layout</html>", new Uri("https://kolnovel.com/")); throw new Exception("Empty page accepted"); }
        catch (ArgumentException) { Console.WriteLine("PASS layout changes fail without resetting history"); }
        var origin = new Uri("https://example.com/series/feed");
        var rss = Encoding.UTF8.GetBytes("<rss><channel><item><guid>ch2</guid><title>Chapter 2</title><link>https://example.com/ch2</link></item><item><guid>ch1</guid><title>Chapter 1</title><link>https://example.com/ch1</link></item></channel></rss>");
        var entries = ReadingFeeds.Parse(rss, origin);
        Check(entries.Count == 2, "RSS chapters parse");
        var watch = new ReadingWatch { Name = "Test", Seen = entries.Select(e => e.Key).ToList() };
        Check(ChapterService.Pending(watch, entries).Count == 0, "baseline does not flood historical chapters");
        watch.Seen.Remove(entries[0].Key);
        Check(ChapterService.Pending(watch, entries).Single().Title == "Chapter 2", "only unseen chapter is pending");
        var atom = Encoding.UTF8.GetBytes("<feed xmlns='http://www.w3.org/2005/Atom'><entry><id>tag:chapter3</id><title>Chapter 3</title><link rel='alternate' href='/ch3'/></entry><entry><id>tag:chapter3</id><title>Edited title</title><link href='/ch3'/></entry></feed>");
        var parsed = ReadingFeeds.Parse(atom, origin);
        Check(parsed.Count == 1 && parsed[0].Url == "https://example.com/ch3", "Atom relative links and stable-ID deduplication");
        try { ReadingFeeds.Parse(Encoding.UTF8.GetBytes("<!DOCTYPE rss [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><rss>&x;</rss>"), origin); throw new Exception("External XML entity accepted"); }
        catch (System.Xml.XmlException) { Console.WriteLine("PASS external XML entities prohibited"); }
        try { ReadingFeeds.Parse(Encoding.UTF8.GetBytes("<html>Not a feed</html>"), origin); throw new Exception("HTML was accepted as a feed"); }
        catch (ArgumentException) { Console.WriteLine("PASS unsupported HTML fails clearly"); }
        foreach (var address in new[] { "127.0.0.1", "127.0.0.1", "172.16.0.1", "127.0.0.1", "169.254.169.254", "::1", "::ffff:127.0.0.1", "fd00::1" })
            Check(!ReadingFeeds.IsPublic(IPAddress.Parse(address)), "private source blocked: " + address);
        foreach (var url in new[] { "http://example.com/feed", "https://localhost/feed", "https://127.0.0.1/feed", "https://user:secret@example.com/feed", "https://example.com:444/feed" })
        {
            try { ReadingFeeds.SourceUri(url); throw new Exception("Unsafe feed URI accepted"); }
            catch (ArgumentException) { Console.WriteLine("PASS unsafe feed URL rejected"); }
        }
        var directory = Path.Combine(Path.GetTempPath(), "gliv-reading-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new ReadingStore(directory); first.Watches.Add(watch); first.Save();
            var second = new ReadingStore(directory);
            Check(second.Watches.Single().Seen.SequenceEqual(watch.Seen), "restart preserves delivered chapter identities");
            Check(ChapterService.Pending(second.Watches.Single(), entries).Count == 1, "undelivered chapter remains pending after restart");
            File.WriteAllText(Path.Combine(directory, "chapters.json"), "broken");
            try { _ = new ReadingStore(directory); throw new Exception("Corrupt history silently reset"); }
            catch (System.Text.Json.JsonException) { Console.WriteLine("PASS corrupt history never silently resets"); }
        }
        finally { Directory.Delete(directory, true); }
    }
}

