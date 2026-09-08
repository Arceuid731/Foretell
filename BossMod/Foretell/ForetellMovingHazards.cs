using System.Numerics;

namespace BossMod.Foretell;

public sealed record MovingHazardState(long ID, ulong ActorID, uint SourceOID, uint SourceNameID, uint ActionID,
    int EventCount, DateTime LastImpact, Vector2 LastOrigin, Vector2 Velocity, float Radius, double IntervalSeconds, bool Ready)
{
    public ulong SourceID => ActorID;
    public float X => LastOrigin.X;
    public float Z => LastOrigin.Y;
    public float VelocityX => Velocity.X;
    public float VelocityZ => Velocity.Y;
    public string Signature => $"moving-impact:{ID}";
    public DateTime NextImpact => LastImpact.AddSeconds(IntervalSeconds);
    public DateTime ExpiresAt => LastImpact.AddSeconds(EventCount >= 2 ? Math.Min(2, IntervalSeconds + Math.Min(.1, IntervalSeconds * .2)) : 2);
}

public sealed class ForetellMovingHazards
{
    public const int MaximumTracks = 128;
    private readonly Dictionary<(ulong Actor, uint Action), Track> _tracks = [];
    private readonly HashSet<ulong> _actors = [];
    private DateTime _clock;
    private DateTime _nextExpiry = DateTime.MaxValue;
    private uint? _territory;
    private uint? _duty;
    private double? _gap;
    private long _nextID;

    private readonly record struct Metadata(uint OID, uint NameID, int Radius, int XAxis, bool TargetArea,
        uint OmenID, string Omen, uint VFXID, GeometryKind Geometry, MechanicKind Kind, float P1, float P2);

    private sealed class Track(long id, ForetellObservation observation, Metadata metadata)
    {
        public readonly long ID = id;
        public readonly ulong ActorID = observation.ActorID;
        public readonly uint ActionID = observation.PrimaryID;
        public readonly Metadata Identity = metadata;
        public DateTime LastImpact = observation.At;
        public Vector2 LastOrigin = new(observation.X, observation.Z);
        public Vector2 Velocity;
        public double Interval;
        public int Count = 1;
        public bool Ready => Count >= 3;
        public DateTime ExpiresAt => LastImpact.AddSeconds(Count >= 2 ? Math.Min(2, Interval + Math.Min(.1, Interval * .2)) : 2);
        public MovingHazardState State => new(ID, ActorID, Identity.OID, Identity.NameID, ActionID,
            Count, LastImpact, LastOrigin, Velocity, Identity.Radius, Interval, Ready);
    }

    public int TrackCount => _tracks.Count;
    public long Revision { get; private set; }

    public void Clear() => Reset();

    public void Reset()
    {
        ++Revision;
        _tracks.Clear();
        _actors.Clear();
        _nextExpiry = DateTime.MaxValue;
        _clock = default;
        _territory = null;
        _duty = null;
        _gap = null;
    }

    public void Observe(ForetellObservation observation) => Observe(observation, observation.SourceKind);

