using BossMod.Foretell;
using System.Net;
using System.Text;
using System.Text.Json;

internal static class GuideCacheTests
{
    private static readonly GuideDuty Duty = new(596, 810, "Hells' Kier");
    private const string Passage = "Sentinel\nHammer: Heavy damage to the tank.";
    private const string Html = "<p>Sentinel</p><p>Hammer: Heavy damage to the tank.</p>";

    public static void Run()
    {
        LegacyCache().GetAwaiter().GetResult();
        AbilityPhaseCache().GetAwaiter().GetResult();
        WeeklyCache().GetAwaiter().GetResult();
        ManualToEntry().GetAwaiter().GetResult();
        Console.WriteLine("Guide cache: legacy migration, ability-phase cleanup, weekly reuse across restarts, expiry, manual refresh and persistent combat reminders passed.");
    }

    public static void VerifyInstalled(string root, string preparedPath)
    {
        GuideDocument? prepared = null;
        var valid = false;
        try
        {
            var path = System.IO.Path.GetFullPath(preparedPath);
            if (new FileInfo(path).Length > 24 * 1024 * 1024) throw new InvalidDataException("Prepared cache exceeds its size limit.");
            using var envelope = JsonDocument.Parse(File.ReadAllText(path));
            var payload = envelope.RootElement.GetProperty("Payload").GetString() ?? throw new InvalidDataException("Prepared cache payload is missing.");
            if (GuideNames.Hash(payload) != envelope.RootElement.GetProperty("Hash").GetString()) throw new InvalidDataException("Prepared cache integrity check failed.");
            using var metadata = JsonDocument.Parse(payload);
            var duty = metadata.RootElement.GetProperty("Duty").Deserialize<GuideDuty>() ?? throw new InvalidDataException("Prepared cache duty is missing.");
            if (!duty.Valid || System.IO.Path.GetFileName(path) != duty.Key + ".json") throw new InvalidDataException("Prepared cache filename does not match its duty.");
            var revision = metadata.RootElement.GetProperty("ModelRevision").GetString();
            var language = metadata.RootElement.GetProperty("AnalysisLanguage").Deserialize<GuideLanguage>();
            var profile = GuideModelCatalog.Profiles.SingleOrDefault(candidate => candidate.Revision == revision);
            prepared = new ForetellGuideCache(System.IO.Path.GetDirectoryName(path)!).Read(duty);
            var source = new ForetellGuideCache(System.IO.Path.Combine(System.IO.Path.GetFullPath(root), "foretell-guides")).Read(duty);
            valid = prepared != null && source != null && profile != null && Enum.IsDefined(language)
                && prepared.ModelRevision == revision && prepared.AnalysisLanguage == language
                && GuidePageAnalysis.ValidPrepared(prepared, source, language, profile);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("Installed prepared cache is invalid or unreadable.", error);
        }
        finally
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                valid,
                mechCount = prepared?.MechanicCount ?? 0,
                phaseCount = prepared?.Bosses.Sum(boss => boss.PhaseDefinitions?.Length ?? 0) ?? 0
            }));
        }
        if (!valid) throw new InvalidDataException("Installed prepared cache failed validation against its source.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static string Response(string evidence) => JsonSerializer.Serialize(new
    {
        summary = "Prepare mitigation.", bosses = new[] { new
        {
            name = "Sentinel", displayName = "Sentinel", summary = "Prepare mitigation.", mechanics = new[] { new
            {
                name = "Hammer", displayName = "Hammer", cue = "Tank: mitigate", description = "The tank takes heavy damage.",
                triggerKind = "cast", triggerName = "Hammer", evidence = new[] { evidence }, responses = Array.Empty<GuideResponse>()
            } }
        } }
    });

    private static async Task LegacyCache()
    {
        using var directory = new TestDirectory();
        var workbook = new GuideSourcePage(ForetellGuideProviders.WorkbookProvider, "https://docs.google.com/spreadsheets/d/fixture/edit", Passage,
            "<worksheet><selection activeCell='B1'/></worksheet>", "xlsx");
        var states = new[] { new GuideProviderState(workbook.Provider, "Ready", workbook.Url) };
        var legacy = GuideSourceAssembly.CombineLegacy(Duty, new([workbook], states), DateTime.UtcNow);
        var sourceCache = new ForetellGuideCache(directory.Path);
        sourceCache.Write(legacy);
        var source = sourceCache.Read(Duty)!;
        Check(source != null && source.SourceHash != legacy.SourceHash && source.Page!.Text == legacy.Page!.Text,
            "Legacy workbook provenance was not verified and migrated to stable content identity.");
        Check(sourceCache.Read(Duty with { EnglishName = "Hells' Kier (Extreme)" }) == null, "Cache crossed a duty variant.");
        var profile = GuideModelCatalog.Get(GuideModelCatalog.DefaultID);
        var prepared = GuidePageAnalysis.Parse(legacy, legacy.Page!.Text, Response("Hammer: Heavy damage to the tank."), GuideLanguage.English, profile)
            with { Coverage = [new(0, legacy.Page.Text.Length)] };
        var preparedCache = new ForetellGuideCache(System.IO.Path.Combine(directory.Path, "pages", profile.ID, "English"));
        preparedCache.Write(prepared);
        var restored = preparedCache.Read(Duty)!;
        Check(restored != null && GuidePageAnalysis.ValidPrepared(restored, source!, GuideLanguage.English, profile), "Migrated prepared guide lost validated reuse.");
        using var summaries = new ForetellGuideSummaries(directory.Path, _ => throw new InvalidOperationException("Migrated cache started inference."));
        try
        {
            summaries.Update(source, GuideLanguage.English, true, true, false, "");
            await WaitFor(() => summaries.Snapshot?.Stage == "Ready");
            Check(summaries.Snapshot?.Prepared?.MechanicCount == 1, "Migrated guide unavailable in combat.");
            sourceCache.Write(legacy with { Sources = [workbook with { Original = "corrupted legacy provenance" }] });
            Check(sourceCache.Read(Duty) == null, "Legacy migration bypassed the original source fingerprint.");
            sourceCache.Write(source! with { Sources = [workbook with { Text = Passage + "\nNew condition." }] });
            Check(sourceCache.Read(Duty) == null, "Stable cache identity accepted changed source text.");
        }
        finally { summaries.Dispose(); await summaries.Completion; }
    }

    private static async Task AbilityPhaseCache()
    {
        using var directory = new TestDirectory();
        string[] names = ["Hammer", "Wave", "Flare"];
        string[] evidence = ["Hammer: Heavy damage to the tank.", "Wave: Party-wide damage.", "Flare: Spread out."];
        string[] cues = ["Mitigate the hit", "Heal the group", "Spread out"];
        var passage = "Sentinel\n" + string.Join('\n', evidence);
        var source = GuideSourceAssembly.Combine(Duty, new([new("Fixture", "https://example.invalid/guide", passage, passage, "html")], []), DateTime.UtcNow);
        var profile = GuideModelCatalog.Get(GuideModelCatalog.DefaultID);
        var response = JsonSerializer.Serialize(new
        {
            summary = "Watch the boss.", bosses = new[] { new
            {
                name = "Sentinel", displayName = "Sentinel", summary = "Watch the boss.",
                mechanics = names.Select((name, index) => new
                {
                    name, displayName = name, cue = cues[index], description = evidence[index],
                    triggerKind = "cast", triggerName = name, evidence = new[] { evidence[index] }
                }).ToArray()
            } }
        });
        var prepared = GuidePageAnalysis.Parse(source, source.Page!.Text, response, GuideLanguage.English, profile)
            with { Coverage = [new(0, source.Page.Text.Length)] };
        var boss = prepared.Bosses[0];
        // Reproduce old model output: every ability description was accepted as its own combat phase.
        var old = prepared with { Bosses = [boss with
        {
            PhaseDefinitions = names.Select((name, index) => new GuidePhaseDefinition(name, name, [evidence[index]])).ToArray(),
            Phases = boss.Phases.Select(phase => phase with { Mechanics = phase.Mechanics.Select((mechanic, index) =>
                new GuideMechanic(mechanic.Name, mechanic.Text, mechanic.Anchor) { Advice = mechanic.Advice,
                    PhaseMemberships = [new(names[index], [evidence[index]])] }).ToArray() }).ToArray()
        }] };
        var cache = new ForetellGuideCache(Path.Combine(directory.Path, "pages", profile.ID, "English"));
        cache.Write(old);
        var original = JsonSerializer.Serialize(old);
        var restored = cache.Read(Duty)!;
        Check(restored != null && restored.Bosses[0].PhaseDefinitions.Length == 0
            && restored.Bosses[0].Phases.SelectMany(phase => phase.Mechanics).All(mechanic => mechanic.PhaseMemberships.Length == 0),
            "Cached ability descriptions still restrict the boss list to one mechanic");
        Check(GuidePageAnalysis.ValidPrepared(restored!, source, GuideLanguage.English, profile), "Phase cleanup invalidated reusable player instructions");
        Check(original == JsonSerializer.Serialize(old) && restored!.MechanicCount == 3
            && JsonSerializer.Serialize(restored.Bosses[0].Phases[0].Mechanics.Select(mechanic => mechanic.Advice))
                == JsonSerializer.Serialize(old.Bosses[0].Phases[0].Mechanics.Select(mechanic => mechanic.Advice)),
            "Phase cleanup changed the supplied guide or its player instructions");

        using var summaries = new ForetellGuideSummaries(directory.Path, _ => throw new InvalidOperationException("Phase cleanup started model inference."));
        try
        {
            summaries.Update(source, GuideLanguage.English, true, true, false, "Sentinel");
            await WaitFor(() => summaries.Snapshot?.Stage == "Ready");
            var document = summaries.Snapshot!.Prepared!;
            boss = document.Bosses[0];
            var phase = boss.Phases[0];
            var tracker = new GuideEncounterTracker();
            tracker.Update(document, [new(10, 20, 30, "Sentinel", false, true, 5)], false);
            foreach (var mechanic in phase.Mechanics)
            {
                var signal = new GuideSignal(boss, phase, mechanic, GuideSignalKind.Cast, 10, 20, 30, 40, 0,
                    DateTime.UtcNow.AddSeconds(4), GuidanceKind.None, "fixture");
                tracker.Synchronize([signal]);
                var rows = GuideCombatListPresentation.Select(tracker.Frame, [signal], BossMod.Class.None, true);
                Check(tracker.Frame.KnownPhase == null && rows.Length == 3 && rows.Count(row => row.Live != null) == 1
                    && rows.Any(row => row.Mechanic == mechanic && row.Live == signal), "Detected cast hides other boss reminders");
                tracker.Synchronize([]);
                rows = GuideCombatListPresentation.Select(tracker.Frame, [], BossMod.Class.None, true);
                Check(rows.Length == 3 && rows.All(row => row.Live == null), "Boss reminders disappear between casts");
            }
        }
        finally { summaries.Dispose(); await summaries.Completion; }
    }

    private static async Task WeeklyCache()
    {
        foreach (var scenario in new (double Days, bool Refresh, bool Corrupt)[] { (1, false, false), (6.99, false, false), (7.01, false, false), (1, true, false), (1, false, true) })
        {
            using var directory = new TestDirectory();
            var savedAt = DateTime.UtcNow.AddDays(-scenario.Days);
            var source = GuideSourceAssembly.Combine(Duty, new([new(ForetellGuideProviders.WikiProvider, "https://ffxiv.consolegameswiki.com/fixture", Passage, Html, "html")], []), savedAt);
            var sourceDirectory = Path.Combine(directory.Path, "sources");
            new ForetellGuideCache(sourceDirectory).Write(source);
            if (scenario.Corrupt) File.WriteAllText(Path.Combine(sourceDirectory, Duty.Key + ".json"), "invalid cache");
            var requests = 0;
            using var handler = new Handler((request, _) =>
            {
                Interlocked.Increment(ref requests);
                return Task.FromResult(request.RequestUri!.Host == "ffxiv.consolegameswiki.com"
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                        { parse = new { title = Duty.EnglishName, revid = 1436842, text = Html } }), Encoding.UTF8, "application/json") }
                    : new(HttpStatusCode.NotFound));
            });
            using var providers = new ForetellGuideProviders(Path.Combine(directory.Path, "providers"), handler);
            using var guides = new ForetellGuideService(sourceDirectory, sources: providers);
            try
            {
                guides.RequestGuide(Duty, scenario.Refresh);
                await WaitFor(() => !guides.HasPendingRequest);
                Check(guides.Snapshot.State == GuideState.Ready, "Weekly cache did not produce an available guide");
                var shouldFetch = scenario.Days >= 7 || scenario.Refresh || scenario.Corrupt;
                Check(shouldFetch ? requests > 0 : requests == 0, "Weekly cache ignored its age, explicit refresh or integrity check");
                if (shouldFetch) continue;
                Check(guides.Snapshot.FromCache && guides.Snapshot.Document!.RetrievedAt == savedAt
                    && new ForetellGuideCache(sourceDirectory).Read(Duty)!.RetrievedAt == savedAt,
                    "Reading a recent guide extended its expiry or hid its cache status");
                var profile = GuideModelCatalog.Get(GuideModelCatalog.DefaultID);
                var prepared = GuidePageAnalysis.Parse(source, source.Page!.Text, Response("Hammer: Heavy damage to the tank."), GuideLanguage.English, profile)
                    with { Coverage = [new(0, source.Page.Text.Length)] };
                new ForetellGuideCache(Path.Combine(directory.Path, "summaries", "pages", profile.ID, "English")).Write(prepared);
                using var summaries = new ForetellGuideSummaries(Path.Combine(directory.Path, "summaries"), _ => throw new InvalidOperationException("Recent saved guide started AI analysis."));
                try
                {
                    summaries.Update(guides.Snapshot.Document, GuideLanguage.English, true, false, false, "Sentinel");
                    await WaitFor(() => summaries.Snapshot?.Stage == "Ready");
                    Check(summaries.Snapshot!.Prepared!.MechanicCount == 1 && requests == 0, "A saved guide was not reused after restarting services");
                }
                finally { summaries.Dispose(); await summaries.Completion; }
            }
            finally { guides.Dispose(); await guides.Completion; }
        }
    }

    private static async Task ManualToEntry()
    {
        using var directory = new TestDirectory();
        var mode = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var networkChecks = 0;
        using var handler = new Handler(async (request, cancellation) =>
        {
            if (request.RequestUri!.Host != "ffxiv.consolegameswiki.com") return new(HttpStatusCode.NotFound);
            Interlocked.Increment(ref networkChecks);
            if (mode == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellation);
            }
            if (mode == 2) return new(HttpStatusCode.NotFound);
            var html = mode >= 3 ? Html + "<p>New condition: stay near the boss.</p>" : Html;
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                { parse = new { title = Duty.EnglishName, revid = 1436842, text = html } }), Encoding.UTF8, "application/json") };
        });
        using var providers = new ForetellGuideProviders(System.IO.Path.Combine(directory.Path, "providers"), handler);
        using var guides = new ForetellGuideService(System.IO.Path.Combine(directory.Path, "sources"), sources: providers);
        var modelStarts = 0;
        using var summaries = new ForetellGuideSummaries(System.IO.Path.Combine(directory.Path, "summaries"), _ =>
        {
            Interlocked.Increment(ref modelStarts);
            return new FixtureModel();
        });
        try
        {
            guides.RequestGuide(Duty);
            await WaitFor(() => guides.Snapshot is { State: GuideState.Ready, Document: not null });
            var manual = guides.Snapshot.Document!;
            summaries.Update(manual, GuideLanguage.English, true, false, false, "");
            await WaitFor(() => summaries.Snapshot?.Stage == "Ready");
            Check(modelStarts == 1, "Manual preparation did not complete exactly once.");
            var prepared = summaries.Snapshot!.Prepared!;

            mode = 1;
            guides.Cancel();
            guides.RequestGuide(new(Duty.ContentID, Duty.TerritoryID, Duty.EnglishName));
            Check(guides.Snapshot is { State: GuideState.Ready, FromCache: true } && guides.Snapshot.Document?.SourceHash == manual.SourceHash,
                "Entering the manually prepared instance hid the cached source.");
            await WaitFor(() => !guides.HasPendingRequest);
            Check(networkChecks == 1 && !entered.Task.IsCompleted, "Re-entering a recently prepared instance contacted providers");
            summaries.Update(guides.Snapshot.Document, GuideLanguage.English, true, true, false, "Sentinel");
            Check(ReferenceEquals(summaries.Snapshot?.Prepared, prepared) && modelStarts == 1, "Entry queued inference or hid prepared advice during source checks.");
            var now = DateTime.UtcNow;
            Check(GuideSynchronization.Match(prepared, new(Duty, 1, 2, 3, "Sentinel", 4, "Hammer", now.AddSeconds(5)), now) != null,
                "Manually prepared advice was not immediately matchable to a live cast.");
            guides.RequestGuide(Duty, true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.TrySetResult();
            await WaitFor(() => !ReferenceEquals(guides.Snapshot.Document, manual));
            Check(networkChecks == 2 && guides.Snapshot.FromCache, "Manual refresh did not recheck sources or treated unchanged input as new analysis.");
            summaries.Update(guides.Snapshot.Document, GuideLanguage.English, true, true, false, "Sentinel");
            Check(summaries.Snapshot?.Stage == "Ready" && modelStarts == 1, "Unchanged entry refresh re-ran inference.");

            mode = 2;
            foreach (var path in Directory.GetFiles(System.IO.Path.Combine(directory.Path, "providers", "http-sources-v1"), "*.json")) File.Delete(path);
            var beforeMissing = guides.Snapshot.Document;
            guides.RequestGuide(Duty, true);
            await WaitFor(() => !ReferenceEquals(guides.Snapshot.Document, beforeMissing));
            summaries.Update(guides.Snapshot.Document, GuideLanguage.English, true, false, false, "");
            Check(guides.Snapshot.Document?.SourceHash == manual.SourceHash && modelStarts == 1, "A missing provider discarded prepared content.");
            Check(summaries.Snapshot?.Prepared?.Providers.Single(state => state.Provider == ForetellGuideProviders.WikiProvider)
                is { Status: "Cached", Error.Length: > 0 }, "Prepared snapshot did not update provider fallback diagnostics.");

            mode = 0;
            var beforeRecovery = guides.Snapshot.Document;
            guides.RequestGuide(Duty, true);
            await WaitFor(() => !ReferenceEquals(guides.Snapshot.Document, beforeRecovery));
            summaries.Update(guides.Snapshot.Document, GuideLanguage.English, true, false, false, "");
            Check(guides.Snapshot.FromCache && modelStarts == 1 && summaries.Snapshot?.Prepared?.Providers
                .Single(state => state.Provider == ForetellGuideProviders.WikiProvider) is { Status: "Ready", Error.Length: 0 },
                "Unchanged explicit refresh repeated inference or kept stale provider failure diagnostics.");

            mode = 3;
            var beforeChange = guides.Snapshot.Document;
            guides.RequestGuide(Duty, true);
            await WaitFor(() => !ReferenceEquals(guides.Snapshot.Document, beforeChange));
            var changed = guides.Snapshot.Document!;
            Check(changed.SourceHash != manual.SourceHash && !guides.Snapshot.FromCache, "A real provider content update did not invalidate analysis.");
            summaries.Update(changed, GuideLanguage.English, true, false, false, "");
            await WaitFor(() => summaries.Snapshot?.Stage == "Ready");
            Check(modelStarts == 2 && summaries.Snapshot?.Prepared?.SourceHash == changed.SourceHash, "Changed source did not receive fresh preparation.");
            summaries.Retry();
            summaries.Update(changed, GuideLanguage.English, true, true, false, "");
            await WaitFor(() => summaries.Snapshot?.Stage == "Ready");
            Check(modelStarts == 2, "Retry ignored a valid prepared cache.");
            summaries.Retry(true);
            summaries.Update(changed, GuideLanguage.English, true, false, false, "");
            await WaitFor(() => summaries.Snapshot?.Stage == "Ready");
            Check(modelStarts == 3, "Explicit reanalysis did not bypass the prepared cache.");
        }
        finally
        {
            release.TrySetResult();
            guides.Dispose(); summaries.Dispose();
            await Task.WhenAll(guides.Completion, summaries.Completion);
        }
    }

    private sealed class FixtureModel : IGuideSummaryModel
    {
        public GuideModelRuntime Runtime => new();
        public Task Start(Action<GuideModelProgress> progress, CancellationToken cancellation) => Task.CompletedTask;
        public Task<string> Summarize(string source, GuideLanguage language, CancellationToken cancellation) => throw new InvalidOperationException("Unexpected legacy summary.");
        public Task<string> Analyze(string system, string source, object schema, int outputTokens, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (system.StartsWith("Read the ENTIRE", StringComparison.Ordinal))
                return Task.FromResult(JsonSerializer.Serialize(new { bosses = new[] { new { name = "Sentinel", passages = new[] { Passage } } } }));
            var paragraph = source.Split('\n').First(line => line.StartsWith('[') && line.Contains("Hammer: Heavy damage to the tank.", StringComparison.Ordinal));
            return Task.FromResult(Response(paragraph[1..paragraph.IndexOf(']')]));
        }
        public void Dispose() { }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => respond(request, cancellation);
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foretell-guide-cache-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            if (resolved.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
