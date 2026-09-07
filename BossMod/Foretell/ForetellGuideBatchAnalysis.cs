using System.IO;
using System.Text.Json;
using System.Threading;

namespace BossMod.Foretell;

internal static partial class GuidePageAnalysis
{
    private static async Task<Dictionary<string, string>> DraftBosses(GuideDocument source, string[] names, GuideLanguage language, int contextTokens,
        IGuideSummaryModel model, CancellationToken cancellation, Action<GuideAnalysisStep>? trace)
    {
        var drafts = new Dictionary<string, string>();
        if (names.Length < 2) return drafts;
        var paragraphs = Paragraphs(source.Page!.Text);
        var numbered = string.Join('\n', paragraphs.Select((paragraph, index) => $"[{index + 1}] {paragraph}"));
        var roster = string.Join(", ", names);
        try
        {
            trace?.Invoke(new("Draft", roster, 1, "Analyzing the requested bosses together against the complete source."));
            var generated = await model.Analyze(Prompt(language),
                $"Instance: {source.Title}\nRequested bosses: {roster}\n<source>\n{numbered}\n</source>\nReturn ALL requested bosses in encounter order, with each boss's complete named AND unnamed mechanics. The roster was identified from the full source. Keep these exact boss names. Do not add other encounters or difficulties. Resolve numbered workbook boss columns using this roster and the other documents. Keep separate bosses' mechanics separate, including similarly named attacks. Preserve phase headings and all conditional alternatives. Evidence contains paragraph IDs, not quotations. Return compact JSON.",
                CitedSchema(paragraphs.Length), Math.Clamp(contextTokens / 3, 2048, 8192), cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            var response = JsonSerializer.Deserialize<Response>(generated, Json);
            if (response?.Bosses == null || response.Bosses.Length > 32)
                throw new InvalidDataException("Invalid combined boss response.");
            var serialization = new JsonSerializerOptions(Json) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            foreach (var name in names)
            {
                var key = GuideNames.Boss(name);
                var matches = response.Bosses.Where(boss => boss != null && !string.IsNullOrWhiteSpace(boss.Name) && GuideNames.Boss(boss.Name) == key).ToArray();
                if (matches.Length != 1)
                {
                    trace?.Invoke(new("Validation", name, 1, "Missing or duplicate boss in combined response; analyzing this boss separately."));
                    continue;
                }
                drafts[key] = JsonSerializer.Serialize(new Response(response.Summary, matches), serialization);
            }
        }
        catch (Exception error) when (error is GuideContextException or GuideOutputException or JsonException or InvalidDataException)
        {
            trace?.Invoke(new("Split", roster, 1, "Combined response unavailable; preserving per-boss validation and progress. " + error.Message));
        }
        return drafts;
    }
}
