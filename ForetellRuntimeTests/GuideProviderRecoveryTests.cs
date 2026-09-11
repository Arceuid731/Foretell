using BossMod.Foretell;
using System.Net;
using System.Text;
using System.Text.Json;

internal static partial class GuideProviderTests
{
    internal static async Task Smoke(string directory, string[] titles)
    {
        Directory.CreateDirectory(directory);
        using var providers = new ForetellGuideProviders(Path.Combine(directory, "providers"));
        foreach (var title in titles)
        {
            var bundle = await providers.Fetch(new GuideDuty(1, 1, title), CancellationToken.None);
            File.WriteAllText(Path.Combine(directory, GuideNames.Hash(title) + ".json"), JsonSerializer.Serialize(bundle));
            Console.WriteLine(JsonSerializer.Serialize(new { title, bundle.States, Sources = bundle.Sources.Select(source => new { source.Provider, source.Url, source.Format, Characters = source.Text.Length }) }));
        }
    }

    private static string ApiEntry(string title, string slug, string html = GuideHtml) => JsonSerializer.Serialize(new
    {
        id = 759, title = new { rendered = title }, link = "https://ravensreminders.com/tldrguide/" + slug + "/",
        type = "tldr_guide", status = "publish", modified_gmt = "2022-03-08T22:43:01", content = new { rendered = html, @protected = false }
    });

    private static async Task RavenApiAcquisition()
    {
        using var directory = new TestDirectory();
        var requests = 0;
        var offline = false;
        var first = "[" + string.Join(",", Enumerable.Range(0, 100).Select(i => ApiEntry("Other " + i, "other-" + i))) + "]";
        var second = "[" + ApiEntry("Orbonne Monastery, The", "orbonne-monastery-the") + "]";
        using var handler = new FakeHandler((request, _) =>
        {
            if (!request.RequestUri!.AbsoluteUri.StartsWith(ForetellGuideProviders.RavenApi, StringComparison.Ordinal)) return Task.FromResult(Status(HttpStatusCode.Forbidden));
            Check(request.Version == HttpVersion.Version20 && request.VersionPolicy == HttpVersionPolicy.RequestVersionOrLower, "Raven did not negotiate HTTP/2 with legacy fallback.");
            ++requests;
            if (offline) return Task.FromResult(Status(HttpStatusCode.Forbidden));
            return Task.FromResult(Validated(Text(request.RequestUri.Query.Contains("&page=2", StringComparison.Ordinal) ? second : first, "application/json")));
        });
        using var providers = new ForetellGuideProviders(directory.Path, handler);
        var original = await providers.Fetch(Duty, CancellationToken.None);
        var source = Page(original, ForetellGuideProviders.RavenProvider);
        Check(requests == 2 && source.Format == "wordpress-json" && source.Text.Contains("icon: shield-alt", StringComparison.Ordinal)
            && source.Original.Contains("modified_gmt", StringComparison.Ordinal) && source.ModifiedAt?.Year == 2022, "Paginated API lost guide content, roles or provenance.");
        offline = true;
        var cached = await providers.Fetch(Duty, CancellationToken.None);
        Check(requests == 2 && Page(cached, source.Provider).FromCache, "Raven API catalogue was downloaded again inside shared lifetime.");
        var fallback = await providers.Fetch(Duty, CancellationToken.None, true);
        Check(requests == 4 && Page(fallback, source.Provider).ContentFingerprint == source.ContentFingerprint
            && State(fallback, source.Provider).Error.Length > 0, "API offline refresh lost the verified cached source or error.");
        var other = await providers.Fetch(Duty with { EnglishName = "Unlisted duty" }, CancellationToken.None);
        Check(other.Sources.Length == 0 && State(other, source.Provider).Status == "Missing", "Cached API catalog reused another duty's guide.");
        Check(ForetellGuideProviders.ReadRavenJson(Encoding.UTF8.GetBytes(second), Duty with { EnglishName = Duty.EnglishName + " (Savage)" }).Matches.Length == 0,
            "API substituted the normal guide for Savage.");
        foreach (var invalid in new[] { second.Replace("https://ravensreminders.com/tldrguide/", "https://example.com/tldrguide/"), second.Replace("\"protected\":false", "\"protected\":true"), "[" + ApiEntry(Duty.EnglishName, "orbonne", "") + "]" })
        {
            try { ForetellGuideProviders.ReadRavenJson(Encoding.UTF8.GetBytes(invalid), Duty); throw new Exception("Invalid API source accepted."); }
            catch (Exception error) when (error is InvalidDataException || error.Message is "Raven API guide is protected." or "Raven API guide contains no usable text.") { }
        }
    }

    private static async Task PalaceWikiIdentity()
    {
        using var directory = new TestDirectory();
        var duty = new GuideDuty(176, 563, "the Palace of the Dead (Floors 21-30)");
        var wrongFloor = false;
        using var handler = new FakeHandler((request, _) =>
        {
            if (!Wiki(request)) return Task.FromResult(Status(HttpStatusCode.NotFound));
            Check(Uri.UnescapeDataString(request.RequestUri!.Query).EndsWith("page=Palace of the Dead (Floors 21-30)", StringComparison.Ordinal), "Palace request retained the nonexistent leading article.");
            return Task.FromResult(Text(WikiJson(GuideHtml, wrongFloor ? "Palace of the Dead (Floors 31-40)" : "Palace of the Dead (Floors 21-30)"), "application/json"));
        });
        using var providers = new ForetellGuideProviders(directory.Path, handler);
        var document = GuideSourceAssembly.Combine(duty, await providers.Fetch(duty, CancellationToken.None), DateTime.UtcNow);
        Check(document.Duty == duty && document.Title == duty.EnglishName && document.Sources.Length == 1, "Source title alias changed live duty identity.");
        wrongFloor = true;
        using var fresh = new TestDirectory();
        using var otherProviders = new ForetellGuideProviders(fresh.Path, handler);
        Check((await otherProviders.Fetch(duty, CancellationToken.None)).Sources.Length == 0, "Palace substituted another floor set.");
    }
}
