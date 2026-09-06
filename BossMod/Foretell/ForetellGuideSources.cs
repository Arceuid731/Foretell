using System.Text.Json;

namespace BossMod.Foretell;

internal sealed record GuideSourcePage(string Provider, string Url, string Text, string Original, string Format)
{
    public DateTime RetrievedAt { get; init; }
    public DateTime? ModifiedAt { get; init; }
    public bool FromCache { get; init; }
    public string Fingerprint => GuideNames.Hash(Provider + "\n" + Url + "\n" + Text + "\n" + Original + "\n" + Format);
}

internal sealed record GuideProviderState(string Provider, string Status, string Url, string Error = "");
internal sealed record GuideSourceBundle(GuideSourcePage[] Sources, GuideProviderState[] States);

internal static class GuideSourceAssembly
{
    internal const int MaximumBytes = 8 * 1024 * 1024;

    public static GuideDocument Combine(GuideDuty duty, GuideSourceBundle bundle, DateTime at)
    {
        if (bundle.Sources.Length is 0 or > 4) throw new System.IO.InvalidDataException("No guide source is available.");
        if (bundle.Sources.Any(page => page == null || string.IsNullOrWhiteSpace(page.Text) || page.Original == null
            || !Uri.TryCreate(page.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps))
            throw new System.IO.InvalidDataException("Invalid guide source.");
        var pages = bundle.Sources.OrderBy(page => page.Provider == "Console Games Wiki" ? 0 : page.Provider == "Community Workbook" ? 2 : 1)
            .ThenBy(page => page.Provider, StringComparer.Ordinal).ToArray();
        var original = JsonSerializer.Serialize(pages.Select(page => new { page.Provider, page.Url, page.Fingerprint, page.Format }));
        var text = string.Join("\n\n", pages.Select((page, index) => $"<document id=\"{index + 1}\" provider=\"{page.Provider}\" url=\"{page.Url}\">\n{page.Text}\n</document>"));
        if (pages.Sum(page => (long)Encoding.UTF8.GetByteCount(page.Original)) > MaximumBytes || Encoding.UTF8.GetByteCount(text) > MaximumBytes)
            throw new System.IO.InvalidDataException("Combined guide sources exceed size limits.");
        return new(GuideDocument.CurrentSchema, duty, duty.EnglishName, 1, at, GuideNames.Hash(original), [])
        {
            Page = new(string.Join(" · ", pages.Select(page => page.Provider)), pages[0].Url, text, original),
            Sources = pages, Providers = bundle.States
        };
    }

    internal static GuideSourcePage[] EvidenceSources(GuideDocument? document, GuideAdvice? advice)
    {
        if (document == null || advice == null) return [];
        var quotes = advice.Evidence.Select(NormalizeEvidence).ToArray();
        return document.Sources.Where(page =>
        {
            var text = NormalizeEvidence(page.Text);
            return quotes.Any(quote => text.Contains(quote, StringComparison.Ordinal));
        }).ToArray();
    }

    internal static string NormalizeEvidence(string text) => GuideNames.Normalize(text.Replace("\\r", " ").Replace("\\n", " ").Replace("\\t", " ").Replace("\\\"", "\""));
}
