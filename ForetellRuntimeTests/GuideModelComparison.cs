using BossMod.Foretell;
using System.Diagnostics;
using System.Text.Json;

// Explicit real-inference probe, never called by the ordinary test suite.
internal static class GuideModelComparison
{
    public static void InspectBindings(string results, string gameDirectory)
    {
        using var game = new Lumina.GameData(gameDirectory);
        var catalog = GuideIdCatalog.FromGameData(game, CancellationToken.None);
        foreach (var trial in Directory.EnumerateDirectories(Path.GetFullPath(results)).Order(StringComparer.Ordinal))
        {
            var file = Path.Combine(trial, "prepared.json");
            if (!File.Exists(file)) continue;
            var document = JsonSerializer.Deserialize<GuideDocument>(File.ReadAllText(file), GuideAnalysisJournal.Json)
                ?? throw new InvalidDataException("Missing prepared result.");
            var plan = new GuideIdPlan(document, catalog);
            var report = new
            {
                document.Duty, document.SourceHash, document.ModelRevision, CatalogHash = catalog.Fingerprint,
                Bosses = plan.Entries.Where(entry => entry.Kind == "boss").ToArray(),
                Mechanics = document.Bosses.SelectMany(boss => boss.Phases.SelectMany(phase => phase.Mechanics.Select(mechanic => new
                {
                    Boss = boss.Name, mechanic.Name,
                    Candidates = plan.Entries.Where(entry => entry.Boss == boss.Name && entry.Mechanic == mechanic.Name).ToArray()
                }))).ToArray(),
                Limitation = "Offline game ID candidate lookup only. No observation, target, phase, actor ownership or presentation lifecycle is replayed; candidate counts are not live coverage or semantic correctness."
            };
            File.WriteAllText(Path.Combine(trial, "id-bindings.json"), JsonSerializer.Serialize(report, GuideAnalysisJournal.Json));
            Console.WriteLine($"{Path.GetFileName(trial)}: unresolved boss names: {string.Join(", ", report.Bosses.Where(entry => entry.IDs.Length == 0).Select(entry => entry.Name))}");
        }
    }

