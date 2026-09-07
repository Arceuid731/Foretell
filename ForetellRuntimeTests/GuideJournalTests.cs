using BossMod.Foretell;
using System.IO.Compression;
using System.Text.Json;

internal static class GuideJournalTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static GuideDocument Source() => new(GuideDocument.CurrentSchema, new(1, 2, "Journal fixture"), "Journal fixture", 1,
        DateTime.UtcNow, GuideNames.Hash("journal"), []) { Page = new("Fixture", "https://example.invalid/guide", "Sentinel: Pulse damages everyone.", "") };

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "foretell-journal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new GuideAnalysisJournal(root);
            var session = journal.Begin(Source(), GuideModelCatalog.DefaultID, true, 65536, 12, true);
            session.Step(new("Draft", "Sentinel", 1));
            var exchange = new GuideModelExchange("system with schema", "whole source", "", "", "", null, null, "", 0, "Started") { RequestJson = "request" };
            session.Exchange(exchange);
            session.Exchange(exchange with { State = "Tokenized", PromptTokens = 7737 });
            session.Runtime("bounded engine details");
            session.Exchange(exchange with { State = "Failed", PromptTokens = 7737, OutputTokens = 8192, Response = "partial JSON", FinishReason = "length", Error = "Incomplete response", Seconds = 2 });
            session.Step(new("Validation", "Sentinel", 1, "Missing evidence field"));
            session.Finish("Failed", new InvalidDataException("Exact validation reason"));
            var report = journal.Reports.Single();
            Check(report.Calls is [{ System: "system with schema", Source: "whole source", Response: "partial JSON", PromptTokens: 7737, OutputTokens: 8192, FinishReason: "length" }]
                && report.Calls[0].RequestJson == "request" && report.Error.Contains("Exact validation reason") && report.RuntimeLog.Contains("bounded engine details"),
                "Failed analysis discarded full request, partial response, token counts or error details");
            Check(report.Events.Any(entry => entry.Detail == "Missing evidence field") && report.SourceDocument?.Page?.Text == Source().Page!.Text,
                "Validation retries or source context were missing from diagnostics");
            var restarted = new GuideAnalysisJournal(root);
            restarted.Load();
            Check(restarted.Reports.Single().Calls.Single().Response == "partial JSON", "Out-of-instance analysis log did not survive reload");
            var damaged = JsonSerializer.SerializeToNode(report, GuideAnalysisJournal.Json)!;
            damaged["Calls"]![0] = null;
            File.WriteAllText(report.FilePath, damaged.ToJsonString());
            var guarded = new GuideAnalysisJournal(root);
            guarded.Load();
            Check(guarded.Reports.Length == 0, "Malformed retained diagnostics reached the UI");
            File.WriteAllText(report.FilePath, JsonSerializer.Serialize(report, GuideAnalysisJournal.Json));
            foreach (var attempt in Enumerable.Range(0, 2))
            {
                var exported = journal.ExportAsync(report.ID).GetAwaiter().GetResult();
                using var zip = ZipFile.OpenRead(exported);
                Check(zip.Entries.Count == 1 && zip.GetEntry("analysis.json") != null, "Standalone diagnostic export was incomplete");
            }
            var privateSession = journal.Begin(Source(), GuideModelCatalog.DefaultID, false, 32768, 6, false);
            privateSession.Exchange(exchange);
            privateSession.Exchange(exchange with { Response = "sensitive response", Reasoning = "sensitive reasoning", State = "Completed" });
            privateSession.Finish("Ready");
            var privateReport = journal.Reports.First();
            var privateText = JsonSerializer.Serialize(privateReport);
            Check(privateReport.SourceDocument == null && !privateText.Contains("whole source") && !privateText.Contains("sensitive") && !privateText.Contains("system with schema"),
                "Disabling conversation recording retained prompt/response content");
            for (var index = 0; index < GuideAnalysisJournal.ReportLimit + 2; ++index)
                journal.Begin(Source(), GuideModelCatalog.DefaultID, false, 32768, 6, false).Finish("Ready");
            Check(journal.Reports.Length == GuideAnalysisJournal.ReportLimit && Directory.GetFiles(root, "*.json").Length == GuideAnalysisJournal.ReportLimit,
                "Analysis journal retention was unbounded");
            var blocked = Path.Combine(root, "blocked");
            File.WriteAllText(blocked, "not a directory");
            var fallback = new GuideAnalysisJournal(blocked);
            fallback.Begin(Source(), GuideModelCatalog.DefaultID, false, 32768, 6, false).Finish("Failed", new IOException("model failure"));
            Check(fallback.Reports.Single().RecordingError.Length > 0 && fallback.Reports.Single().Error.Contains("model failure"),
                "A diagnostic disk failure swallowed the original analysis error");
            var overflow = fallback.Begin(Source(), GuideModelCatalog.DefaultID, false, 32768, 6, true);
            for (var index = 0; index < 256; ++index)
            {
                overflow.Exchange(exchange);
                overflow.Exchange(exchange with { State = "Completed", Response = "retained " + index });
            }
            overflow.Exchange(exchange);
            overflow.Exchange(exchange with { State = "Tokenized" });
            overflow.Exchange(exchange with { State = "Completed", Response = "overflow" });
            Check(overflow.Report.Calls.Length == 256 && overflow.Report.Calls[^1].Response == "retained 255" && overflow.Report.ContentOmitted,
                "Overflow exchanges corrupted the last retained request");
            File.Delete(blocked);
            overflow.Finish("Ready");
            var recovered = new GuideAnalysisJournal(blocked);
            recovered.Load();
            Check(recovered.Reports.Single().RecordingError.Length == 0, "A recovered recording failure persisted on disk");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("AI journal: complete and failed exchanges, independent persistence/export, opt-out, retention and disk-error isolation passed.");
    }

    public static void Reproduce(string cacheFile, string runtime, string output)
    {
        runtime = Path.GetFullPath(runtime);
        output = Path.GetFullPath(output);
        using var cache = JsonDocument.Parse(File.ReadAllText(cacheFile));
        using var payload = JsonDocument.Parse(cache.RootElement.GetProperty("Payload").GetString()!);
        var duty = payload.RootElement.GetProperty("Duty").Deserialize<GuideDuty>()!;
        var source = new ForetellGuideCache(Path.GetDirectoryName(Path.GetFullPath(cacheFile))!).Read(duty)
            ?? throw new InvalidDataException("Validated cached source unavailable.");
        var journal = new GuideAnalysisJournal(output);
        var session = journal.Begin(source, GuideModelCatalog.DefaultID, true, 65536, 12, true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var model = new ForetellGuideLocalModel(runtime, true, 65536, 12);
        model.ExchangeTrace = session.Exchange;
        model.RuntimeTrace = session.Runtime;
        try
        {
            model.Start(progress => Console.WriteLine(progress.Stage), cancellation.Token).GetAwaiter().GetResult();
            var prepared = GuidePageAnalysis.Compile(source, GuideLanguage.English, GuideModelCatalog.Get(GuideModelCatalog.DefaultID), 65536,
                model, (done, total) => Console.WriteLine($"Boss progress: {done}/{total}"), cancellation.Token, null,
                step => { session.Step(step); Console.WriteLine($"{step.Stage}: {step.Boss} #{step.Attempt} {step.Detail}"); }).GetAwaiter().GetResult();
            Check(GuidePageAnalysis.ValidPrepared(prepared, source, GuideLanguage.English, GuideModelCatalog.Get(GuideModelCatalog.DefaultID)),
                "Real model output failed prepared-cache validation.");
            session.Finish("Ready");
            File.WriteAllText(Path.Combine(output, "prepared.json"), JsonSerializer.Serialize(prepared, GuideAnalysisJournal.Json));
            Console.WriteLine($"Prepared: {prepared.Bosses.Length} bosses, {prepared.MechanicCount} mechanics");
            foreach (var boss in prepared.Bosses)
            {
                Console.WriteLine($"Boss {boss.Name}: {boss.PhaseDefinitions.Length} phases; {boss.Phases.Sum(phase => phase.Mechanics.Count(GuideCombatRelevance.IsActionable))} actionable mechanics");
                foreach (var phase in boss.PhaseDefinitions)
                    Console.WriteLine($"Phase {phase.Name}: {boss.Phases.Sum(section => section.Mechanics.Count(mechanic => GuidePhases.Includes(boss, mechanic, phase)))} mechanics");
            }
        }
        catch (Exception error) { session.Finish("Failed", error); throw; }
        finally { Console.WriteLine("Diagnostic: " + session.Report.FilePath); }
    }

    public static void ReproducePhases(string input, string runtime, string output)
    {
        var document = JsonSerializer.Deserialize<GuideDocument>(File.ReadAllText(input), GuideAnalysisJournal.Json)!;
        output = Path.GetFullPath(output);
        var journal = new GuideAnalysisJournal(output);
        var session = journal.Begin(document, GuideModelCatalog.DefaultID, true, 65536, 12, true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var model = new ForetellGuideLocalModel(Path.GetFullPath(runtime), true, 65536, 12);
        model.ExchangeTrace = session.Exchange;
        model.RuntimeTrace = session.Runtime;
        try
        {
            model.Start(progress => Console.WriteLine(progress.Stage), cancellation.Token).GetAwaiter().GetResult();
            var prepared = GuidePageAnalysis.RepairPhasePlan(document, document.Page!.Text, model, cancellation.Token,
                step => { session.Step(step); Console.WriteLine($"{step.Stage}: {step.Detail}"); }).GetAwaiter().GetResult();
            Check(GuidePageAnalysis.ValidPrepared(prepared, document, GuideLanguage.English, GuideModelCatalog.Get(GuideModelCatalog.DefaultID)), "Repaired phases failed cache validation.");
            session.Finish("Ready");
            File.WriteAllText(Path.Combine(output, "prepared.json"), JsonSerializer.Serialize(prepared, GuideAnalysisJournal.Json));
            foreach (var boss in prepared.Bosses)
                Console.WriteLine($"Boss {boss.Name}: {boss.PhaseDefinitions.Length} phases: {string.Join(", ", boss.PhaseDefinitions.Select(phase => phase.Name))}");
        }
        catch (Exception error) { session.Finish("Failed", error); throw; }
    }

    public static void Replay(string log, string output)
    {
        var report = JsonSerializer.Deserialize<GuideAnalysisReport>(File.ReadAllText(log), GuideAnalysisJournal.Json)
            ?? throw new InvalidDataException("Missing analysis report.");
        var source = report.SourceDocument ?? throw new InvalidDataException("Source conversation was not recorded.");
        using var model = new RecordedModel(report.Calls);
        var document = GuidePageAnalysis.Compile(source, GuideLanguage.English, GuideModelCatalog.Get(report.ModelID), report.ContextTokens,
            model, (_, _) => { }, CancellationToken.None, null, step => Console.WriteLine($"{step.Stage}: {step.Detail}")).GetAwaiter().GetResult();
        Check(GuidePageAnalysis.ValidPrepared(document, source, GuideLanguage.English, GuideModelCatalog.Get(report.ModelID)), "Replayed output failed cache validation");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "prepared-replayed.json"), JsonSerializer.Serialize(document, GuideAnalysisJournal.Json));
        Console.WriteLine($"Recorded-response replay: {document.Bosses.Length} bosses, {document.MechanicCount} mechanics; {model.Requests} recorded exchanges, no inference.");
    }

    private sealed class RecordedModel(GuideAnalysisCall[] calls) : IGuideSummaryModel
    {
        public int Requests;
        public GuideModelRuntime Runtime => new();
        public Task Start(Action<GuideModelProgress> progress, CancellationToken cancellation) => Task.CompletedTask;
        public Task<string> Summarize(string source, GuideLanguage language, CancellationToken cancellation) => throw new NotSupportedException();
        public Task<string> Analyze(string system, string source, object schema, int outputTokens, CancellationToken cancellation)
        {
            var prompt = system + "\nRequired JSON schema:\n" + JsonSerializer.Serialize(schema);
            var call = calls.FirstOrDefault(call => call.State == "Completed" && call.Source == source && call.System == prompt)
                ?? throw new InvalidDataException("Replay requires an unrecorded model request.");
            ++Requests;
            return Task.FromResult(call.Response);
        }
        public void Dispose() { }
    }
}
