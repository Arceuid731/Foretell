using BossMod.Foretell;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal static class GuideFactsTests
{
    private const string Spark = "Spark: Red mark means move out.";
    private const string Blue = "With a blue mark, Spark instead requires moving in.";
    private const string Pulse = "Pulse: Unavoidable damage to everyone.";
    private const string Bond = "Bond: A tether appears. Its response is undocumented.";
    private static readonly string[] Lines = ["Sentinel", Spark, Blue, Pulse, "Keeper", Bond, "Unrelated page footer"];
    private static readonly GuideModelProfile Profile = GuideModelCatalog.Get(GuideModelCatalog.DefaultID);
    private static GuideDocument Source() => new(1, new(913, 914, "Fact fixture"), "Fact fixture", 1, DateTime.UtcNow, GuideNames.Hash(string.Join('\n', Lines)), [])
    { Page = new("Fixture", "https://example.invalid/facts", string.Join('\n', Lines), "") };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    internal static async Task Run()
    {
        using (var model = new FactsModel())
        {
            var partials = new List<GuideDocument>();
            var result = await Compile(model, partials.Add);
            Check(model.Order.SequenceEqual(["outline", "facts:Sentinel", "draft:Sentinel", "facts:Keeper", "draft:Keeper"]), "Fact analysis batched bosses or skipped a model stage.");
            Check(model.FullOutline && model.Focused && result.MechanicCount == 3, "Fact analysis lost full-source reading or crossed boss contexts.");
            Check(partials.Select(document => document.MechanicCount).SequenceEqual([2, 3])
                && partials.All(document => !GuidePageAnalysis.ValidPrepared(document, Source(), GuideLanguage.English, Profile))
                && GuidePageAnalysis.ValidPrepared(result, Source(), GuideLanguage.English, Profile), "Progress published incomplete coverage as a finished cache.");
            var spark = result.Bosses[0].Phases[0].Mechanics.Single(mechanic => mechanic.Name == "Spark");
            Check(spark.Advice!.Responses.Length == 2 && spark.Advice.ShortCue == "Red: out; blue: in", "Fact presentation dropped opposing branches.");
        }
        using (var model = new FactsModel { OmitNamed = true })
        {
            var result = await Compile(model);
            Check(model.Repairs == 1 && result.MechanicCount == 3, "Named fact disappeared from the draft without a focused correction.");
        }
        using (var model = new FactsModel { OmitBranchEvidence = true })
        {
            var result = await Compile(model);
            Check(model.Repairs == 1 && result.Bosses[0].Phases[0].Mechanics.Single(mechanic => mechanic.Name == "Spark").Advice!.Evidence.Contains(Blue),
                "A draft silently dropped the source of an extracted alternative.");
        }
        using (var model = new FactsModel { WrongFactCitation = true })
        {
            await Compile(model);
            Check(model.FactRetries == 1 && model.FactFeedback, "Fact citation retry omitted its previous response or matching numbered paragraphs.");
        }
        using (var model = new FactsModel { FailKeeper = true })
        {
            var partials = new List<GuideDocument>();
            try { await Compile(model, partials.Add); throw new Exception("Invalid later boss accepted."); }
            catch (InvalidDataException) { }
            Check(partials is [{ MechanicCount: 2 }] && partials[0].Bosses[1].Phases.Length == 0, "A failed later boss replaced validated progress or became visible.");
        }
        using (var cancel = new CancellationTokenSource())
        using (var model = new FactsModel())
        {
            try { await Compile(model, _ => cancel.Cancel(), cancel.Token); throw new Exception("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            Check(!model.Order.Contains("facts:Keeper"), "Cancelled progressive analysis still queried the next boss.");
        }
        var valid = FactsModel.Facts(new Dictionary<string, string> { ["1"] = Bond });
        GuidePageAnalysis.ValidateFacts(valid, [Bond]);
        foreach (var defect in new[] { "name", "paragraph", "unknown-action", "duplicate" })
        {
            var broken = JsonNode.Parse(valid)!;
            var mechanic = broken["mechanics"]![0]!;
            var branch = mechanic["branches"]![0]!;
            if (defect == "name") mechanic["name"] = "Fabricated Ability";
            if (defect == "paragraph") branch["evidence"]![0] = "2";
            if (defect == "unknown-action") branch["playerAction"] = "Move away";
            if (defect == "duplicate") broken["mechanics"]!.AsArray().Add(mechanic.DeepClone());
            try { GuidePageAnalysis.ValidateFacts(broken.ToJsonString(), [Bond]); throw new Exception("Invalid fact evidence accepted: " + defect); }
            catch (InvalidDataException) { }
        }
        Console.WriteLine("Guide facts: staged model calls, opposing branches, named-mechanic coverage, source paragraph references, unknown responses, partial failure and cancellation passed (stub models).");
    }

    private static Task<GuideDocument> Compile(FactsModel model, Action<GuideDocument>? ready = null, CancellationToken cancellation = default)
        => GuidePageAnalysis.Compile(Source(), GuideLanguage.English, Profile, 32768, model, (_, _) => { }, cancellation, ready, strategy: GuideAnalysisStrategy.EvidenceFirst);

    private sealed class FactsModel : IGuideSummaryModel
    {
        internal List<string> Order = [];
        internal bool FullOutline, Focused = true, OmitNamed, FailKeeper, OmitBranchEvidence, WrongFactCitation, FactFeedback;
        internal int Repairs, FactRetries;
        public GuideModelRuntime Runtime => new();
        public Task Start(Action<GuideModelProgress> progress, CancellationToken cancellation) => Task.CompletedTask;
        public Task<string> Summarize(string source, GuideLanguage language, CancellationToken cancellation) => throw new NotSupportedException();
        public void Dispose() { }

        internal static string Facts(Dictionary<string, string> paragraphs)
        {
            return JsonSerializer.Serialize(new { mechanics = paragraphs.Where(entry => entry.Value.Contains(": ", StringComparison.Ordinal)).Select(entry =>
            {
                var name = entry.Value.Split(':')[0];
                var branches = new[] { new
                {
                    when = "", actor = name == "Bond" ? "Keeper" : "Sentinel", target = "Players", effect = entry.Value,
                    playerAction = name == "Bond" ? "" : name == "Spark" ? "Red: move out" : "Heal and mitigate",
                    actionBasis = name == "Bond" ? "unknown" : "terminology", evidence = new[] { entry.Key }
                } }.ToList();
                if (name == "Spark") branches.Add(new { when = "Blue mark", actor = "Sentinel", target = "Players", effect = Blue,
                    playerAction = "Move in", actionBasis = "explicit", evidence = new[] { paragraphs.Single(pair => pair.Value == Blue).Key } });
                return new { name, named = true, branches };
            }) });
        }

        public Task<string> Analyze(string system, string source, object schema, int outputTokens, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (system.StartsWith("Read the ENTIRE", StringComparison.Ordinal))
            {
                Order.Add("outline"); FullOutline = Lines.All(line => source.Contains(line, StringComparison.Ordinal));
                return Task.FromResult("{\"bosses\":[{\"name\":\"Sentinel\",\"passages\":[\"1\",\"2\",\"3\",\"4\"]},{\"name\":\"Keeper\",\"passages\":[\"5\",\"6\"]}]}");
            }
            var boss = source.StartsWith("Boss: Sentinel", StringComparison.Ordinal) ? "Sentinel" : "Keeper";
            Focused &= boss == "Sentinel" ? !source.Contains(Bond, StringComparison.Ordinal) : !source.Contains(Spark, StringComparison.Ordinal);
            var paragraphs = Regex.Matches(source.Split("</source>")[0], @"(?m)^\[(\d+)\] (.+)$").ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value);
            if (system.StartsWith("Extract the complete combat facts", StringComparison.Ordinal))
            {
                Order.Add("facts:" + boss);
                if (FailKeeper && boss == "Keeper") throw new InvalidDataException("Unusable second boss evidence.");
                var facts = Facts(paragraphs);
                if (WrongFactCitation && boss == "Keeper")
                {
                    if (FactRetries++ == 0)
                    {
                        var wrong = JsonNode.Parse(facts)!;
                        wrong["mechanics"]![0]!["branches"]![0]!["evidence"]![0] = "1";
                        return Task.FromResult(wrong.ToJsonString());
                    }
                    --FactRetries;
                    FactFeedback = source.Contains("<previous-facts>", StringComparison.Ordinal) && source.Contains("Paragraphs naming Bond:\n[2] " + Bond, StringComparison.Ordinal);
                }
                return Task.FromResult(facts);
            }
            var repair = system.StartsWith("Audit this FFXIV", StringComparison.Ordinal);
            if (repair) ++Repairs;
            Order.Add("draft:" + boss);
            Check(system.Contains(GuidePageAnalysis.VocabularyPrompt, StringComparison.Ordinal), "Draft/repair did not share the same vocabulary definitions.");
            var mechanics = paragraphs.Where(entry => entry.Value.Contains(": ", StringComparison.Ordinal))
                .Where(entry => !OmitNamed || repair || !entry.Value.StartsWith("Spark:", StringComparison.Ordinal))
                .Select(entry =>
                {
                    var name = entry.Value.Split(':')[0];
                    var cue = name == "Spark" ? "Red: out; blue: in" : name == "Pulse" ? "Heal and mitigate" : "Watch the tether";
                    return new { name, displayName = name, cue, cueScope = "complete", description = entry.Value + (name == "Spark" ? " " + Blue : ""), triggerKind = "cast", triggerName = name,
                        evidence = name == "Spark" && (!OmitBranchEvidence || repair) ? new[] { entry.Key, paragraphs.Single(pair => pair.Value == Blue).Key } : new[] { entry.Key }, triggers = Array.Empty<GuideTrigger>(),
                        responses = name == "Spark" ? new[] { new { when = "Red mark", instruction = "Move out" }, new { when = "Blue mark", instruction = "Move in" } } : [],
                        roles = Array.Empty<string>(), conflict = "", phaseMemberships = Array.Empty<GuidePhaseMembership>(), contextOnly = false };
                }).ToArray();
            return Task.FromResult(JsonSerializer.Serialize(new { summary = "Prepare healing.", bosses = new[] { new
            { name = boss, displayName = boss, summary = "Prepare healing.", phaseDefinitions = Array.Empty<GuidePhaseDefinition>(), mechanics } } }));
        }
    }
}