    public void Observe(ForetellObservation observation, SourceKind sourceNormalized)
    {
        if (observation.At < _clock)
        {
            var lifecycle = observation.Kind is ObservationKind.DutyStarted or ObservationKind.DutyWiped
                or ObservationKind.DutyRecommenced or ObservationKind.DutyCompleted;
            var pulse = observation.Kind == ObservationKind.ActionResolved && sourceNormalized == SourceKind.Enemy
                && observation.SourceKind is not (SourceKind.Player or SourceKind.Pet);
            var sourceState = _actors.Contains(observation.ActorID) && observation.Kind is
                ObservationKind.ActorRemoved or ObservationKind.ActorAdded or ObservationKind.DeathChanged
                or ObservationKind.TargetableChanged or ObservationKind.ActorSnapshot or ObservationKind.PositionSample or ObservationKind.Displacement;
            if (!lifecycle && !pulse && !sourceState) return;
        }
        Advance(observation.At);
        if (_territory is { } territory && territory != observation.TerritoryID) Reset();
        _territory = observation.TerritoryID;
        var duty = observation.Context is { } context ? (uint?)context.Duty
            : Integer(observation, "runtime.core.currentCFCID", out var numericDuty) ? numericDuty : null;
        if (duty is { } currentDuty)
        {
            if (_duty is { } previousDuty && previousDuty != currentDuty) ClearTracks();
            _duty = currentDuty;
        }
        double? gap = observation.Context?.OutcomeGap;
        if (Number(observation, "decision.outcomeGap", out var numericGap)) gap = numericGap;
        if (gap is { } currentGap)
        {
            if (_gap is { } previousGap && previousGap != currentGap) ClearTracks();
            _gap = currentGap;
        }
        _clock = observation.At;
        if (observation.Kind is ObservationKind.DutyStarted or ObservationKind.DutyWiped
            or ObservationKind.DutyRecommenced or ObservationKind.DutyCompleted)
        {
            ClearTracks();
            return;
        }
        if (observation.Kind is not (ObservationKind.ActionResolved or ObservationKind.ActorRemoved or ObservationKind.ActorAdded
            or ObservationKind.DeathChanged or ObservationKind.TargetableChanged or ObservationKind.ActorSnapshot
            or ObservationKind.PositionSample or ObservationKind.Displacement)) return;
        var trackedActor = _actors.Contains(observation.ActorID);
        if (!trackedActor && (observation.Kind != ObservationKind.ActionResolved || sourceNormalized != SourceKind.Enemy
            || observation.SourceKind is SourceKind.Player or SourceKind.Pet)) return;
        if (observation.Kind is ObservationKind.ActorRemoved or ObservationKind.ActorAdded
            || observation.Kind is ObservationKind.DeathChanged or ObservationKind.TargetableChanged && observation.Flag
            || Number(observation, "actor.isDead", out var dead) && dead != 0
            || Number(observation, "actor.isDestroyed", out var destroyed) && destroyed != 0
            || Number(observation, "actor.isTargetable", out var targetable) && targetable != 0)
        {
            RemoveActor(observation.ActorID);
            return;
        }
        if (trackedActor)
        {
            var hasName = Integer(observation, "actor.nameID", out var name);
            var identityChanged = false;
            foreach (var existingTrack in _tracks.Values)
                if (existingTrack.ActorID == observation.ActorID)
                {
                    var changed = observation.ActorOID != 0 && observation.ActorOID != existingTrack.Identity.OID
                        || hasName && name != existingTrack.Identity.NameID;
                    if (observation.Kind is ObservationKind.ActorSnapshot or ObservationKind.PositionSample or ObservationKind.Displacement)
                    {
                        var displacement = Vector2.Distance(existingTrack.LastOrigin, new(observation.X, observation.Z));
                        var maximumTravel = Math.Max(0, (observation.At - existingTrack.LastImpact).TotalSeconds) * 20 + .5;
                        changed |= !float.IsFinite(displacement) || displacement > maximumTravel;
                    }
                    if (changed)
                    {
                        identityChanged = true;
                        break;
                    }
                }
            if (identityChanged) RemoveActor(observation.ActorID);
        }
        if (observation.Kind != ObservationKind.ActionResolved) return;
        var key = (observation.ActorID, observation.PrimaryID);
        if (!Eligible(observation, sourceNormalized, out var metadata))
        {
            Remove(key);
            return;
        }
        var origin = new Vector2(observation.X, observation.Z);
        if (_tracks.TryGetValue(key, out var track) && track.Identity == metadata)
        {
            var interval = (observation.At - track.LastImpact).TotalSeconds;
            var velocity = interval > 0 ? (origin - track.LastOrigin) / (float)interval : Vector2.Zero;
            var speed = velocity.Length();
            var stable = interval is >= .2 and <= 2 && float.IsFinite(speed) && speed <= 20;
            if (speed < .1f) velocity = Vector2.Zero;
            if (track.Count >= 2)
                stable &= Math.Abs(interval - track.Interval) <= Math.Max(.03, track.Interval * .2)
                    && (speed < .1f) == (track.Velocity.Length() < .1f)
                    && Vector2.Distance(velocity, track.Velocity) <= Math.Max(.3f, track.Velocity.Length() * .2f);
            if (stable)
            {
                track.Interval = track.Count == 1 ? interval : (track.Interval + interval) * .5;
                track.Velocity = track.Count == 1 ? velocity : (track.Velocity + velocity) * .5f;
                track.LastImpact = observation.At;
                track.LastOrigin = origin;
                track.Count = Math.Min(1000000, track.Count + 1);
                if (track.ExpiresAt < _nextExpiry) _nextExpiry = track.ExpiresAt;
                ++Revision;
                return;
            }
        }
        if (!_tracks.ContainsKey(key) && _tracks.Count == MaximumTracks)
            Remove(_tracks.OrderBy(entry => entry.Value.LastImpact).ThenBy(entry => entry.Value.ID).First().Key);
        _tracks[key] = new(long.MinValue / 2 + ++_nextID, observation, metadata);
        _actors.Add(observation.ActorID);
        if (_tracks[key].ExpiresAt < _nextExpiry) _nextExpiry = _tracks[key].ExpiresAt;
        ++Revision;
    }

