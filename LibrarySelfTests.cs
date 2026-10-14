namespace GreatLoveImmortalVenerable;
public static class LibrarySelfTests
{
    public static void Run()
    {
        var catalog = LibraryCatalog.Load();
        if (catalog.Entries.Count == 0 || catalog.MessageCount == 0 || catalog.Categories.Count == 0)
            throw new Exception("Library coverage mismatch.");
        if (catalog.Entries.Select(e => e.Id).Distinct().Count() != catalog.Entries.Count
            || catalog.Entries.Sum(e => e.MessageUrls.Length) != catalog.MessageCount)
            throw new Exception("Original messages or stable IDs lost.");
        foreach (var category in catalog.Categories)
        {
            var entries = catalog.Entries.Where(e => e.Category == category.Key).ToList();
            if (entries.Count == 0) throw new Exception("Empty category.");
            for (var page = 1; page <= (entries.Count + 5) / 6; page++)
                if (LibraryCatalog.Page(entries, page).Length > 3900) throw new Exception("Category page would be truncated.");
        }
        for (var page = 1; page <= (catalog.Entries.Count + 5) / 6; page++)
            if (LibraryCatalog.Page(catalog.Entries, page).Length > 3900) throw new Exception("Full archive page would be truncated.");
        if (!LibraryCatalog.Page([], 1).Contains("results")) throw new Exception("Empty search not explained.");
        if (!LibraryCatalog.Page(catalog.Entries, 1000).Contains("not found")) throw new Exception("Invalid page not handled.");
        foreach (var name in new[] { "library.md", "library.html" })
            if (new FileInfo(Path.Combine(LibraryCatalog.DirectoryPath, name)).Length == 0) throw new Exception("Empty export.");
        Console.WriteLine("PASS all source-message references retained across unique IDs.");
        Console.WriteLine("PASS all categories and archive pages fit Discord embed limits.");
        Console.WriteLine("PASS invalid pages and empty results handled; both export files readable.");
    }
}

