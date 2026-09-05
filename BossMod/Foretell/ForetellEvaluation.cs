using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private readonly bool _isReplay;
    private readonly bool _evaluationAllowLearning;
    private Action<DecisionAuditEntry>? _evaluationAuditSink;
    private DateTime _evaluationNow;
    private DecisionContextSnapshot? _decisionContext;
    private DateTime LearningNow => _evaluationNow == default ? DateTime.UtcNow : _evaluationNow.ToUniversalTime();
    private bool DecisionCombat => _isReplay ? _decisionContext?.InCombat == true
        : Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];

    // Detached engine: no subscriptions, native hooks, game services, writers, or shared mutable learning state.
    private ForetellEngine(DateTime at, ForetellStore knowledge, bool learn)
    {
        _isReplay = true;
        _evaluationAllowLearning = learn;
        _evaluationNow = at;
        _ws = new(1, "recorded-decision-context", initializeSupportedItems: false);
        _cfg = new() { EnableLearning = learn, EnableML = true, RecordReplay = false, Mode = ForetellMode.Foretell };
        _storePath = _replayDir = _rawDir = _signalFilterPath = "";
        _subscriptions = new();
        _raw = null!; // never constructed or accessed by a detached evaluator
        _store = knowledge;
        NormalizeStore();
        _preImpact = new(_store.PreImpact);
        if (!learn) _preImpact.FreezeCalibration();
        _session = NewSession(0);
    }

    private void PrepareDecisionContext(ForetellObservation observation)
    {
        _evaluationNow = observation.At;
        if (_isReplay)
        {
            if (observation.Context is { } context)
            {
                if (context.Actors == null || context.Actors.Length > 256 || context.Party == null || context.Party.Length > 64)
                    throw new InvalidDataException("Invalid decision context bounds");
                _decisionContext = context;
                _cfg.EnableLearning = _evaluationAllowLearning && context.Learning;
                _cfg.EnableML = context.ML;
                _cfg.VisualConfidence = Finite(context.VisualThreshold, 75, 0, 100);
                _cfg.WarningConfidence = Finite(context.WarningThreshold, 95, 0, 100);
                _cfg.SafeConfidence = Finite(context.StrictThreshold, 99, 0, 100);
                _ws.CurrentCFCID = context.Duty;
                _ws.CurrentZone = (ushort)observation.TerritoryID;
                _ws.Actors.Actors.Clear();
                foreach (var snapshot in context.Actors)
                {
                    if (!float.IsFinite(snapshot.X) || !float.IsFinite(snapshot.Y) || !float.IsFinite(snapshot.Z)
                        || !float.IsFinite(snapshot.Rotation) || !float.IsFinite(snapshot.Hitbox))
                        throw new InvalidDataException("Non-finite decision actor");
                    var actor = new Actor(snapshot.ID, snapshot.OID, 0, 0, "", 0, (ActorType)snapshot.Type,
                        (Class)snapshot.Job, snapshot.Level, new(snapshot.X, snapshot.Y, snapshot.Z, snapshot.Rotation),
                        snapshot.Hitbox, new(snapshot.HP, snapshot.MaxHP, 0, 0, 0), snapshot.Targetable, snapshot.Ally, resolveGameMetadata: false)
                    { IsDead = snapshot.Dead, InCombat = snapshot.Combat, AggroPlayer = snapshot.Aggro, TargetID = snapshot.Target };
                    _ws.Actors.Actors[actor.InstanceID] = actor;
                }
                for (var slot = 0; slot < 64; ++slot)
                    _ws.Execute(new PartyState.OpModify(slot, new(0, slot < context.Party.Length ? context.Party[slot] : 0, false)));
                _outcomeGapGeneration = Math.Max(_outcomeGapGeneration, context.OutcomeGap);
            }
            _ws.Frame = new(observation.At, 0, 0, .016f, .016f, 1);
            if (observation.Kind != ObservationKind.GenericFeature && (observation.ContextID == 0
                || _decisionContext?.ID != observation.ContextID || _decisionContext.Complete != true))
                ++_outcomeGapGeneration;
            return;
        }

        if (_capture == null && !_cfg.RecordReplay) return;
        var id = _ws.CurrentTime.Ticks;
        if (_decisionContext?.ID != id)
        {
            var player = _ws.Party[PartyState.PlayerSlot];
            var nearby = _ws.Actors.Where(a => a.InstanceID == player?.InstanceID
                || player == null || Vector2.Distance(V(a.Position), V(player.Position)) <= 120).Take(257).ToArray();
            var actors = nearby.Take(256).Select(a => new DecisionActorSnapshot(a.InstanceID, a.OID, (ushort)a.Type,
                (uint)a.Class, a.Level, a.PosRot.X, a.PosRot.Y, a.PosRot.Z, a.PosRot.W, a.HitboxRadius,
                a.HPMP.CurHP, a.HPMP.MaxHP, a.IsTargetable, a.IsAlly, a.IsDeadOrDestroyed, a.InCombat, a.AggroPlayer, a.TargetID)).ToArray();
            var bossID = TryBossHealth(0, 0, observation.At, out var boss, out _, out _) ? boss.InstanceID : 0;
            _decisionContext = new()
            {
                Learning = _cfg.EnableLearning, ML = _cfg.EnableML, VisualThreshold = _cfg.VisualConfidence,
                WarningThreshold = _cfg.WarningConfidence, StrictThreshold = _cfg.SafeConfidence,
                ID = id, At = observation.At, Duty = _ws.CurrentCFCID, InCombat = DecisionCombat,
                Complete = nearby.Length <= 256, OutcomeGap = _outcomeGapGeneration, BossID = bossID,
                Party = _ws.Party.Members.Select(m => m.InstanceId).ToArray(), Actors = actors
            };
        }
        observation.ContextID = id;
        // Each writer performs its own deduplication after accepting the event. A dropped event or a newly
        // sealed part must not strand following observations with a context ID that was never written.
        observation.Context = _decisionContext;
        observation.Numeric["decision.outcomeGap"] = _outcomeGapGeneration;
    }

    public static ForetellReplayEvaluation EvaluateRecordedObservations(IReadOnlyList<ForetellObservation> observations,
        ForetellStore? initialKnowledge = null, bool learn = true, bool captureComplete = true, System.Threading.CancellationToken cancellationToken = default)
        => EvaluateRecordedStream(observations.OrderBy(o => o.At).ThenBy(o => o.Sequence), initialKnowledge, learn, captureComplete, cancellationToken);

    public static ForetellReplayEvaluation EvaluateRecordedStream(IEnumerable<ForetellObservation> observations,
        ForetellStore? initialKnowledge = null, bool learn = true, bool captureComplete = true, System.Threading.CancellationToken cancellationToken = default)
    {
        using var cursor = observations.GetEnumerator();
        var hasFirst = cursor.MoveNext();
        var options = new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var knowledge = initialKnowledge == null ? new ForetellStore()
            : JsonSerializer.Deserialize<ForetellStore>(JsonSerializer.Serialize(initialKnowledge, options), options)!;
        knowledge.DecisionAudit ??= [];
        knowledge.DecisionAudit.Clear();
        var first = hasFirst ? cursor.Current.At : DateTime.UnixEpoch;
        var engine = new ForetellEngine(first, knowledge, learn);
        var report = new ReplayReport { First = first, Last = first };
        var initialAssessed = knowledge.PreImpact.Classes.Values.Sum(c => c.Assessed);
        var initialCorrect = knowledge.PreImpact.Classes.Values.Sum(c => c.Hits);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        engine._evaluationAuditSink = d =>
        {
            ++report.AuditEntries;
            digest.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { d.At, d.Activation, d.PredictionID, d.Stage,
                d.SignalKey, d.Mechanic, d.Geometry, d.Guidance, d.P1, d.P2, d.OriginX, d.OriginZ, d.TargetX, d.TargetZ,
                d.Rotation, d.Confidence, d.Verified, d.Validation }, options));
            digest.AppendData([10]);
            if (d.Validation == PredictionValidationKind.TriggerTiming)
            {
                if (d.Verified is bool timing) { ++report.TriggerAssessed; if (timing) ++report.TriggerCorrect; }
                else ++report.TriggerUnverifiable;
            }
            else if (d.Stage == DecisionAuditStage.Verified && d.Validation == PredictionValidationKind.Outcome)
            {
                if (d.Verified == null) ++report.Unverifiable;
                else { ++report.Assessed; if (d.Verified == true) ++report.Correct; else ++report.Incorrect; }
            }
        };
        while (hasFirst)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = cursor.Current;
            // The reducer normalizes its inputs. Clone each event so callers and repeated evaluations are unchanged.
            var observation = JsonSerializer.Deserialize<ForetellObservation>(JsonSerializer.Serialize(original, options), options)!;
            hasFirst = cursor.MoveNext();
            if ((observation.Detail ?? "").StartsWith("transport:", StringComparison.Ordinal)) continue;
            if (observation.TerritoryID != engine._territory)
            {
                engine.FinalizeDue(DateTime.MaxValue, exhaustive: true);
                engine.EndCombatPull();
                engine._episodes.Clear(); engine._tracks.Clear(); engine._effectSequenceEpisodes.Clear();
                engine._episodeFinalization.Clear(); engine._episodeCleanup.Clear();
                engine._precursorCues.Clear();
                engine._territory = observation.TerritoryID;
                engine._session = engine.NewSession(observation.TerritoryID);
                engine.StartEncounterSession(observation.TerritoryID);
            }
            if (!captureComplete && observation.Context != null) observation.Context.Complete = false;
            engine.ProcessObservation(observation, replaying: true);
            ++report.Parsed;
            report.Counts[observation.Kind] = report.Counts.GetValueOrDefault(observation.Kind) + 1;
            if (observation.At < report.First) report.First = observation.At;
            if (observation.At > report.Last) report.Last = observation.At;
            if (observation.Kind != ObservationKind.GenericFeature && (observation.ContextID == 0 || engine._decisionContext?.ID != observation.ContextID || engine._decisionContext.Complete != true)) ++report.MissingContexts;
        }
        engine.FinalizeDue(DateTime.MaxValue, exhaustive: true);
        report.Territories = knowledge.Encounters.Count;
        report.RediscoveredMechanics = knowledge.Encounters.Values.Sum(e => e.Mechanics.Count);
        report.AmbiguousMechanics = knowledge.Encounters.Values.Sum(e => e.Mechanics.Values.Count(m => m.Kind == MechanicKind.Unknown));
        report.PreImpactAssessed = knowledge.PreImpact.Classes.Values.Sum(c => c.Assessed) - initialAssessed;
        report.PreImpactCorrect = knowledge.PreImpact.Classes.Values.Sum(c => c.Hits) - initialCorrect;
        report.DecisionDigest = Convert.ToHexString(digest.GetHashAndReset());
        report.Status = $"Detached {(learn ? "learn-then-test chronologically" : "frozen-knowledge evaluation")}: {report.Assessed} assessable, {report.Incorrect} contradicted, {report.MissingContexts} events without recorded world context";
        return new(report, knowledge);
    }
}
