using BossMod.Foretell;
using System.Text.Json;
using System.Threading.Channels;

internal static class GuidePauseTests
{
    public static void Run()
    {
        Configuration();
        CompletedResponseCancellation().GetAwaiter().GetResult();
        WorkerPause().GetAwaiter().GetResult();
        Console.WriteLine("Guide analysis: optional combat pause, preserved progress, repeated resume and completed-response cancellation passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Configuration()
    {
        var config = new ForetellConfig();
        Check(!config.GuidePauseInCombat, "Combat pause is enabled by default.");
        using var previous = JsonDocument.Parse("{\"GuideLocalSummaries\":true}");
        config.Deserialize(previous.RootElement, new());
        Check(!config.GuidePauseInCombat, "An existing configuration enabled combat pause.");
        using var enabled = JsonDocument.Parse("{\"GuidePauseInCombat\":true}");
        config.Deserialize(enabled.RootElement, new());
        Check(config.GuidePauseInCombat, "The combat pause preference was not restored.");
    }

    private static async Task CompletedResponseCancellation()
    {
        var memory = new GuideAnalysisMemory();
        using var cancellation = new CancellationTokenSource();
        using var original = new GuideResumableModel(new ResponseModel(() =>
        {
            cancellation.Cancel();
            return Task.FromResult("complete response");
        }), memory);
        try
        {
            await original.Analyze("system", "source", new { type = "object" }, 100, cancellation.Token);
            throw new InvalidOperationException("Completed-response cancellation was ignored.");
        }
        catch (OperationCanceledException) { }
        Check(memory.Responses.Count == 1 && memory.Characters == "complete response".Length,
            "A fully returned response was discarded when combat started at completion.");
        using var replacement = new GuideResumableModel(new ResponseModel(() => throw new InvalidOperationException("A completed response ran again.")), memory);
        Check(await replacement.Analyze("system", "source", new { type = "object" }, 100, CancellationToken.None) == "complete response",
            "The completed response did not survive model replacement.");
        using var interrupted = new GuideResumableModel(new ResponseModel(() => Task.FromCanceled<string>(cancellation.Token)), memory);
        try
        {
            await interrupted.Analyze("system", "unfinished", new { type = "object" }, 100, CancellationToken.None);
            throw new InvalidOperationException("An interrupted response was accepted.");
        }
        catch (OperationCanceledException) { }
        Check(memory.Responses.Count == 1, "An interrupted model response was cached.");
    }

    private static async Task WorkerPause()
    {
        var directory = Path.Combine(Path.GetTempPath(), "foretell-pause-tests-" + Guid.NewGuid());
        var duty = new GuideDuty(901, 902, "Pause test chamber");
        var source = ForetellGuideParser.ReadPage(JsonSerializer.Serialize(new
        {
            parse = new { title = duty.EnglishName, revid = 1,
                text = "<p>Sentinel</p><p>Hammer: Heavy damage to the tank.</p><p>Keeper</p><p>Pulse: Everyone takes damage.</p>" }
        }), duty, DateTime.UtcNow);
        var config = new ForetellConfig();
        var model = new PausingModel();
        using var service = new ForetellGuideSummaries(directory, _ => model);
        model.Snapshot = () => service.Snapshot;
        void Update(bool combat) => service.Update(source, GuideLanguage.English, true, combat && config.GuidePauseInCombat, false, "Keeper");
        try
        {
            Update(true);
            var pending = await model.Next();
            Check(service.Snapshot is { Stage: "Analyzing", Completed: 1, Total: 2, Prepared.MechanicCount: 1 },
                "Default settings did not allow analysis to progress during combat.");
            for (var repetition = 0; repetition < 2; ++repetition)
            {
                config.GuidePauseInCombat = true;
                Update(true);
                await WaitFor(() => service.Snapshot?.Stage == "PausedInCombat");
                Check(pending.Cancellation.IsCancellationRequested, "Enabling combat pause did not cancel the active request.");
                Check(service.Snapshot is { Completed: 1, Total: 2, Prepared.MechanicCount: 1 },
                    "Combat pause lost completed boss progress or prepared advice.");
                config.GuidePauseInCombat = false;
                Update(true);
                pending = await model.Next();
                Check(model.Outlines == 1 && model.SentinelCalls == 2, "Resuming repeated completed analysis requests.");
                Check(model.Starts == repetition + 2 && service.Snapshot is { Completed: 1, Total: 2, Prepared.MechanicCount: 1 },
                    "Model restart lost completed boss progress.");
                Check(service.Snapshot?.RemainingSeconds == null, "Replayed responses produced a misleading remaining-time estimate.");
                Check(model.StartSnapshots.Skip(1).All(snapshot => snapshot is { Completed: 1, Total: 2, Prepared.MechanicCount: 1 }),
                    "Installing or starting reset the displayed progress.");
            }
            pending.Release.TrySetResult();
            (await model.Next()).Release.TrySetResult();
            await WaitFor(() => service.Snapshot?.Stage == "Ready");
            Check(service.Snapshot?.Prepared?.MechanicCount == 2 && model.Outlines == 1 && model.SentinelCalls == 2,
                "The resumed guide did not finish without recomputing completed bosses.");
            service.Retry(true);
            Update(true);
            (await model.Next()).Release.TrySetResult();
            (await model.Next()).Release.TrySetResult();
            await WaitFor(() => service.Snapshot?.Stage == "Ready");
            Check(model.Outlines == 2 && model.SentinelCalls == 4, "Explicit retry during unpaused combat did not refresh analysis.");
        }
        finally
        {
            service.Dispose();
            await service.Completion;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class ResponseModel(Func<Task<string>> response) : IGuideSummaryModel
    {
        public GuideModelRuntime Runtime => new();
        public Task Start(Action<GuideModelProgress> progress, CancellationToken cancellation) => Task.CompletedTask;
        public Task<string> Summarize(string source, GuideLanguage language, CancellationToken cancellation) => throw new InvalidOperationException("Unexpected legacy summary.");
        public Task<string> Analyze(string system, string source, object schema, int outputTokens, CancellationToken cancellation) => response();
        public void Dispose() { }
    }

    private sealed record Pending(CancellationToken Cancellation)
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PausingModel : IGuideSummaryModel
    {
        private readonly Channel<Pending> _pending = Channel.CreateUnbounded<Pending>();
        public int Starts;
        public int Outlines;
        public int SentinelCalls;
        public readonly List<GuideSummarySnapshot?> StartSnapshots = [];
        public Func<GuideSummarySnapshot?> Snapshot = () => null;
        public GuideModelRuntime Runtime => new();
        public Task<Pending> Next() => _pending.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public Task Start(Action<GuideModelProgress> progress, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            ++Starts;
            progress(new("Starting"));
            StartSnapshots.Add(Snapshot());
            return Task.CompletedTask;
        }
        public Task<string> Summarize(string source, GuideLanguage language, CancellationToken cancellation) => throw new InvalidOperationException("Unexpected legacy summary.");
        public async Task<string> Analyze(string system, string source, object schema, int outputTokens, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (system.StartsWith("Read the ENTIRE", StringComparison.Ordinal))
            {
                ++Outlines;
                return JsonSerializer.Serialize(new { bosses = new[]
                {
                    new { name = "Sentinel", passages = new[] { "Sentinel\nHammer: Heavy damage to the tank." } },
                    new { name = "Keeper", passages = new[] { "Keeper\nPulse: Everyone takes damage." } }
                } });
            }
            var sentinel = source.StartsWith("Boss: Sentinel", StringComparison.Ordinal);
            if (sentinel) ++SentinelCalls;
            else
            {
                var pending = new Pending(cancellation);
                _pending.Writer.TryWrite(pending);
                await pending.Release.Task.WaitAsync(cancellation);
            }
            var boss = sentinel ? "Sentinel" : "Keeper";
            var ability = sentinel ? "Hammer" : "Pulse";
            var instruction = sentinel ? "Tank: mitigate" : "Heal the group";
            return JsonSerializer.Serialize(new { summary = instruction, bosses = new[] { new
            {
                name = boss, displayName = boss, summary = instruction, mechanics = new[] { new
                {
                    name = ability, displayName = ability, cue = instruction, description = instruction,
                    triggerKind = "cast", triggerName = ability, evidence = new[] { "2" }, responses = Array.Empty<GuideResponse>()
                } }
            } } });
        }
        public void Dispose() { }
    }
}
