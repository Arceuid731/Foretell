using System.Diagnostics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private const int ArenaBoundaryRayCount = 96;
    private const float ArenaBoundaryRadius = 90f;
    private const int MaxArenaBoundaryRaysPerFrame = 4;
    private const double MaxArenaBoundaryMillisecondsPerFrame = .12;
    private readonly float[] _arenaBoundaryDistances = new float[ArenaBoundaryRayCount];
    private readonly bool[] _arenaBoundaryHits = new bool[ArenaBoundaryRayCount];
    private ArenaBoundaryAnalysis? _arenaBoundary;
    private Vector3 _arenaBoundaryOrigin;
    private int _arenaBoundaryCursor;
    private bool _arenaBoundarySweepRequested = true;
    private bool _arenaBoundarySweepInProgress;
    private DateTime _arenaBoundaryRescanAfter;
    private long _arenaBoundaryRays;
    private long _arenaBoundarySweeps;
    private long _arenaBoundaryChanges;
    private DateTime _arenaContextRefreshAfter;
    private DateTime _arenaEnemySummaryAt;
    private int _arenaEnemyCount;
    private uint _arenaEnemyMaximumHP;
    private uint _arenaPlayerMaximumHP;
    private bool _arenaHasBossCandidate;

    internal ArenaBoundaryAnalysis? CurrentArenaBoundary
    {
        get
        {
            var player = _ws.Party[PartyState.PlayerSlot];
            var inCombat = Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];
            if (!ForetellInferenceCore.ShouldUseFastArenaBoundary(inCombat)
                || _arenaBoundary is not { ArenaLike: true } boundary || player == null
                || Math.Abs(player.PosRot.Y - boundary.ReferenceY) > 6
                || !ForetellArenaBoundaryCore.Contains(boundary.Points, V(player.Position)))
                return null;
            return boundary;
        }
    }

    private void ResetArenaBoundary()
    {
        _arenaBoundary = null;
        _arenaBoundaryOrigin = default;
        _arenaBoundaryCursor = 0;
        _arenaBoundarySweepRequested = true;
        _arenaBoundarySweepInProgress = false;
        _arenaBoundaryRescanAfter = default;
        _arenaContextRefreshAfter = default;
        _arenaEnemySummaryAt = default;
        Array.Clear(_arenaBoundaryDistances);
        Array.Clear(_arenaBoundaryHits);
    }

    private void InvalidateArenaBoundary(bool immediate = false)
    {
        _arenaBoundarySweepRequested = true;
        _arenaBoundaryRescanAfter = immediate ? default : DateTime.UtcNow.AddMilliseconds(150);
    }

    // Returns true when this independent, tightly bounded accelerator consumed work in the current frame.
    // The authoritative reachable floor frontier may still advance under its own watchdog.
    private unsafe bool SampleNativeArenaBoundary(BGCollisionModule* collision, Vector3 player, DateTime now)
    {
        var inCombat = Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];
        if (!ForetellInferenceCore.ShouldUseFastArenaBoundary(inCombat))
        {
            // Courtyard walls, stairs and nearby buildings form plausible radial polygons while travelling. They
            // are not walkable topology and caused the cyan outline to appear late then vanish on height changes.
            _arenaBoundary = null;
            _arenaBoundarySweepInProgress = false;
            _arenaBoundaryCursor = 0;
            _arenaBoundarySweepRequested = true;
            return false;
        }
        if (_arenaBoundary == null)
            TryRestoreKnownArenaBoundary(player);
        if (_arenaBoundary is { } active && (Math.Abs(player.Y - active.ReferenceY) > 6
            || Vector2.Distance(new(player.X, player.Z), active.Origin) > ArenaBoundaryRadius * .75f))
            _arenaBoundary = null;

        if (!_arenaBoundarySweepInProgress && !_arenaBoundarySweepRequested && now >= _arenaBoundaryRescanAfter)
            _arenaBoundarySweepRequested = true;

        if (!_arenaBoundarySweepInProgress && (!_arenaBoundarySweepRequested || now < _arenaBoundaryRescanAfter))
            return false;

        if (!_arenaBoundarySweepInProgress)
        {
            _arenaBoundaryOrigin = player;
            _arenaBoundaryCursor = 0;
            Array.Fill(_arenaBoundaryDistances, ArenaBoundaryRadius);
            Array.Clear(_arenaBoundaryHits);
            _arenaBoundarySweepRequested = false;
            _arenaBoundarySweepInProgress = true;
        }
        if (Vector2.Distance(new(player.X, player.Z), new(_arenaBoundaryOrigin.X, _arenaBoundaryOrigin.Z)) > 2.5f
            || Math.Abs(player.Y - _arenaBoundaryOrigin.Y) > 2.5f)
        {
            _arenaBoundarySweepInProgress = false;
            _arenaBoundarySweepRequested = true;
            return false;
        }

        var started = Stopwatch.GetTimestamp();
        var sampled = 0;
        try
        {
            while (_arenaBoundaryCursor < ArenaBoundaryRayCount && sampled < MaxArenaBoundaryRaysPerFrame
                && Stopwatch.GetElapsedTime(started).TotalMilliseconds < MaxArenaBoundaryMillisecondsPerFrame)
            {
                var index = _arenaBoundaryCursor++;
                var angle = MathF.Tau * index / ArenaBoundaryRayCount;
                var direction = new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle));
                var origin = _arenaBoundaryOrigin + new Vector3(0, 1.1f, 0);
                if (BGCollisionModule.RaycastMaterialFilter(origin, direction, out var hit, ArenaBoundaryRadius))
                {
                    var distance = Vector2.Distance(new(origin.X, origin.Z), new(hit.Point.X, hit.Point.Z));
                    _arenaBoundaryDistances[index] = Math.Clamp(distance, 1, ArenaBoundaryRadius);
                    _arenaBoundaryHits[index] = distance < ArenaBoundaryRadius - .1f;
                }
                ++sampled;
                ++_arenaBoundaryRays;
            }
        }
        catch (Exception e)
        {
            _arenaBoundarySweepInProgress = false;
            _arenaBoundarySweepRequested = true;
            ++_topologyFailures;
            SuspendTopology(now, $"arena boundary probe rejected safely: {e.GetType().Name}");
            return false;
        }

        if (_arenaBoundaryCursor >= ArenaBoundaryRayCount)
        {
            _arenaBoundarySweepInProgress = false;
            _arenaBoundaryRescanAfter = now.AddSeconds(inCombat ? 2 : 6);
            CompleteArenaBoundarySweep();
        }
        return sampled != 0;
    }

    private void CompleteArenaBoundarySweep()
    {
        ++_arenaBoundarySweeps;
        var result = ForetellArenaBoundaryCore.Analyze(new(_arenaBoundaryOrigin.X, _arenaBoundaryOrigin.Z), _arenaBoundaryOrigin.Y,
            _arenaBoundaryDistances, _arenaBoundaryHits, ArenaBoundaryRadius);
        if (result.Points.Count == 0 || result.Hits < ArenaBoundaryRayCount / 3)
            return;
        if (_arenaBoundary?.Fingerprint != result.Fingerprint)
            ++_arenaBoundaryChanges;
        // Partial radial visibility polygons are still useful raw evidence, but they are not stable arena frames.
        // Keep them out of live rendering and persistent knowledge so the denser floor scan can take over.
        _arenaEnemySummaryAt = default;
        var accepted = result.ArenaLike && ArenaEnemySummary(result).HasBossCandidate;
        _arenaBoundary = accepted ? result : null;

        var encounter = Encounter(_territory);
        var now = DateTime.UtcNow;
        if (accepted && !encounter.ArenaBoundaries.TryGetValue(result.Fingerprint, out var memory))
        {
            memory = new()
            {
                Fingerprint = result.Fingerprint,
                OriginX = result.Origin.X,
                OriginZ = result.Origin.Y,
                ReferenceY = result.ReferenceY,
                Points = result.Points.Select(point => new TopologyPoint { X = point.X, Z = point.Y }).ToList(),
                Rays = result.Rays,
                Hits = result.Hits,
                Area = result.Area,
                Compactness = result.Compactness,
                AspectRatio = result.AspectRatio,
                ArenaLike = result.ArenaLike,
                FirstSeen = now
            };
            encounter.ArenaBoundaries[result.Fingerprint] = memory;
            while (encounter.ArenaBoundaries.Count > 16)
            {
                var oldest = encounter.ArenaBoundaries.Values.OrderBy(item => item.LastSeen).First();
                encounter.ArenaBoundaries.Remove(oldest.Fingerprint);
            }
        }
        if (accepted)
        {
            var acceptedMemory = encounter.ArenaBoundaries[result.Fingerprint];
            acceptedMemory.LastSeen = now;
            ++acceptedMemory.Observations;
        }

        var observation = Observation(ObservationKind.TopologySnapshot, detail: $"boundary:{result.Fingerprint}");
        observation.SourceKind = SourceKind.Environment;
        StoreNative(observation, "native.arenaBoundary.rays", result.Rays);
        StoreNative(observation, "native.arenaBoundary.hits", result.Hits);
        StoreNative(observation, "native.arenaBoundary.area", result.Area);
        StoreNative(observation, "native.arenaBoundary.compactness", result.Compactness);
        StoreNative(observation, "native.arenaBoundary.aspectRatio", result.AspectRatio);
        StoreNative(observation, "native.arenaBoundary.arenaLike", result.ArenaLike);
        StoreNative(observation, "native.arenaBoundary.accepted", accepted);
        ProcessObservation(observation, enriched: true);
    }

    private bool TryRestoreKnownArenaBoundary(Vector3 player)
    {
        if (!ForetellInferenceCore.ShouldUseFastArenaBoundary(Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat]))
            return false;
        if (!_store.Encounters.TryGetValue(_territory, out var encounter))
            return false;
        var position = new Vector2(player.X, player.Z);
        var memory = encounter.ArenaBoundaries.Values
            .Where(item => item.ArenaLike && Math.Abs(player.Y - item.ReferenceY) <= 6
                && ForetellArenaBoundaryCore.Contains(item.Points.Select(point => new Vector2(point.X, point.Z)).ToArray(), position))
            .OrderByDescending(item => item.LastSeen)
            .FirstOrDefault();
        if (memory == null)
            return false;
        var restored = new ArenaBoundaryAnalysis(memory.Fingerprint, new(memory.OriginX, memory.OriginZ), memory.ReferenceY,
            memory.Points.Select(point => new Vector2(point.X, point.Z)).ToList(), memory.Rays, memory.Hits,
            memory.Area, memory.Compactness, memory.AspectRatio, memory.ArenaLike);
        _arenaEnemySummaryAt = default;
        if (!ArenaEnemySummary(restored).HasBossCandidate)
            return false;
        _arenaBoundary = restored;
        return true;
    }

    private bool? IsArenaBoundaryPassable(Vector2 world)
        => CurrentArenaBoundary is { } boundary ? ForetellArenaBoundaryCore.Contains(boundary.Points, world) : null;

    private void RefreshLearnedArenaSourceContext()
    {
        if (!_cfg.EnableLearning || CurrentArenaBoundary is not { ArenaLike: true } boundary
            || !Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat])
            return;
        var now = _ws.CurrentTime;
        if (now < _arenaContextRefreshAfter)
            return;
        _arenaContextRefreshAfter = now.AddMilliseconds(250);
        var summary = ArenaEnemySummary(boundary);
        if (!summary.HasBossCandidate)
            return;
        var encounter = Encounter(_territory);
        foreach (var actor in _ws.Actors)
        {
            if (!LiveArenaEnemy(actor, boundary))
                continue;
            if (!encounter.Sources.TryGetValue(actor.OID, out var source))
                continue;
            source.MaximumHP = Math.Max(source.MaximumHP, actor.HPMP.MaxHP);
            source.MaximumHitboxRadius = Math.Max(source.MaximumHitboxRadius, actor.HitboxRadius);
            source.ArenaContextObservations = Math.Max(1, source.ArenaContextObservations);
            if (IsBossCandidate(actor, summary.MaximumHP, summary.PlayerMaximumHP))
                source.BossCandidateObservations = Math.Max(1, source.BossCandidateObservations);
        }
    }

    private void RecordLearnedArenaSourceContext(ForetellObservation observation, SourceMemory source)
    {
        if (source.Kind != SourceKind.Enemy)
            return;
        if (observation.Numeric.TryGetValue("actor.hp.maximum", out var hp) && hp is > 0 and <= uint.MaxValue)
            source.MaximumHP = Math.Max(source.MaximumHP, (uint)hp);
        if (observation.Numeric.TryGetValue("actor.hitboxRadius", out var radius) && float.IsFinite((float)radius))
            source.MaximumHitboxRadius = Math.Max(source.MaximumHitboxRadius, (float)radius);
        if (CurrentArenaBoundary is not { ArenaLike: true } boundary
            || !Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat])
            return;
        var actor = observation.ActorID != 0 ? _ws.Actors.Find(observation.ActorID) : null;
        if (actor == null || !LiveArenaEnemy(actor, boundary))
            return;
        var summary = ArenaEnemySummary(boundary);
        // A compact room alone is not evidence of a boss encounter. Only attach arena context after a credible
        // boss candidate has been observed; the remaining enemies then become that boss's adds.
        if (!summary.HasBossCandidate)
            return;
        ++source.ArenaContextObservations;
        if (IsBossCandidate(actor, summary.MaximumHP, summary.PlayerMaximumHP))
            ++source.BossCandidateObservations;
    }

    private (int Count, uint MaximumHP, uint PlayerMaximumHP, bool HasBossCandidate) ArenaEnemySummary(ArenaBoundaryAnalysis boundary)
    {
        var now = _ws.CurrentTime;
        if (_arenaEnemySummaryAt == now)
            return (_arenaEnemyCount, _arenaEnemyMaximumHP, _arenaPlayerMaximumHP, _arenaHasBossCandidate);

        _arenaEnemySummaryAt = now;
        _arenaEnemyCount = 0;
        _arenaEnemyMaximumHP = 0;
        _arenaPlayerMaximumHP = _ws.Party[PartyState.PlayerSlot]?.HPMP.MaxHP ?? 0;
        foreach (var actor in _ws.Actors)
        {
            if (!LiveArenaEnemy(actor, boundary))
                continue;
            ++_arenaEnemyCount;
            _arenaEnemyMaximumHP = Math.Max(_arenaEnemyMaximumHP, actor.HPMP.MaxHP);
        }
        _arenaHasBossCandidate = false;
        foreach (var actor in _ws.Actors)
        {
            if (LiveArenaEnemy(actor, boundary) && IsBossCandidate(actor, _arenaEnemyMaximumHP, _arenaPlayerMaximumHP))
            {
                _arenaHasBossCandidate = true;
                break;
            }
        }
        return (_arenaEnemyCount, _arenaEnemyMaximumHP, _arenaPlayerMaximumHP, _arenaHasBossCandidate);
    }

    private static bool LiveArenaEnemy(Actor actor, ArenaBoundaryAnalysis boundary)
        => actor.Type == ActorType.Enemy && !actor.IsAlly && !actor.IsDeadOrDestroyed && actor.IsTargetable
            && ForetellArenaBoundaryCore.Contains(boundary.Points, V(actor.Position));

    private static bool IsBossCandidate(Actor actor, uint maximumHP, uint playerMaximumHP)
        => ForetellArenaBoundaryCore.IsBossCandidate(actor.HPMP.MaxHP, maximumHP, playerMaximumHP, actor.HitboxRadius);
}
