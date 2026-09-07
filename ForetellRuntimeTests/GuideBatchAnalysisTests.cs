using BossMod.Foretell;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal static class GuideBatchAnalysisTests
{
    private static readonly GuideModelProfile Profile = GuideModelCatalog.Get(GuideModelCatalog.DefaultID);
    private static readonly string[] Names = ["Sentinel", "Keeper", "Warden"];
    private static readonly string[] Abilities = ["Hammer", "Pulse", "Nova"];
    private static readonly string[] Cues = ["Tank: mitigate", "Heal the group", "Spread out"];
    private static readonly string[] Passages = ["Hammer: Heavy damage to the tank.", "Pulse: Everyone takes damage.", "Nova: Spread out."];
    private static readonly string[] Paragraphs = [Names[0], Passages[0], Names[1], Passages[1], Names[2], Passages[2], "FULL_PAGE_END: Remain inside the arena."];
    private static GuideDocument Source(string newline = "\n", int extraParagraphs = 0)
    {
        var text = string.Join(newline, Paragraphs.Concat(Enumerable.Repeat("Additional source context.", extraParagraphs)));
        return new(GuideDocument.CurrentSchema, new(913, 914, "Batch fixture"), "Batch fixture", 1,
            DateTime.UtcNow, GuideNames.Hash(text), []) { Page = new("Fixture", "https://example.invalid/batch", text, "") };
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static Task<GuideDocument> Compile(GuideDocument source, IGuideSummaryModel model, CancellationToken cancellation = default,
        Action<GuideDocument>? ready = null) => GuidePageAnalysis.Compile(source, GuideLanguage.English, Profile, 32768, model, (_, _) => { }, cancellation, ready);
    private static void Valid(GuideDocument prepared, GuideDocument source)
    {
        Check(prepared.Bosses.Select(boss => boss.Name).SequenceEqual(Names) && prepared.MechanicCount == 3
            && GuidePageAnalysis.ValidPrepared(prepared, source, GuideLanguage.English, Profile), "Batch result lost the roster, mechanics or strict cache validation.");
        for (var index = 0; index < Names.Length; ++index)
        {
            var mechanic = prepared.Bosses[index].Phases.Single().Mechanics.Single();
            Check(mechanic.Name == Abilities[index] && mechanic.Advice is { TriggerKind: "cast" }
                && mechanic.Advice.TriggerName == Abilities[index] && mechanic.Advice.Cue == Cues[index]
                && mechanic.Advice.Evidence.Single().TrimEnd('\r') == Passages[index], "Batch validation changed grounded advice or crossed boss evidence.");
        }
    }

    public static async Task Run()
    {
        foreach (var newline in new[] { "\n", "\r\n" })
        {
            var source = Source(newline);
            using var model = new BatchModel(source);
            var partials = new List<GuideDocument>();
            var prepared = await Compile(source, model, ready: partials.Add);
            Valid(prepared, source);
            Check(model.Requests == 2 && model.Outlines == 1 && model.Batches == 1 && model.Individuals.Count == 0,
                "Three valid bosses required more than one global outline and one global draft.");
            Check(partials.Count == 3 && partials.Select(document => document.MechanicCount).SequenceEqual([1, 2, 3])
                && partials.All(document => document.Bosses.Select(boss => boss.Name).SequenceEqual(Names)
                    && !GuidePageAnalysis.ValidPrepared(document, source, GuideLanguage.English, Profile)),
                "Batch results skipped independent boss publication or accepted partial coverage as a complete cache.");
            for (var index = 0; index < Names.Length; ++index)
                Check(ReferenceEquals(partials[index].Bosses[index], prepared.Bosses[index]), "Later validation replaced an already validated batch boss.");
            VerifyCompactSchemas(model);
        }
        await RepairRequestedBoss();
        await FullBatchFallback();
        await CompactSchemaSize();
        await CancellationAndResume();
        Console.WriteLine("Batch guide analysis: two-request full-source draft, isolated boss repair, bounded fallback, cancellation/resume and compact citations passed (stub models).");
    }

    private static async Task RepairRequestedBoss()
    {
        var source = Source();
        foreach (var defect in new[] { "missing", "duplicate", "empty", "evidence", "cue", "responses", "duplicate-mechanic" })
        {
            using var model = new BatchModel(source, defect);
            var partials = new List<GuideDocument>();
            var prepared = await Compile(source, model, ready: partials.Add);
            Valid(prepared, source);
            Check(model.Requests == 3 && model.Outlines == 1 && model.Batches == 1 && model.Individuals.SequenceEqual([Names[1]]),
                "Repair repeated a valid boss or failed to isolate the requested boss for " + defect);
            Check(model.Reviews == (defect is "missing" or "duplicate" ? 0 : 1), "Invalid batch content bypassed existing review validation for " + defect);
            Check(ReferenceEquals(partials[0].Bosses[0], prepared.Bosses[0]) && partials[0].Bosses[1].Phases.Length == 0
                && partials[1].Bosses[2].Phases.Length == 0, "Repair discarded valid progress or published an unvalidated boss for " + defect);
        }
        using (var model = new BatchModel(source, "missing-review"))
        {
            Valid(await Compile(source, model), source);
            Check(model.Requests == 4 && model.Individuals.SequenceEqual([Names[1], Names[1]]) && model.Reviews == 1,
                "A missing boss's invalid individual draft skipped the existing review path or recomputed valid batch bosses.");
        }
        using (var model = new BatchModel(source, "persistent"))
        {
            var partials = new List<GuideDocument>();
            try { await Compile(source, model, ready: partials.Add); throw new Exception("Persistently ungrounded batch repair was accepted."); }
            catch (InvalidDataException) { }
            Check(model.Batches == 1 && model.Reviews == 2 && model.Individuals.SequenceEqual([Names[1], Names[1]])
                && partials is [{ MechanicCount: 1 }] && partials[0].Bosses[1].Phases.Length == 0,
                "Failed repair exceeded the validation budget or published invalid progress.");
        }
    }

    private static async Task FullBatchFallback()
    {
        var source = Source();
        foreach (var defect in new[] { "output", "context", "malformed", "null", "missing-bosses" })
        {
            using var model = new BatchModel(source, defect);
            var partials = new List<GuideDocument>();
            Valid(await Compile(source, model, ready: partials.Add), source);
            Check(model.Requests == 5 && model.Outlines == 1 && model.Batches == 1 && model.Individuals.SequenceEqual(Names) && model.Reviews == 0,
                "A failed combined response did not fall back once to the existing individual drafts for " + defect);
            Check(partials.Select(document => document.MechanicCount).SequenceEqual([1, 2, 3]), "Whole-batch fallback lost individual progress for " + defect);
        }
    }

    private static async Task CancellationAndResume()
    {
        var source = Source();
        using (var canceled = new CancellationTokenSource())
        using (var model = new BatchModel(source))
        {
            canceled.Cancel();
            await Canceled(() => Compile(source, model, canceled.Token));
            Check(model.Requests == 0, "Pre-canceled compilation issued a model request.");
        }
        foreach (var completed in new[] { false, true })
        {
            var memory = new GuideAnalysisMemory();
            using var cancellation = new CancellationTokenSource();
            using var first = new BatchModel(source) { OnBatch = () => { cancellation.Cancel(); return completed; } };
            using (var resumable = new GuideResumableModel(first, memory))
                await Canceled(() => Compile(source, resumable, cancellation.Token));
            Check(first.Requests == 2 && first.Individuals.Count == 0 && memory.Responses.Count == (completed ? 2 : 1),
                "Batch cancellation fell back to individual requests or cached an unfinished response.");
            using var replacement = new BatchModel(source);
            using (var resumable = new GuideResumableModel(replacement, memory))
                Valid(await Compile(source, resumable), source);
            Check(replacement.Requests == (completed ? 0 : 1) && replacement.Outlines == 0 && replacement.Individuals.Count == 0,
                "Resuming a canceled batch repeated completed requests or omitted the unfinished draft.");
        }
        foreach (var defect in new[] { "evidence", "output", "context", "malformed" })
        {
            var memory = new GuideAnalysisMemory();
            var partials = new List<GuideDocument>();
            using var cancellation = new CancellationTokenSource();
            using var first = new BatchModel(source, defect)
            {
                OnIndividual = name => { if (name == Names[1]) cancellation.Cancel(); }
            };
            using (var resumable = new GuideResumableModel(first, memory))
                await Canceled(() => Compile(source, resumable, cancellation.Token, partials.Add));
            Check(partials is [{ MechanicCount: 1 }] && partials[0].Bosses[1].Phases.Length == 0,
                "Interrupted boss repair lost the completed first boss or published unfinished advice.");
            using var replacement = new BatchModel(source, defect);
            GuideDocument prepared;
            using (var resumable = new GuideResumableModel(replacement, memory)) prepared = await Compile(source, resumable);
            Valid(prepared, source);
            Check(replacement.Outlines == 0 && replacement.Batches == (defect is "output" or "context" ? 1 : 0)
                && replacement.Individuals.SequenceEqual(defect == "evidence" ? [Names[1]] : Names.Skip(1)),
                "Resume repeated completed individual/batch requests or failed to continue the interrupted boss for " + defect);
            Check(JsonSerializer.Serialize(partials[0].Bosses[0]) == JsonSerializer.Serialize(prepared.Bosses[0]),
                "Resume changed previously validated boss content.");
        }
        {
            var memory = new GuideAnalysisMemory();
            var partials = new List<GuideDocument>();
            using var cancellation = new CancellationTokenSource();
            using var first = new BatchModel(source);
            using (var resumable = new GuideResumableModel(first, memory))
                await Canceled(() => Compile(source, resumable, cancellation.Token, partial =>
                {
                    partials.Add(partial);
                    cancellation.Cancel();
                }));
            Check(partials is [{ MechanicCount: 1 }] && memory.Responses.Count == 2,
                "Cancellation between batch bosses published later bosses or discarded the completed response.");
            using var replacement = new BatchModel(source);
            using (var resumable = new GuideResumableModel(replacement, memory)) Valid(await Compile(source, resumable), source);
            Check(replacement.Requests == 0, "Resuming between batch bosses repeated a completed request.");
        }
    }

    private static async Task Canceled(Func<Task<GuideDocument>> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Canceled batch compilation completed.");
    }

    private static void VerifyCompactSchemas(BatchModel model)
    {
        var outlineBoss = model.Schemas[0]["properties"]!["bosses"]!["items"]!["properties"]!;
        var draftBoss = model.Schemas[1]["properties"]!["bosses"]!["items"]!["properties"]!;
        var mechanic = draftBoss["mechanics"]!["items"]!["properties"]!;
        foreach (var citation in new[] { outlineBoss["passages"]!["items"]!, mechanic["evidence"]!["items"]!,
            draftBoss["phaseDefinitions"]!["items"]!["properties"]!["evidence"]!["items"]!,
            mechanic["phaseMemberships"]!["items"]!["properties"]!["evidence"]!["items"]! })
            VerifyCompactCitation(citation);
    }

    internal static void VerifyCompactCitation(JsonNode citation)
    {
        Check(citation["type"]!.GetValue<string>() == "string" && citation["enum"] == null
            && citation["maxLength"]!.GetValue<int>() > 0 && citation.ToJsonString().Length < 128,
            "Citation schema still repeats IDs or dropped its string bound.");
        var pattern = citation["pattern"]!.GetValue<string>();
        Check(new[] { "1", "12", "999" }.All(value => Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
            && new[] { "", "-1", "1.5", "1a", " 1", "1 ", "١" }.All(value => !Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))),
            "Compact citation schema accepts non-digit references or rejects digit strings.");
    }

    private static async Task CompactSchemaSize()
    {
        using var small = new BatchModel(Source());
        using var large = new BatchModel(Source(extraParagraphs: 1200));
        await Compile(small.Document, small);
        await Compile(large.Document, large);
        VerifyCompactSchemas(large);
        for (var index = 0; index < 2; ++index)
            Check(large.Schemas[index].ToJsonString().Length < 5000
                && Math.Abs(large.Schemas[index].ToJsonString().Length - small.Schemas[index].ToJsonString().Length) < 64,
                "Citation schema size still grows with the number of source paragraphs.");
    }

    private sealed class BatchModel(GuideDocument document, string defect = "none") : IGuideSummaryModel
    {
        public GuideDocument Document => document;
        public int Requests;
        public int Outlines;
        public int Batches;
        public int Reviews;
        public readonly List<string> Individuals = [];
        public readonly List<JsonNode> Schemas = [];
        public Func<bool>? OnBatch;
        public Action<string>? OnIndividual;
        public GuideModelRuntime Runtime => new();
        public Task Start(Action<GuideModelProgress> progress, CancellationToken cancellation) => throw new InvalidOperationException("No inference allowed.");
        public Task<string> Summarize(string source, GuideLanguage language, CancellationToken cancellation) => throw new InvalidOperationException("Unexpected legacy summary.");
        public Task<string> Analyze(string system, string source, object schema, int outputTokens, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            ++Requests;
            Schemas.Add(JsonSerializer.SerializeToNode(schema)!);
            var paragraphs = document.Page!.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Check(source.Contains("<source>\n" + string.Join('\n', paragraphs.Select((paragraph, index) => $"[{index + 1}] {paragraph}")) + "\n</source>", StringComparison.Ordinal),
                "Global or individual analysis lost full-source text, ordering, line endings or context omitted by the outline.");
            if (system.StartsWith("Read the ENTIRE", StringComparison.Ordinal))
            {
                ++Outlines;
                return Task.FromResult(JsonSerializer.Serialize(new { bosses = Names.Select((name, index) => new
                {
                    name, passages = new[] { (2 * index + 1).ToString(CultureInfo.InvariantCulture), (2 * index + 2).ToString(CultureInfo.InvariantCulture) }
                }) }));
            }
            if (source.Contains("Requested bosses:", StringComparison.Ordinal))
            {
                ++Batches;
                Check(source.Contains("Requested bosses: " + string.Join(", ", Names) + "\n", StringComparison.Ordinal), "Global draft did not request every boss in roster order.");
                Check(!system.StartsWith("Audit", StringComparison.Ordinal), "Valid batch analysis unnecessarily requested a global audit.");
                if (OnBatch?.Invoke() == false) return Task.FromCanceled<string>(cancellation);
                if (defect == "output") throw new GuideOutputException();
                if (defect == "context") throw new GuideContextException(40000, 32768);
                if (defect == "malformed") return Task.FromResult("{\"bosses\":[");
                if (defect == "null") return Task.FromResult("null");
                if (defect == "missing-bosses") return Task.FromResult("{\"summary\":\"Prepare for the bosses.\"}");
                var bosses = new JsonArray(Enumerable.Range(0, 3).Select(index => (JsonNode)Boss(index)).ToArray());
                if (defect is "missing" or "missing-review") bosses.RemoveAt(1);
                else if (defect == "duplicate") bosses.Add(bosses[1]!.DeepClone());
                else Corrupt(bosses[1]!.AsObject(), defect);
                return Task.FromResult(Response(bosses));
            }
            var bossIndex = Array.FindIndex(Names, name => source.StartsWith("Boss: " + name + "\n", StringComparison.Ordinal));
            Check(bossIndex >= 0, "Unexpected model request outside the batch or requested-boss draft/review path.");
            var review = system.StartsWith("Audit", StringComparison.Ordinal);
            if (review)
            {
                ++Reviews;
                Check(source.Contains("<Draft>", StringComparison.Ordinal) && source.Contains("validation issue", StringComparison.Ordinal), "Individual repair omitted its failed draft or validation error.");
            }
            Individuals.Add(Names[bossIndex]);
            OnIndividual?.Invoke(Names[bossIndex]);
            cancellation.ThrowIfCancellationRequested();
            var boss = Boss(bossIndex);
            if (defect == "persistent" || defect == "missing-review" && !review) Corrupt(boss, "evidence");
            return Task.FromResult(Response(new JsonArray(boss)));
        }

        private static JsonObject Boss(int index) => JsonSerializer.SerializeToNode(new
        {
            name = Names[index], displayName = Names[index], summary = Cues[index], phaseDefinitions = Array.Empty<GuidePhaseDefinition>(),
            mechanics = new[] { new
            {
                name = Abilities[index], displayName = Abilities[index], cue = Cues[index], description = Cues[index],
                triggerKind = "cast", triggerName = Abilities[index], evidence = new[] { (2 * index + 2).ToString(CultureInfo.InvariantCulture) },
                responses = Array.Empty<GuideResponse>(), roles = Array.Empty<string>(), conflict = "", phaseMemberships = Array.Empty<GuidePhaseMembership>(), contextOnly = false
            } }
        })!.AsObject();

        private static void Corrupt(JsonObject boss, string defect)
        {
            if (defect == "empty") boss["mechanics"] = new JsonArray();
            if (defect is "evidence" or "persistent") boss["mechanics"]![0]!["evidence"]![0] = "999999";
            if (defect == "cue") boss["mechanics"]![0]!["cue"] = "Move 999 yalms away";
            if (defect == "responses") boss["mechanics"]![0]!["responses"] = new JsonArray(new JsonObject { ["when"] = "After 999 seconds", ["instruction"] = "Spread out" });
            if (defect == "duplicate-mechanic") boss["mechanics"]!.AsArray().Add(boss["mechanics"]![0]!.DeepClone());
        }

        private static string Response(JsonArray bosses) => new JsonObject { ["summary"] = "Prepare for the bosses.", ["bosses"] = bosses }.ToJsonString();
        public void Dispose() { }
    }
}
