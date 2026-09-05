using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using BossMod.Foretell;

internal static class Program
{
    public static void Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
            var file = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
        };
        if (args.Length == 0) Run(); else EvaluateFiles(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        var at = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
        var context = new DecisionContextSnapshot
        {
            ID = 1, At = at, Complete = true, InCombat = true, Duty = 1, Party = [1, 2, 3, 4],
            Actors = [
                new(10, 100, 0x205, 0, 100, 0, 0, 0, 0, 3, 10000, 10000, true, false, false, true, true, 1),
                new(1, 0, 0x104, 19, 100, 0, 0, 1, 0, 1, 100, 100, true, false, false, true, false, 10),
                new(2, 0, 0x104, 24, 100, 1, 0, 1, 0, 1, 100, 100, true, false, false, true, false, 10),
                new(3, 0, 0x104, 25, 100, 0, 0, 15, 0, 1, 100, 100, true, false, false, true, false, 10),
                new(4, 0, 0x104, 23, 100, -10, 0, 10, 0, 1, 100, 100, true, false, false, true, false, 10)]
        };
        var events = new List<ForetellObservation>();
        long sequence = 0;
        ForetellObservation Add(ObservationKind kind, double seconds, ulong actor = 10, ulong target = 0)
        {
            var observation = new ForetellObservation { Sequence = ++sequence, At = at.AddSeconds(seconds), TerritoryID = 1,
                Kind = kind, ActorID = actor, ActorOID = actor == 10 ? 100u : 0, SourceKind = actor == 10 ? SourceKind.Enemy : SourceKind.Player,
                PrimaryID = 200, ContextID = 1, TargetID = target };
            events.Add(observation); return observation;
        }
        var first = Add(ObservationKind.DecisionFrame, 0); first.Context = context;
        foreach (var actor in context.Actors.Where(a => a.ID != 10))
        {
            var position = Add(ObservationKind.PositionSample, .1, actor.ID); position.X = actor.X; position.Z = actor.Z;
        }
        var cast = Add(ObservationKind.CastStart, .2); cast.Value1 = 2;
        cast.Prior = new(200, GeometryKind.Circle, MechanicKind.GroundAOE, 5, 0, .96f, 2, 5, 0, true, 0, "", 0, "recorded client shape");
        foreach (var actor in context.Actors.Where(a => a.ID != 10))
        {
            var position = Add(ObservationKind.PositionSample, 2.2, actor.ID); position.X = actor.X; position.Z = actor.Z;
        }
        var action = Add(ObservationKind.ActionResolved, 2.3); action.Prior = cast.Prior; action.Numeric["action.globalSequence"] = 42;
        foreach (var target in new ulong[] { 1, 2 })
        {
            var effect = Add(ObservationKind.AffectedTarget, 2.3, target: target);
            effect.Numeric["actionEffect.0.type"] = 3; effect.Numeric["actionEffect.0.atSource"] = 0;
            effect.Numeric["actionEffect.0.damageHealValue"] = 100;
        }
        Add(ObservationKind.DecisionFrame, 20);
        var original = JsonSerializer.Serialize(events);
        var repeated = new List<ForetellObservation>();
        for (var repetition = 0; repetition < 4; ++repetition)
        {
            var copy = JsonSerializer.Deserialize<List<ForetellObservation>>(original)!;
            foreach (var item in copy)
            {
                item.At = item.At.AddSeconds(30 * repetition); item.Sequence += 100 * repetition;
                item.ContextID += repetition;
                if (item.Context != null) { item.Context.ID += repetition; item.Context.At = item.At; }
            }
            var followup = new ForetellObservation { At = at.AddSeconds(4.3 + 30 * repetition), Sequence = 90 + 100 * repetition,
                ContextID = 1 + repetition, TerritoryID = 1, ActorID = 10, ActorOID = 100, SourceKind = SourceKind.Enemy,
                Kind = ObservationKind.ActionResolved, PrimaryID = 201, X = 10, TargetX = 10, Prior = cast.Prior };
            copy.Add(followup); repeated.AddRange(copy);
        }
        var program = ForetellEngine.EvaluateRecordedObservations(repeated);
        object Calibration(ForetellStore store) => store.Encounters.Values.Select(e => new { e.Timeline, e.TriggerContexts, e.Composites }).ToArray();
        var calibration = JsonSerializer.Serialize(Calibration(program.Knowledge));
        var frozenProgram = ForetellEngine.EvaluateRecordedObservations(repeated, program.Knowledge, learn: false);
        Check(calibration == JsonSerializer.Serialize(Calibration(frozenProgram.Knowledge)), "Frozen evaluation updated timing or composite calibration");
        var learnedStage = program.Knowledge.Encounters.Values.SelectMany(e => e.Mechanics.Values).SelectMany(m => m.Stages).Single();
        Check(learnedStage.Observations == 4 && learnedStage.Hits == 1 && learnedStage.Misses == 0,
            "A follow-up stage was not learned or counted observations as issued predictions");
        // No Dalamud Service initialization, native scene, current player, or shared live state exists in this process.
        var firstRun = ForetellEngine.EvaluateRecordedObservations(events);
        var secondRun = ForetellEngine.EvaluateRecordedObservations(events);
        Check(firstRun.Report.DecisionDigest == secondRun.Report.DecisionDigest, "Repeated detached evaluation diverged");
        Check(original == JsonSerializer.Serialize(events), "Replay mutated caller observations");
        Check(firstRun.Report.MissingContexts == 0, "Recorded decision context was ignored");
        Check(firstRun.Report.Assessed == 1 && firstRun.Report.Correct == 1, "The real engine did not score the issued footprint exactly once");
        Check(firstRun.Report.RediscoveredMechanics > 0, "The real learner did not execute");
        var contradicted = JsonSerializer.Deserialize<List<ForetellObservation>>(original)!;
        var badCast = contradicted.Single(o => o.Kind == ObservationKind.CastStart);
        badCast.X = badCast.TargetX = 30;
        var wrong = ForetellEngine.EvaluateRecordedObservations(contradicted);
        Check(wrong.Report.Incorrect == 1, "The real engine credited a footprint at the wrong position");
        var seed = JsonSerializer.Serialize(firstRun.Knowledge);
        var frozen = ForetellEngine.EvaluateRecordedObservations(events, firstRun.Knowledge, learn: false);
        Check(seed == JsonSerializer.Serialize(firstRun.Knowledge), "Frozen replay mutated the supplied knowledge");
        Check(frozen.Report.Assessed == 1, "Frozen replay mixed training-run audit outcomes into evaluation metrics");
        Check(frozen.Knowledge.PreImpact.Model.Updates == firstRun.Knowledge.PreImpact.Model.Updates, "Frozen replay trained the model");
        var before = firstRun.Knowledge.DecisionAudit.Where(d => d.At <= cast.At).Select(d => new { d.Stage, d.Mechanic, d.Geometry, d.Confidence }).ToArray();
        var changedOutcome = JsonSerializer.Deserialize<List<ForetellObservation>>(original)!;
        foreach (var effect in changedOutcome.Where(o => o.Kind == ObservationKind.AffectedTarget)) effect.Numeric.Clear();
        var changed = ForetellEngine.EvaluateRecordedObservations(changedOutcome);
        Check(JsonSerializer.Serialize(before) == JsonSerializer.Serialize(changed.Knowledge.DecisionAudit.Where(d => d.At <= cast.At)
            .Select(d => new { d.Stage, d.Mechanic, d.Geometry, d.Confidence }).ToArray()), "Future outcomes changed earlier decisions");
        CaptureTests.Run(events, firstRun.Report.DecisionDigest);
        foreach (var observation in events) { observation.Context = null; observation.ContextID = 0; }
        var legacy = ForetellEngine.EvaluateRecordedObservations(events);
        Check(legacy.Report.MissingContexts > 0 && legacy.Report.Assessed == 0, "Legacy context gaps were reported as validated outcomes");
        Console.WriteLine($"Detached runtime tests passed: {firstRun.Report.DecisionDigest}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EvaluateFiles(string[] args)
    {
        string Argument(string flag)
        {
            var index = Array.IndexOf(args, flag);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException($"Missing {flag}");
        }
        var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var output = Path.GetFullPath(Argument("--out"));
        if (Array.IndexOf(args, "--inspect") >= 0)
        {
            var input = new ForetellRecordingReader(Argument("--inspect")); input.Inspect();
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "capture-summary.json"), JsonSerializer.Serialize(new
            { input.Parsed, input.Rejected, input.Complete, input.First, input.Last }, options));
            Console.WriteLine($"Capture: {input.Parsed:N0} events; complete={input.Complete}; {input.Rejected:N0} rejected");
            return;
        }
        var trainingPath = Path.GetFullPath(Argument("--train"));
        var evaluationPath = Path.GetFullPath(Argument("--evaluate"));
        if (string.Equals(trainingPath, evaluationPath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Training and evaluation recordings must differ");
        var trainingInput = new ForetellRecordingReader(trainingPath); trainingInput.Inspect();
        var evaluationInput = new ForetellRecordingReader(evaluationPath); evaluationInput.Inspect();
        if (trainingInput.Last >= evaluationInput.First)
            throw new ArgumentException("Evaluation must be a separate recording strictly after the training period");
        var training = ForetellEngine.EvaluateRecordedStream(trainingInput.Read(), captureComplete: trainingInput.Complete);
        training.Report.Rejected = (int)Math.Min(int.MaxValue, trainingInput.Rejected);
        var result = ForetellEngine.EvaluateRecordedStream(evaluationInput.Read(), training.Knowledge, learn: false, captureComplete: evaluationInput.Complete);
        result.Report.Rejected = (int)Math.Min(int.MaxValue, evaluationInput.Rejected);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "training-report.json"), JsonSerializer.Serialize(training.Report, options));
        File.WriteAllText(Path.Combine(output, "evaluation-report.json"), JsonSerializer.Serialize(result.Report, options));
        File.WriteAllText(Path.Combine(output, "evaluation-decisions.json"), JsonSerializer.Serialize(result.Knowledge.DecisionAudit, options));
        Console.WriteLine(result.Report.Status);
        Console.WriteLine($"Decision digest: {result.Report.DecisionDigest}");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