    public DecisionHazard[] Snapshot(DateTime at)
    {
        if (at < _clock) return [];
        Advance(at);
        List<DecisionHazard> hazards = [];
        foreach (var track in _tracks.Values.OrderBy(track => track.ID))
        {
            if (!track.Ready) continue;
            var activation = track.LastImpact.AddSeconds(track.Interval);
            var expiry = track.ExpiresAt;
            var motionAt = at < activation ? at : activation;
            var origin = track.LastOrigin + track.Velocity * (float)(motionAt - track.LastImpact).TotalSeconds;
            var target = track.LastOrigin + track.Velocity * (float)track.Interval;
            var prediction = new ActivePrediction(track.ActorID, track.ActionID, GeometryKind.Circle, MechanicKind.Unknown,
                origin, target, 0, track.Identity.Radius, 0, activation, .8f, "Repeated actor impacts",
                SignalKey: $"moving-impact:{track.ActionID}", Guidance: GuidanceKind.None, Anticipated: true)
            {
                CreatedAt = at, MotionUntil = activation, Velocity = track.Velocity,
                Binding = HazardBinding.Fixed, Provenance = "Repeated actor impacts"
            };
            hazards.Add(new(track.ID, prediction, expiry, true, true, prediction.Provenance));
        }
        return hazards.ToArray();
    }

    public MovingHazardState[] States(DateTime at)
    {
        if (at < _clock) return [];
        Advance(at);
        return _tracks.Values.OrderBy(track => track.ID).Select(track => track.State).ToArray();
    }

    private void Advance(DateTime at)
    {
        if (at < _clock) Reset();
        _clock = at;
        if (at <= _nextExpiry) return;
        _nextExpiry = DateTime.MaxValue;
        Span<(ulong Actor, uint Action)> expired = stackalloc (ulong, uint)[MaximumTracks];
        var count = 0;
        foreach (var entry in _tracks)
        {
            var expiry = entry.Value.ExpiresAt;
            if (at > expiry) expired[count++] = entry.Key;
            else if (expiry < _nextExpiry) _nextExpiry = expiry;
        }
        for (var index = 0; index < count; ++index) Remove(expired[index]);
    }

    private void RemoveActor(ulong actor)
    {
        if (!_actors.Contains(actor)) return;
        Span<(ulong Actor, uint Action)> removed = stackalloc (ulong, uint)[MaximumTracks];
        var count = 0;
        foreach (var key in _tracks.Keys)
            if (key.Actor == actor) removed[count++] = key;
        for (var index = 0; index < count; ++index) Remove(removed[index]);
    }

    private void Remove((ulong Actor, uint Action) key)
    {
        if (!_tracks.Remove(key)) return;
        ++Revision;
        foreach (var remaining in _tracks.Keys)
            if (remaining.Actor == key.Actor) return;
        _actors.Remove(key.Actor);
    }

    private void ClearTracks()
    {
        _tracks.Clear();
        _actors.Clear();
        _nextExpiry = DateTime.MaxValue;
        ++Revision;
    }

    private static bool Eligible(ForetellObservation observation, SourceKind source, out Metadata metadata)
    {
        metadata = default;
        if (source != SourceKind.Enemy || observation.SourceKind is SourceKind.Player or SourceKind.Pet
            || observation.At == default || observation.At > DateTime.MaxValue.AddSeconds(-2)
            || observation.ActorID == 0 || observation.ActorOID == 0 || observation.PrimaryID == 0
            || observation.TargetID != observation.ActorID || !float.IsFinite(observation.X) || !float.IsFinite(observation.Z)
            || !Number(observation, "actor.isTargetable", out var targetable) || targetable != 0
            || !Integer(observation, "actor.nameID", out var nameID)
            || observation.Prior is not { CastType: 2, EffectRange: > 0 and <= 20 } prior
            || prior.ActionID != observation.PrimaryID || prior.Geometry is not (GeometryKind.Unknown or GeometryKind.Circle)
            || prior.Kind is not (MechanicKind.Unknown or MechanicKind.GroundAOE)
            || prior.Omen is { Length: > 1024 }
            || prior.P1 != prior.EffectRange || prior.P2 != 0)
            return false;
        metadata = new(observation.ActorOID, nameID, prior.EffectRange, prior.XAxisModifier, prior.TargetArea,
            prior.OmenID, prior.Omen ?? "", prior.VFXID, prior.Geometry, prior.Kind, prior.P1, prior.P2);
        return true;
    }

    private static bool Number(ForetellObservation observation, string key, out double value)
    {
        value = 0;
        return observation.Numeric != null && observation.Numeric.TryGetValue(key, out value) && double.IsFinite(value);
    }

    private static bool Integer(ForetellObservation observation, string key, out uint value)
    {
        value = 0;
        if (!Number(observation, key, out var number) || number < 0 || number > uint.MaxValue || number != Math.Truncate(number)) return false;
        value = (uint)number;
        return true;
    }
}