    public static async Task Run(string input, string runtime, string output, string modelID)
    {
        var recorded = JsonSerializer.Deserialize<GuideAnalysisReport>(File.ReadAllText(input), GuideAnalysisJournal.Json)
            ?? throw new InvalidDataException("Missing recorded analysis.");
        var source = recorded.SourceDocument ?? throw new InvalidDataException("Missing complete recorded source.");
        var profile = GuideModelCatalog.Profiles.Single(profile => profile.ID == modelID);
        if (recorded.ContentOmitted || source.Page == null || source.SourceHash != recorded.SourceHash)
            throw new InvalidDataException("Incomplete or inconsistent recorded source.");
        if (recorded.ContextTokens != 65536 || recorded.MemoryGiB != 12 || !recorded.Gpu)
            throw new InvalidDataException("This comparison requires the same recorded 64K / 12 GiB / Vulkan settings.");
        runtime = Path.GetFullPath(runtime);
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Use a new output directory to preserve previous trials.");
        // An evaluation must not silently download a different runtime or weights.
        foreach (var asset in new[] { profile.Asset, ForetellGuideLocalModel.Vulkan })
            if (!await ForetellGuideLocalModel.Verify(Path.Combine(runtime, asset.Name), asset, CancellationToken.None))
                throw new InvalidDataException("Missing or unverified installed asset: " + asset.Name);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "source.json"), JsonSerializer.Serialize(source, GuideAnalysisJournal.Json));
        var journal = new GuideAnalysisJournal(output);
        var session = journal.Begin(source, modelID, true, 65536, 12, true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var model = new ForetellGuideLocalModel(runtime, true, 65536, 12, modelID);
        model.ExchangeTrace = session.Exchange;
        model.RuntimeTrace = session.Runtime;
        var clock = Stopwatch.StartNew();
        double startupSeconds = 0;
        string backend = "";
        long peakPrivateBytes = 0, peakWorkingSetBytes = 0;
        int memorySamples = 0;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            try
            {
                while (!sampling.IsCancellationRequested)
                {
                    if (model.ProcessID is { } id)
                    {
                        try
                        {
                            using var process = Process.GetProcessById(id);
                            process.Refresh();
                            peakPrivateBytes = Math.Max(peakPrivateBytes, process.PrivateMemorySize64);
                            peakWorkingSetBytes = Math.Max(peakWorkingSetBytes, process.PeakWorkingSet64);
                            ++memorySamples;
                        }
                        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    }
                    await Task.Delay(500, sampling.Token);
                }
            }
            catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
        });
        GuideDocument? prepared = null;
        string failure = "";
        try
        {
            await model.Start(progress =>
            {
                if (progress.Stage.Contains("falling back to CPU", StringComparison.Ordinal))
                    throw new InvalidOperationException("CPU fallback is outside this Vulkan comparison.");
                Console.WriteLine($"{modelID}: {progress.Stage}");
            }, cancellation.Token);
            backend = model.Runtime.Backend;
            if (backend != "Vulkan") throw new InvalidOperationException("Unexpected comparison backend: " + backend);
            startupSeconds = clock.Elapsed.TotalSeconds;
            prepared = await GuidePageAnalysis.Compile(source, GuideLanguage.English, profile, 65536,
                model, (done, total) => Console.WriteLine($"{modelID}: bosses {done}/{total}"), cancellation.Token, null,
                step => { session.Step(step); Console.WriteLine($"{modelID}: {step.Stage} {step.Boss} #{step.Attempt} {step.Detail}"); });
            if (!GuidePageAnalysis.ValidPrepared(prepared, source, GuideLanguage.English, profile))
                throw new InvalidDataException("Prepared-cache validation failed.");
            File.WriteAllText(Path.Combine(output, "prepared.json"), JsonSerializer.Serialize(prepared, GuideAnalysisJournal.Json));
            session.Finish("Ready");
        }
        catch (Exception error)
        {
            prepared = null;
            failure = error.ToString();
            session.Finish("Failed", error);
        }
        finally
        {
            clock.Stop();
            sampling.Cancel();
            await sampler;
            model.Dispose();
        }
        var result = new
        {
            source.Duty, source.SourceHash, ModelID = modelID, ModelSha256 = profile.Asset.Hash,
            RuntimeSha256 = ForetellGuideLocalModel.Vulkan.Hash, ContextTokens = 65536, MemoryGiB = 12, Backend = backend,
            Complete = prepared != null, Seconds = clock.Elapsed.TotalSeconds, StartupSeconds = startupSeconds,
            PeakSampledPrivateBytes = peakPrivateBytes, PeakWorkingSetBytes = peakWorkingSetBytes, MemorySamples = memorySamples,
            Requests = session.Report.Calls.Length, RequestSeconds = session.Report.Calls.Sum(call => call.Seconds),
            PromptTokens = session.Report.Calls.Sum(call => call.PromptTokens ?? 0), OutputTokens = session.Report.Calls.Sum(call => call.OutputTokens ?? 0),
            Bosses = prepared?.Bosses.Select(boss => boss.Name).ToArray() ?? [], Mechanics = prepared?.MechanicCount ?? 0,
            Error = failure, Journal = Path.GetFileName(session.Report.FilePath),
            Measurement = "Fresh model process and no prepared/response cache. Asset preflight excluded; startup includes runtime verification. Private memory sampled at 500 ms; working set peak is the process OS counter. Neither is VRAM. Successful validation is not a semantic quality score."
        };
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(result, GuideAnalysisJournal.Json));
        Console.WriteLine(JsonSerializer.Serialize(new { modelID, source.Title, result.Complete, result.Seconds, result.Mechanics, Error = failure.Split('\n')[0] }));
    }
}
