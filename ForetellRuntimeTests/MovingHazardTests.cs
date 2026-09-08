using System.Numerics;
using System.Text.Json;
using BossMod.Foretell;

internal static class MovingHazardTests
{
    private static readonly DateTime Start = new(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc);

    public static void Run()
    {
        MovingAndStationary();
        IndependentSources();
        Eligibility();
        UnstableSequences();
        LifecycleAndIdentity();
        BoundsAndRevision();
        Console.WriteLine("Moving hazards: pulse learning, interpolation, source isolation, metadata gates, resets, expiry and bounds passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static ForetellObservation Pulse(double seconds, float position, ulong actor = 100, uint action = 200, float lane = 0)
        => new()
        {
            At = Start.AddSeconds(seconds), TerritoryID = 1, Kind = ObservationKind.ActionResolved,
            SourceKind = SourceKind.Enemy, ActorID = actor, ActorOID = 300, TargetID = actor,
            PrimaryID = action, X = position, Z = lane,
            Numeric = new() { ["actor.nameID"] = 400, ["actor.isTargetable"] = 0, ["actor.isDead"] = 0 },
            Prior = new(action, GeometryKind.Unknown, MechanicKind.Unknown, 3, 0, .72f, 2, 3, 0, false, 0, "", 0, "Fixture metadata")
        };

    private static void Feed(ForetellMovingHazards tracker, ForetellObservation observation)
        => tracker.Observe(observation, SourceKind.Enemy);

    private static ForetellMovingHazards Learned()
    {
        var tracker = new ForetellMovingHazards();
        for (var index = 0; index < 3; ++index) Feed(tracker, Pulse(index * .6, index * 1.73f));
        return tracker;
    }

    private static void MovingAndStationary()
    {
        var tracker = new ForetellMovingHazards();
        Feed(tracker, Pulse(0, 0));
        Feed(tracker, Pulse(.6, 1.73f));
        Check(tracker.Snapshot(Start.AddSeconds(.6)).Length == 0, "Two impacts must not forecast.");
        Feed(tracker, Pulse(1.2, 3.46f));
        var hazard = tracker.Snapshot(Start.AddSeconds(1.5)).Single();
        var prediction = hazard.Prediction;
        Check(Vector2.Distance(prediction.Origin, new(4.325f, 0)) < .001f
            && Vector2.Distance(prediction.Target, new(5.19f, 0)) < .001f, "Snapshot must interpolate the origin and project just one next impact.");
        Check(Math.Abs((prediction.Activation - Start.AddSeconds(1.8)).Ticks) <= 2
            && Math.Abs((hazard.ActiveUntil - Start.AddSeconds(1.9)).Ticks) <= 2,
            "Forecast lifetime exceeded its bounded pulse grace period.");
        Check(hazard.AdvisoryOnly && hazard.SpatiallyKnown && prediction.Guidance == GuidanceKind.None
            && prediction.Geometry == GeometryKind.Circle && prediction.P1 == 3 && prediction.Confidence <= .8f,
            "Advisory metadata was promoted to tactical guidance.");
        Check(ForetellDecisionCore.Valid(prediction), "Forecast contains invalid geometry or motion.");
        var state = tracker.States(Start.AddSeconds(1.5)).Single();
        Check(state.Ready && state.EventCount == 3 && state.SourceID == 100 && state.SourceOID == 300
            && state.SourceNameID == 400 && state.ActionID == 200 && state.NextImpact == prediction.Activation,
            "State lost source identity or pulse evidence.");
        using (var serialized = JsonDocument.Parse(JsonSerializer.Serialize(state)))
        {
            var root = serialized.RootElement;
            Check(root.GetProperty("X").GetSingle() == state.LastOrigin.X && root.GetProperty("Z").GetSingle() == state.LastOrigin.Y
                && root.GetProperty("VelocityX").GetSingle() == state.Velocity.X
                && root.GetProperty("VelocityZ").GetSingle() == state.Velocity.Y,
                "Default JSON serialization lost scalar motion evidence.");
        }
        var grace = tracker.Snapshot(Start.AddSeconds(1.85)).Single();
        Check(Vector2.Distance(grace.Prediction.Origin, prediction.Target) < .001f,
            "Grace period either removed the circle or extrapolated beyond the next centre.");
        Check(tracker.Snapshot(Start.AddSeconds(1.91)).Length == 0, "Stopped source kept forecasting beyond its short pulse grace period.");
        Check(tracker.States(Start.AddSeconds(2)).Length == 0, "Stopped source was retained beyond its short grace period.");
        tracker.Clear();
        for (var index = 0; index < 5; ++index) Feed(tracker, Pulse(index * .6, 4));
        var stationary = tracker.Snapshot(Start.AddSeconds(2.4)).Single();
        Check(stationary.Prediction.Velocity == Vector2.Zero && stationary.Prediction.Origin == new Vector2(4, 0)
            && stationary.AdvisoryOnly && stationary.Prediction.Guidance == GuidanceKind.None,
            "Stationary repeated circles were lost or promoted to tactical guidance.");
        Feed(tracker, Pulse(3, 5.73f));
        Check(tracker.Snapshot(Start.AddSeconds(3)).Length == 0
            && tracker.States(Start.AddSeconds(3)).Single().EventCount == 1, "Stationary-to-moving transition did not restart learning.");
        Feed(tracker, Pulse(3.6, 7.46f));
        Feed(tracker, Pulse(4.2, 9.19f));
        Check(tracker.Snapshot(Start.AddSeconds(4.2)).Single().Prediction.Velocity.X > 2,
            "Movement did not recover after three new pulses.");
        Feed(tracker, Pulse(4.8, 9.19f));
        Check(tracker.Snapshot(Start.AddSeconds(4.8)).Length == 0, "Moving-to-stationary transition did not restart learning.");
        Feed(tracker, Pulse(5.4, 9.19f));
        Feed(tracker, Pulse(6, 9.19f));
        Check(tracker.Snapshot(Start.AddSeconds(6)).Single().Prediction.Velocity == Vector2.Zero,
            "Stationary forecast did not recover after three new pulses.");
    }

    private static void IndependentSources()
    {
        var tracker = new ForetellMovingHazards();
        for (var index = 0; index < 3; ++index)
        {
            Feed(tracker, Pulse(index * .6, index * 1.73f, actor: 100, lane: -5));
            Feed(tracker, Pulse(index * .6, -index * 1.73f, actor: 101, lane: 5));
            Feed(tracker, Pulse(index * .6, index * 2, actor: 100, action: 201, lane: -5));
        }
        var hazards = tracker.Snapshot(Start.AddSeconds(1.3));
        Check(hazards.Length == 3 && hazards.Select(hazard => hazard.ID).Distinct().Count() == 3,
            "Shared action IDs, sources or lanes were merged.");
        Check(hazards.Single(hazard => hazard.Prediction.CasterID == 101).Prediction.Velocity.X < 0,
            "Opposing lanes contaminated velocity.");
        var removed = Pulse(1.4, 0);
        removed.Kind = ObservationKind.ActorRemoved;
        Feed(tracker, removed);
        Check(tracker.Snapshot(Start.AddSeconds(1.4)).Single().Prediction.CasterID == 101,
            "Despawn failed to clear all actions belonging to only that source.");
    }

    private static void Eligibility()
    {
        Action<ForetellObservation>[] invalid =
        [
            observation => observation.Numeric.Clear(),
            observation => observation.Numeric = null!,
            observation => observation.Numeric.Remove("actor.isTargetable"),
            observation => observation.Numeric["actor.isTargetable"] = 1,
            observation => observation.Numeric["actor.isTargetable"] = double.NaN,
            observation => observation.Numeric.Remove("actor.nameID"),
            observation => observation.Numeric["actor.nameID"] = 3.5,
            observation => observation.Numeric["actor.isDead"] = 1,
            observation => observation.ActorID = 0,
            observation => observation.ActorOID = 0,
            observation => observation.TargetID = 999,
            observation => observation.SourceKind = SourceKind.Player,
            observation => observation.SourceKind = SourceKind.Pet,
            observation => observation.Kind = ObservationKind.AffectedTarget,
            observation => observation.X = float.NaN,
            observation => observation.Z = float.PositiveInfinity,
            observation => observation.Prior = null,
            observation => observation.Prior = observation.Prior!.Value with { ActionID = 999 },
            observation => observation.Prior = observation.Prior!.Value with { CastType = 5 },
            observation => observation.Prior = observation.Prior!.Value with { EffectRange = 0, P1 = 0 },
            observation => observation.Prior = observation.Prior!.Value with { EffectRange = 21, P1 = 21 },
            observation => observation.Prior = observation.Prior!.Value with { P1 = 0 },
            observation => observation.Prior = observation.Prior!.Value with { P1 = float.NaN },
            observation => observation.Prior = observation.Prior!.Value with { Kind = MechanicKind.Knockback },
            observation => observation.Prior = observation.Prior!.Value with { Geometry = GeometryKind.Donut }
        ];
        foreach (var mutate in invalid)
        {
            var tracker = new ForetellMovingHazards();
            for (var index = 0; index < 4; ++index)
            {
                var observation = Pulse(index * .6, index * 1.73f);
                mutate(observation);
                Feed(tracker, observation);
            }
            Check(tracker.Snapshot(Start.AddSeconds(1.8)).Length == 0, "Invalid input produced a forecast.");
        }
        foreach (var source in new[] { SourceKind.Unknown, SourceKind.Player, SourceKind.Pet, SourceKind.EventObject })
        {
            var tracker = new ForetellMovingHazards();
            for (var index = 0; index < 3; ++index) tracker.Observe(Pulse(index * .6, index * 1.73f), source);
            Check(tracker.TrackCount == 0, "Normalized non-enemy source was accepted.");
        }
    }

    private static void UnstableSequences()
    {
        foreach (var positions in new float[][] { [0, 2, 0], [0, 2, 20], [0, 13, 26], [0, 2, 2] })
        {
            var tracker = new ForetellMovingHazards();
            for (var index = 0; index < positions.Length; ++index) Feed(tracker, Pulse(index * .6, positions[index]));
            Check(tracker.Snapshot(Start.AddSeconds(1.2)).Length == 0, "Reversal, acceleration, teleport or stop was extrapolated.");
        }
        foreach (var seconds in new double[][] { [0, .1, .2], [0, .6, 1.6], [0, 2.1, 4.2], [0, .6, .6] })
        {
            var tracker = new ForetellMovingHazards();
            foreach (var second in seconds) Feed(tracker, Pulse(second, (float)second * 3));
            Check(tracker.Snapshot(Start.AddSeconds(seconds[^1])).Length == 0, "Unstable or duplicated timings were accepted.");
        }
        foreach (var interval in new[] { .2, 2.0 })
        {
            var tracker = new ForetellMovingHazards();
            for (var index = 0; index < 3; ++index) Feed(tracker, Pulse(index * interval, (float)(index * interval * 2)));
            var hazard = tracker.Snapshot(Start.AddSeconds(2 * interval)).Single();
            Check((hazard.Prediction.Activation - hazard.Prediction.CreatedAt).TotalSeconds <= 2,
                "Boundary interval exceeded forecast horizon.");
        }
        var jitter = new ForetellMovingHazards();
        var at = 0d;
        var position = 0f;
        Feed(jitter, Pulse(at, position));
        foreach (var interval in new[] { .55, .63, .58, .61, .56, .62 })
        {
            at += interval;
            position += 1.73f;
            Feed(jitter, Pulse(at, position));
        }
        Check(jitter.Snapshot(Start.AddSeconds(at)).Length == 1, "Ordinary packet interval jitter prevented learning.");
    }

    private static void LifecycleAndIdentity()
    {
        Action<ForetellObservation>[] changes =
        [
            observation => observation.TerritoryID = 2,
            observation => observation.ActorOID = 301,
            observation => observation.Numeric["actor.nameID"] = 401,
            observation => observation.Prior = observation.Prior!.Value with { EffectRange = 4, P1 = 4 },
            observation => observation.Prior = observation.Prior!.Value with { VFXID = 5 },
            observation => { observation.Kind = ObservationKind.DeathChanged; observation.Flag = true; },
            observation => { observation.Kind = ObservationKind.TargetableChanged; observation.Flag = true; },
            observation => observation.Kind = ObservationKind.ActorRemoved,
            observation => observation.Kind = ObservationKind.ActorAdded,
            observation => observation.Kind = ObservationKind.DutyWiped,
            observation => observation.Kind = ObservationKind.DutyStarted,
            observation => observation.Kind = ObservationKind.DutyRecommenced,
            observation => observation.Kind = ObservationKind.DutyCompleted
        ];
        foreach (var change in changes)
        {
            var tracker = Learned();
            var observation = Pulse(1.8, 5.19f);
            change(observation);
            Feed(tracker, observation);
            Check(tracker.Snapshot(observation.At).Length == 0, "Lifecycle or identity change retained learned motion.");
        }
        var teleported = Learned();
        var displacement = Pulse(1.3, 100);
        displacement.Kind = ObservationKind.Displacement;
        Feed(teleported, displacement);
        Check(teleported.TrackCount == 0, "Observed teleport retained its old forecast until another impact.");
        var stale = Learned();
        Feed(stale, Pulse(4, 6));
        Check(stale.States(Start.AddSeconds(4)).Single().EventCount == 1, "Long gap did not restart learning.");
        var backwards = Learned();
        var previousRevision = backwards.Revision;
        Check(backwards.Snapshot(Start.AddSeconds(.5)).Length == 0 && backwards.TrackCount == 1
            && backwards.Revision == previousRevision, "Historical query exposed future evidence or destroyed current learning.");
        backwards = Learned();
        Feed(backwards, Pulse(.5, 1));
        Check(backwards.States(Start.AddSeconds(.5)).Single().EventCount == 1, "Backwards observation reused future evidence.");
        foreach (var contextChange in new[] { true, false })
        {
            var tracker = new ForetellMovingHazards();
            for (var index = 0; index < 4; ++index)
            {
                var observation = Pulse(index * .6, index * 1.73f);
                observation.Context = new() { Duty = (ushort)(contextChange && index == 3 ? 2 : 1), OutcomeGap = !contextChange && index == 3 ? 1 : 0 };
                Feed(tracker, observation);
            }
            Check(tracker.States(Start.AddSeconds(1.8)).Single().EventCount == 1, "Duty/gap context did not invalidate tracks.");
        }
    }

    private static void BoundsAndRevision()
    {
        var tracker = new ForetellMovingHazards();
        for (ulong actor = 1; actor <= 150; ++actor) Feed(tracker, Pulse(0, 0, actor));
        Check(tracker.TrackCount == ForetellMovingHazards.MaximumTracks, "Track storage was not bounded.");
        Check(tracker.States(Start).All(state => state.SourceID > 22), "Capacity eviction was not deterministic.");
        tracker = Learned();
        var revision = tracker.Revision;
        var state = tracker.States(Start.AddSeconds(1.2)).Single();
        Check(state.ID < 0 && state.ID > long.MinValue / 2 && state.ID < long.MinValue / 4,
            "Moving hazard ID collided with positive episodes or long.MinValue annotations.");
        tracker.Snapshot(Start.AddSeconds(1.3));
        Check(tracker.Revision == revision, "Read-only interpolation changed capture revision.");
        Feed(tracker, Pulse(1.8, 5.19f));
        var next = tracker.States(Start.AddSeconds(1.8)).Single();
        Check(tracker.Revision > revision && state.ID == next.ID && state.Signature == next.Signature,
            "Pulse update lost retained identity or revision.");
        revision = tracker.Revision;
        tracker.Snapshot(Start.AddSeconds(3));
        Check(tracker.Revision > revision && tracker.TrackCount == 0, "Expiry failed to update revision.");
        revision = tracker.Revision;
        tracker.Clear();
        Check(tracker.Revision > revision && tracker.TrackCount == 0, "Explicit clear failed to update revision.");
        tracker = Learned();
        revision = tracker.Revision;
        var unrelated = Pulse(1.3, 0, actor: 999);
        unrelated.SourceKind = SourceKind.Player;
        tracker.Observe(unrelated);
        unrelated.Kind = ObservationKind.ServerIPC;
        tracker.Observe(unrelated);
        Check(tracker.Revision == revision && tracker.TrackCount == 1, "Unrelated traffic changed retained motion state.");
        unrelated.Numeric["decision.outcomeGap"] = 1;
        tracker.Observe(unrelated);
        unrelated.Numeric["decision.outcomeGap"] = 2;
        tracker.Observe(unrelated);
        Check(tracker.TrackCount == 0, "Fast path skipped gap invalidation on unrelated traffic.");
        tracker = new ForetellMovingHazards();
        for (var index = 0; index < 3; ++index)
        {
            Feed(tracker, Pulse(index * .6, index * 1.73f));
            revision = tracker.Revision;
            foreach (var kind in new[] { ObservationKind.NativeVFXSpawn, ObservationKind.NativeVFXDestroy, ObservationKind.GenericFeature })
            {
                var late = Pulse(index * .6 - 2, 0);
                late.Kind = kind;
                late.TerritoryID = 99;
                late.Context = new() { Duty = 99, OutcomeGap = 99 };
                tracker.Observe(late);
                Check(tracker.Snapshot(late.At).Length == 0 && tracker.States(late.At).Length == 0
                    && tracker.Revision == revision, "Delayed unrelated hook reset state or exposed a future forecast.");
            }
        }
        Check(tracker.Snapshot(Start.AddSeconds(1.2)).Length == 1,
            "Interleaved delayed native hooks prevented three-pulse learning.");
    }
}
