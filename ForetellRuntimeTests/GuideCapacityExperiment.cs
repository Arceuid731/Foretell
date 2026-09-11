using BossMod.Foretell;
using System.Diagnostics;
using System.Text.Json;

// Explicit real-inference trials. Prepared guides never enter the player's cache or change their settings.
internal static class GuideCapacityExperiment
{
    private static GuideDocument ReadSource(string input)
    {
        using var parsed = JsonDocument.Parse(File.ReadAllText(input));
        if (parsed.RootElement.TryGetProperty("SourceDocument", out _))
        {
            var report = parsed.Deserialize<GuideAnalysisReport>(GuideAnalysisJournal.Json)!;
            if (report.ContentOmitted || report.SourceDocument?.SourceHash != report.SourceHash)
                throw new InvalidDataException("Incomplete source archive.");
            return report.SourceDocument!;
        }
        return parsed.Deserialize<GuideDocument>(GuideAnalysisJournal.Json) ?? throw new InvalidDataException("Missing source.");
    }

    internal static async Task Fetch(string inputs, string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Use a new source directory.");
        Directory.CreateDirectory(output);
        using var providers = new ForetellGuideProviders(Path.Combine(output, "providers"));
        foreach (var file in Directory.EnumerateFiles(inputs, "analysis-*.json").Order())
        {
            var original = ReadSource(file);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var bundle = await providers.Fetch(original.Duty, deadline.Token);
            File.WriteAllText(Path.Combine(output, original.Duty.Key + "-bundle.json"), JsonSerializer.Serialize(bundle, GuideAnalysisJournal.Json));
            var source = GuideSourceAssembly.Combine(original.Duty, bundle, DateTime.UtcNow);
            File.WriteAllText(Path.Combine(output, original.Duty.Key + "-source.json"), JsonSerializer.Serialize(source, GuideAnalysisJournal.Json));
            Console.WriteLine(JsonSerializer.Serialize(new { original.Duty, bundle.States, Sources = bundle.Sources.Select(page => new { page.Provider, Characters = page.Text.Length }) }));
        }
    }

    internal static void Augment(string inputs, string current, string output)
    {
        if (Directory.Exists(output)) throw new IOException("Use a new augmentation directory.");
        Directory.CreateDirectory(output);
        foreach (var file in Directory.EnumerateFiles(inputs, "analysis-*.json").Order())
        {
            var original = ReadSource(file);
            var fresh = ReadSource(Path.Combine(current, original.Duty.Key + "-source.json"));
            if (fresh.Duty != original.Duty) throw new InvalidDataException("Mismatched source duty.");
            var additions = fresh.Sources.Where(page => original.Sources.All(previous => previous.Provider != page.Provider)).ToArray();
            var pages = original.Sources.Concat(additions).ToArray();
            var bundle = new GuideSourceBundle(pages, fresh.Providers);
            var augmented = GuideSourceAssembly.Combine(original.Duty, bundle, DateTime.UtcNow);
            File.WriteAllText(Path.Combine(output, original.Duty.Key + "-source.json"), JsonSerializer.Serialize(augmented, GuideAnalysisJournal.Json));
            Console.WriteLine(JsonSerializer.Serialize(new { original.Duty, OriginalSourceHash = original.SourceHash, AugmentedSourceHash = augmented.SourceHash,
                AddedProviders = additions.Select(page => page.Provider), PreservedOriginalFingerprints = original.Sources.Select(page => page.Fingerprint) }));
        }
    }

