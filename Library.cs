using System.Text.Json;
using NetCord;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace GreatLoveImmortalVenerable;

public sealed record LibraryCategory(string Key, string Label);
public sealed class LibraryEntry
{
    public int Id { get; set; }
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Url { get; set; } = "";
    public string CurrentUrl { get; set; } = "";
    public string[] OriginalUrls { get; set; } = [];
    public string[] MessageUrls { get; set; } = [];
    public string Status { get; set; } = "";
    public string Note { get; set; } = "";
    public string Recommendation { get; set; } = "";
    public bool Recommended { get; set; }
}
public sealed class LibraryCatalog
{
    public string ReviewedOn { get; set; } = "";
    public int MessageCount { get; set; }
    public List<LibraryCategory> Categories { get; set; } = [];
    public List<LibraryEntry> Entries { get; set; } = [];
    public static string DirectoryPath => Environment.GetEnvironmentVariable("READING_DATA_DIR") ?? "/app/data";
    public static LibraryCatalog Load() => JsonSerializer.Deserialize<LibraryCatalog>(
        File.ReadAllText(Path.Combine(DirectoryPath, "library.json")))
        ?? throw new InvalidOperationException("Library catalog unavailable.");
    public static string Page(IEnumerable<LibraryEntry> source, int page)
    {
        var entries = source.ToList();
        var pages = Math.Max(1, (entries.Count + 5) / 6);
        if (page < 1 || page > pages) return $"Page not found. Available pages: 1–{pages}.";
        if (entries.Count == 0) return "No results found. Try a tool name or another keyword.";
        return string.Join("\n\n", entries.Skip((page - 1) * 6).Take(6).Select(e =>
            $"**#{e.Id} [{e.Name}]({e.CurrentUrl})**\n{e.Summary}\n*{e.Status}*"))
            + $"\n\nPage {page}/{pages} · {entries.Count} references\nDetails: /library item · Full index: /library export";
    }
}
[SlashCommand("library", "Owner: your organized saved links and descriptions", DefaultGuildPermissions = Permissions.Administrator)]
public sealed class LibraryCommands(CommandGate gate) : ApplicationCommandModule<ApplicationCommandContext>
{
    private Task Run(Func<LibraryCatalog, EmbedProperties> render) => gate.Execute(Context,
        () => Task.FromResult(render(LibraryCatalog.Load())), true);

    [SubSlashCommand("index", "Show all categories, counts and how to browse your archive")]
    public Task Index() => Run(c => Embeds.Build("Your organized library",
        $"**{c.MessageCount} messages → {c.Entries.Count} references → {c.Categories.Count} categories**\nSources reviewed: {c.ReviewedOn}\n\n"
        + string.Join("\n", c.Categories.Select(k => $"**{k.Key}** — {k.Label} ({c.Entries.Count(e => e.Category == k.Key)})"))
        + "\n\n/library browse category:android page:1\n/library search query:Steam\n/library item id:42\n/library recommended\n/library export\n\nThis catalog contains references and review notes. New links are not added automatically."));
    [SubSlashCommand("browse", "Browse six saved links per page; use index for category names")]
    public Task Browse(string category = "all", int page = 1) => Run(c =>
    {
        var selected = c.Categories.FirstOrDefault(k => k.Key.Equals(category, StringComparison.OrdinalIgnoreCase)
            || k.Label.Equals(category, StringComparison.OrdinalIgnoreCase));
        if (!category.Equals("all", StringComparison.OrdinalIgnoreCase) && selected is null)
            return Embeds.Build("Category not found", "Use /library index to see categories, or all to browse the entire library.");
        var items = c.Entries.Where(e => selected is null || e.Category == selected.Key);
        return Embeds.Build(selected?.Label ?? "All references", LibraryCatalog.Page(items, page));
    });
    [SubSlashCommand("search", "Search names, descriptions, categories and review notes")]
    public Task Search(string query, int page = 1) => Run(c =>
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 150) return Embeds.Build("Search", "Enter a keyword or tool name, up to 150 characters.");
        query = query.Trim();
        var items = c.Entries.Where(e => (e.Name + " " + e.Summary + " " + e.Category + " " + e.Note + " " + e.Status)
            .Contains(query, StringComparison.OrdinalIgnoreCase));
        return Embeds.Build("Search results", LibraryCatalog.Page(items, page));
    });
    [SubSlashCommand("item", "Show one saved link, review status, notes and original Discord messages")]
    public Task Item(int id) => Run(c =>
    {
        var entry = c.Entries.FirstOrDefault(e => e.Id == id);
        if (entry is null) return Embeds.Build("Reference not found", "Use /library search or /library browse to find an entry ID.");
        var category = c.Categories.First(k => k.Key == entry.Category).Label;
        return Embeds.Build($"#{entry.Id} — {entry.Name}",
            $"**{category}**\n\n{entry.Summary}\n\n**Status:** {entry.Status}\n[Open source]({entry.CurrentUrl})"
            + (entry.Note.Length > 0 ? $"\n\n**Note:** {entry.Note}" : "")
            + (entry.Recommendation.Length > 0 ? $"\n\n**Recommendation:** {entry.Recommendation}" : "")
            + "\n\n**Original messages:** " + string.Join(" · ", entry.MessageUrls.Select((u, i) => $"[Message {i + 1}]({u})"))
            + $"\n\nReviewed {c.ReviewedOn} · Documentation review; software was not executed.");
    });
    [SubSlashCommand("recommended", "Show the saved tools most relevant to your PC, phone and server")]
    public Task Recommended(int page = 1) => Run(c => Embeds.Build("Recommended references",
        LibraryCatalog.Page(c.Entries.Where(e => e.Recommended), page)));

    [SubSlashCommand("export", "Download the complete library as Markdown and searchable HTML")]
    public async Task Export()
    {
        if (!await gate.Begin(Context, true)) return;
        try
        {
            await using var markdown = File.OpenRead(Path.Combine(LibraryCatalog.DirectoryPath, "library.md"));
            await using var html = File.OpenRead(Path.Combine(LibraryCatalog.DirectoryPath, "library.html"));
            await Context.Interaction.ModifyResponseAsync(p =>
            {
                p.Content = "Your library export: Markdown for reading and HTML for viewing in a browser.";
                p.Attachments = [new AttachmentProperties("library.md", markdown), new AttachmentProperties("library.html", html)];
            });
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Library export failed: {e.GetType().Name}");
            await Context.Interaction.ModifyResponseAsync(p => p.Content = "The library export is unavailable. Try /library index.");
        }
    }
}

