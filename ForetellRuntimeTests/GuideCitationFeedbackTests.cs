using BossMod.Foretell;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class GuideCitationFeedbackTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private const string Passage = "Ixtab casts Scream if adds remain; kill the adds. Shadow Flare damages everyone; heal the group.";
    private static string Draft(bool firstFixed, bool secondFixed) => JsonSerializer.Serialize(new
    {
        summary = "Kill adds and prepare group healing.", bosses = new[] { new
        {
            name = "Ixtab", displayName = "Ixtab", summary = "Kill adds and prepare group healing.", phaseDefinitions = Array.Empty<GuidePhaseDefinition>(),
            mechanics = new[] { Mechanic("Scream", "Kill the adds", firstFixed), Mechanic("Shadow Flare", "Heal the group", secondFixed) }
        } }
    }, Json);

    private static object Mechanic(string name, string cue, bool fixedEvidence) => new
    {
        name, displayName = name, cue, description = Passage, triggerKind = "cast", triggerName = name,
        evidence = new[] { fixedEvidence ? "111" : "110" }, responses = Array.Empty<GuideResponse>(), roles = Array.Empty<string>(), conflict = "",
        phaseMemberships = Array.Empty<GuidePhaseMembership>(), contextOnly = false,
        triggers = new[] { new GuideTrigger("cast", name, cue, [fixedEvidence ? "111" : "110"]) }
    };

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    internal static async Task Run()
    {
        var paragraphs = Enumerable.Range(1, 109).Select(i => i == 1 ? "[Ixtab (Palace of the Dead)] Ixtab" : "Navigation row " + i)
            .Concat(["Level 59", Passage]).ToArray();
        var text = string.Join('\n', paragraphs);
        var source = new GuideDocument(GuideDocument.CurrentSchema, new(177, 564, "the Palace of the Dead (Floors 31-40)"),
            "the Palace of the Dead (Floors 31-40)", 1, DateTime.UtcNow, GuideNames.Hash(text), [])
            { Page = new("Fixture", "https://example.invalid/palace", text, "") };
        var profile = GuideModelCatalog.Get(GuideModelCatalog.DefaultID);
        try
        {
            GuidePageAnalysis.Parse(source, text, GuidePageAnalysis.ResolveEvidence(Draft(false, false), paragraphs), GuideLanguage.English, profile);
            throw new Exception("Wrong source paragraph passed validation.");
        }
        catch (InvalidDataException error) { Check(error.Message.Contains("exact original name", StringComparison.Ordinal), "Fixture did not reproduce the recorded citation failure."); }
        var feedback = GuidePageAnalysis.CitationFeedback(Draft(false, false), paragraphs);
        Check(feedback.Contains("Cited [110]: Level 59", StringComparison.Ordinal) && feedback.Contains("Candidate [111]: " + Passage, StringComparison.Ordinal),
            "Repair feedback did not distinguish the incorrect level paragraph from the actual combat paragraph.");
        Check(GuidePageAnalysis.CitationFeedback(Draft(true, true), paragraphs) == "", "Valid citations triggered unnecessary repair feedback.");
        Check(GuidePageAnalysis.VisibleBossName("Ixtab (Palace of the Dead)", text) == "Ixtab"
            && GuidePageAnalysis.VisibleBossName("Ixtab (Savage)", text) == "Ixtab (Savage)"
            && GuidePageAnalysis.VisibleBossName("Ixtab (Palace of the Dead)", "Ixtab (Palace of the Dead)") == "Ixtab (Palace of the Dead)",
            "Boss alias resolution ignored the source's visible heading or stripped an unrelated suffix.");
        using var model = new RepairModel();
        var prepared = await GuidePageAnalysis.Compile(source, GuideLanguage.English, profile, 65536, model, (_, _) => { }, CancellationToken.None);
        Check(model.Reviews == 2 && prepared.Bosses.Single().Name == "Ixtab" && prepared.MechanicCount == 2
            && GuidePageAnalysis.ValidPrepared(prepared, source, GuideLanguage.English, profile), "Citation repair failed strict validation, cache reuse or live boss identity.");
        Console.WriteLine("Palace citation regression: wrong paragraph rejected, explicit numbered feedback, latest draft retained and source-backed boss label passed (stub model).");
    }

    private sealed class RepairModel : IGuideSummaryModel
    {
        public int Reviews;
        public GuideModelRuntime Runtime => new();
        public Task Start(Action<GuideModelProgress> progress, CancellationToken cancellation) => Task.CompletedTask;
        public Task<string> Summarize(string source, GuideLanguage language, CancellationToken cancellation) => throw new NotSupportedException();
        public Task<string> Analyze(string system, string source, object schema, int outputTokens, CancellationToken cancellation)
        {
            if (system.StartsWith("Read the ENTIRE", StringComparison.Ordinal))
                return Task.FromResult("{\"bosses\":[{\"name\":\"Ixtab (Palace of the Dead)\",\"passages\":[\"1\",\"110\",\"111\"]}]}");
            if (!system.StartsWith("Audit this", StringComparison.Ordinal)) return Task.FromResult(Draft(false, false));
            ++Reviews;
            Check(source.Contains("Cited [110]: Level 59", StringComparison.Ordinal) && source.Contains("Candidate [111]", StringComparison.Ordinal), "Review lacks actionable citation diagnostics.");
            if (Reviews == 2)
            {
                var start = source.IndexOf("<Draft>\n", StringComparison.Ordinal) + 8;
                var end = source.IndexOf("\n</Draft>", start, StringComparison.Ordinal);
                var draft = JsonNode.Parse(source[start..end])!;
                Check(draft["bosses"]![0]!["mechanics"]![0]!["evidence"]![0]!.GetValue<string>() == "111", "Second review restarted from the original invalid draft.");
            }
            return Task.FromResult(Draft(true, Reviews == 2));
        }
        public void Dispose() { }
    }
}