    internal static async Task Run(string input, string runtime, string output, string modelID, string mode, int contextTokens)
    {
        if (mode is not ("baseline" or "vocabulary" or "facts" or "facts-v2" or "facts-v3")) throw new ArgumentException("Unknown experiment mode.");
        var profile = GuideModelCatalog.Profiles.Single(profile => profile.ID == modelID);
        var source = ReadSource(input);
        if (source.Page is not { Text.Length: > 0 }) throw new InvalidDataException("Missing complete source text.");
        runtime = Path.GetFullPath(runtime);
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new IOException("Use a new trial directory.");
        foreach (var asset in new[] { profile.Asset, ForetellGuideLocalModel.Vulkan })
            if (!await ForetellGuideLocalModel.Verify(Path.Combine(runtime, asset.Name), asset, CancellationToken.None))
                throw new InvalidDataException("Missing verified asset: " + asset.Name);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "source.json"), JsonSerializer.Serialize(source, GuideAnalysisJournal.Json));
        var journal = new GuideAnalysisJournal(output);
        var session = journal.Begin(source, modelID, true, contextTokens, 12, true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        using var model = new GuideReasoningExperiment.ExperimentModel(runtime, profile, false, session.Runtime, contextTokens);
        model.ExchangeTrace = session.Exchange;
        var watch = Stopwatch.StartNew();
        var publications = new List<object>();
        var samples = new List<object>();
        using var sampling = new CancellationTokenSource();
        async Task Sample()
        {
            try
            {
                while (!sampling.IsCancellationRequested)
                {
                    var start = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.total,memory.used,memory.free,utilization.gpu --format=csv,noheader,nounits")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    using var gpu = Process.Start(start)!;
                    var reading = await gpu.StandardOutput.ReadToEndAsync(sampling.Token);
                    await gpu.WaitForExitAsync(sampling.Token);
                    long? privateBytes = null;
                    if (model.ProcessID is { } id)
                    {
                        try { using var process = Process.GetProcessById(id); privateBytes = process.PrivateMemorySize64; }
                        catch (ArgumentException) { }
                    }
                    var game = Process.GetProcessesByName("ffxiv_dx11");
                    var gameIDs = game.Select(process => process.Id).ToArray();
                    foreach (var process in game) process.Dispose();
                    samples.Add(new { Seconds = watch.Elapsed.TotalSeconds, At = DateTime.UtcNow, Gpu = reading.Trim(), GameIDs = gameIDs, PrivateBytes = privateBytes });
                    await Task.Delay(1000, sampling.Token);
                }
            }
            catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
        }
        var sampler = Sample();
        GuideDocument? prepared = null;
        var failure = "";
        double startup = 0;
        try
        {
            await model.Start(progress => Console.WriteLine(progress.Stage), deadline.Token);
            startup = watch.Elapsed.TotalSeconds;
            prepared = await GuidePageAnalysis.Compile(source, GuideLanguage.English, profile, contextTokens, model,
                (done, total) => Console.WriteLine($"bosses {done}/{total}"), deadline.Token,
                document => publications.Add(new { Seconds = watch.Elapsed.TotalSeconds, Bosses = document.Bosses.Where(boss => boss.Phases.Length > 0).Select(boss => boss.Name).ToArray(), document.MechanicCount }),
                step => { session.Step(step); Console.WriteLine($"{step.Stage} {step.Boss} #{step.Attempt}: {step.Detail}"); },
                mode.StartsWith("facts", StringComparison.Ordinal) ? GuideAnalysisStrategy.EvidenceFirst : GuideAnalysisStrategy.Combined);
            if (!GuidePageAnalysis.ValidPrepared(prepared, source, GuideLanguage.English, profile)) throw new InvalidDataException("Prepared-cache validation failed.");
            File.WriteAllText(Path.Combine(output, "prepared.json"), JsonSerializer.Serialize(prepared, GuideAnalysisJournal.Json));
            session.Finish("Ready");
        }
        catch (Exception error) { failure = error.ToString(); prepared = null; session.Finish("Failed", error); }
        finally
        {
            watch.Stop(); sampling.Cancel();
            try { await sampler; } catch (Exception error) { File.WriteAllText(Path.Combine(output, "sampling-error.txt"), error.ToString()); }
            model.Dispose();
            File.WriteAllText(Path.Combine(output, "gpu-samples.json"), JsonSerializer.Serialize(samples, GuideAnalysisJournal.Json));
        }
        var result = new
        {
            source.Duty, source.SourceHash, ModelID = modelID, ModelSha256 = profile.Asset.Hash, RuntimeSha256 = ForetellGuideLocalModel.Vulkan.Hash,
            Mode = mode, ContextTokens = contextTokens, MemoryGiB = 12, Backend = "Vulkan", Complete = prepared != null,
            Seconds = watch.Elapsed.TotalSeconds, StartupSeconds = startup, Publications = publications, Requests = session.Report.Calls.Length,
            RequestSeconds = session.Report.Calls.Sum(call => call.Seconds), PromptTokens = session.Report.Calls.Sum(call => call.PromptTokens ?? 0),
            OutputTokens = session.Report.Calls.Sum(call => call.OutputTokens ?? 0), Mechanics = prepared?.MechanicCount ?? 0,
            Error = failure, Journal = Path.GetFileName(session.Report.FilePath),
            Measurement = "Fresh process; temperature 0, seed 42, reasoning off; no CPU fallback. NVIDIA samples measure whole-device dedicated memory including game and desktop, not model-exclusive VRAM. Game process presence does not prove active rendering or combat. Successful schema/citation validation is not semantic correctness."
        };
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(result, GuideAnalysisJournal.Json));
        Console.WriteLine(JsonSerializer.Serialize(new { modelID, mode, result.Complete, result.Seconds, result.Mechanics, Error = failure.Split('\n')[0] }));
    }
}
