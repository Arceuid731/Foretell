using System.Diagnostics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private const float MinimumTopologyRadius = 64f;
    private const float MinimumTopologyResolution = 1.25f;
    private const float MaximumTopologyResolution = 4f;
    private const float MaximumTopologyStepHeight = 1.5f;
    private const float TopologyWallProbeHeight = .75f;
    private const int TargetTopologyHalfCells = 40;
    private const int MaxTopologyRaysPerFrame = 64;
    private const double TopologyBurstMillisecondsPerFrame = .80;
    private const double TopologySteadyMillisecondsPerFrame = .30;
    private readonly ForetellTopologyGrid _topology = new();
    private readonly ForetellTopologyFrontier _topologyFrontier = new();
    private sealed record TopologyAnalysisWork(long Generation, ForetellTopologyGrid Grid, Vector2 Player, bool Complete, TopologyAnalysis Analysis);
    private sealed record CollisionRasterWork(long Generation, ForetellCollisionRasterResult Result);
    private Task<TopologyAnalysisWork>? _topologyAnalysisTask;
    private Task<CollisionRasterWork>? _collisionRasterTask;
    private long _topologyGeneration;
    private TopologyAnalysis? _topologyAnalysis;
    private string _topologyFingerprint = "";
    private DateTime _topologySuspendedUntil;
    private long _topologyRays;
    private long _topologySweeps;
    private long _topologyChanges;
    private long _topologyFailures;
    private long _topologyOverruns;
    private double _lastTopologyMilliseconds;
    private double _peakTopologyMilliseconds;
    private int _topologyConsecutiveOverruns;
    private long _topologyInvalidations;
    private bool _topologySweepRequested = true;
    private bool _topologySweepInProgress;
    private bool _topologyAnalysisComplete;
    private bool _topologyHardInvalidation = true;
    private bool _topologyPublishProgress;
    private DateTime _topologyRescanAfter;
    private DateTime _topologyLastAnalysisAt;
    private Vector2 _topologyAnalysisPlayer;
    private bool _topologyAnalysisQueued;
    private bool _topologyAnalysisQueuedComplete;
    private float _topologySampleRadius;
    private DateTime _topologySweepStartedAt;
    private DateTime _topologyBurstUntil;
    private double _topologyLastSweepMilliseconds;
    private double _topologyPeakSweepMilliseconds;
    private double _topologyFirstSurfaceMilliseconds;
    private int _topologyFloorSamples;
    private int _topologyEdgeSamples;
    private bool _topologyCombatStateKnown;
    private bool _topologyLastCombatState;
    private bool _topologyMeshPrimary;
    private bool _topologyRescanAfterRaster;
    private DateTime _topologyMeshRetryAfter;
    private long _topologyMeshCaptures;
    private long _topologyMeshFallbacks;
    private int _topologyMeshTriangles;
    private int _topologyMeshColliders;
    private int _topologyMeshFloorTriangles;
    private int _topologyMeshWallTriangles;
    private int _topologyMeshCandidateSamples;
    private double _topologyMeshCaptureMilliseconds;
    private double _topologyMeshRasterMilliseconds;
    private double _topologyMeshPeakCaptureMilliseconds;
    private double _topologyMeshPeakRasterMilliseconds;

    internal TopologyAnalysis? CurrentTopology => _topologyAnalysis;
    internal bool TopologySuspended => DateTime.UtcNow < _topologySuspendedUntil;

    private void ResetTopology()
    {
        ++_topologyGeneration;
        _topologyAnalysis = null;
        _topologyFingerprint = "";
        _topology.Clear();
        _topologyConsecutiveOverruns = 0;
        _topologySuspendedUntil = default;
        _topologySweepRequested = true;
        _topologySweepInProgress = false;
        _topologyAnalysisComplete = false;
        _topologyHardInvalidation = true;
        _topologyPublishProgress = false;
        _topologyRescanAfter = default;
        _topologyLastAnalysisAt = default;
        _topologyAnalysisQueued = false;
        _topologyAnalysisQueuedComplete = false;
        _topologySampleRadius = 0;
        _topologySweepStartedAt = default;
        _topologyBurstUntil = default;
        _topologyLastSweepMilliseconds = 0;
        _topologyPeakSweepMilliseconds = 0;
        _topologyFirstSurfaceMilliseconds = 0;
        _topologyCombatStateKnown = false;
        _topologyLastCombatState = false;
        _topologyMeshPrimary = false;
        _topologyRescanAfterRaster = false;
        _topologyMeshRetryAfter = default;
        _topologyMeshTriangles = 0;
        _topologyMeshColliders = 0;
        _topologyMeshFloorTriangles = 0;
        _topologyMeshWallTriangles = 0;
        _topologyMeshCandidateSamples = 0;
        _topologyMeshCaptureMilliseconds = 0;
        _topologyMeshRasterMilliseconds = 0;
        _topologyFrontier.Clear();
        ResetArenaBoundary();
    }

    private void InvalidateTopology(bool immediate = false, bool hard = true)
    {
        ++_topologyInvalidations;
        _topologySweepRequested = true;
        _topologyHardInvalidation |= hard;
        if (hard)
        {
            if (_collisionRasterTask != null)
                _topologyRescanAfterRaster = true;
            else
                ++_topologyGeneration;
            _topologySweepInProgress = false;
            _topologyAnalysisComplete = false;
            _topologyAnalysisQueued = false;
            _topologyAnalysisQueuedComplete = false;
            _topologyFrontier.Clear();
        }
        _topologyRescanAfter = immediate ? default : DateTime.UtcNow.AddMilliseconds(150);
        InvalidateArenaBoundary(immediate);
    }

    // Primary path: copy the already loaded local PCB triangles on the framework thread, then rasterize their
    // layered heightfield on a worker. The bounded ray frontier remains a compatibility fallback for scenes whose
    // PCB data is unavailable. In both cases the grid is local and resolution-adaptive, never a full-map reveal.
    private unsafe void SampleNativeTopology()
    {
        var now = DateTime.UtcNow;
        PollCompletedCollisionRaster();
        PollCompletedTopologyAnalysis();
        if (now < _topologySuspendedUntil)
            return;
        var player = _ws.Party[PartyState.PlayerSlot];
        if (player == null)
            return;
        var inCombat = Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];
        if (!_topologyCombatStateKnown)
        {
            _topologyCombatStateKnown = true;
            _topologyLastCombatState = inCombat;
        }
        else if (_topologyLastCombatState != inCombat)
        {
            _topologyLastCombatState = inCombat;
            InvalidateTopology(immediate: true);
        }

        var framework = FFXIVFramework.Instance();
        var module = framework != null ? framework->BGCollisionModule : null;
        if (module == null || module->ShuttingDown || module->SceneManager == null || module->LoadInProgressCounter > 0)
        {
            RegisterCapability("native.topology.collision", typeof(BGCollisionModule), "BGCollisionModule", false, false, "collision scene unavailable or streaming");
            return;
        }

        var player3 = new Vector3(player.Position.X, player.PosRot.Y, player.Position.Z);
        if (!float.IsFinite(player3.X) || !float.IsFinite(player3.Y) || !float.IsFinite(player3.Z))
        {
            RegisterCapability("native.topology.collision", typeof(BGCollisionModule), "RaycastMaterialFilter", false, false, "non-finite player position rejected before native call");
            return;
        }
        if (!_topologySweepInProgress && !_topologySweepRequested && now >= _topologyRescanAfter)
            _topologySweepRequested = true;
        if (_topology.CellCount == 0)
            TryRestoreKnownTopology(player3);
        var desiredRadius = DesiredTopologyRadius();
        var desiredResolution = DesiredTopologyResolution(desiredRadius);
        var needsReset = _topology.CellCount == 0
            || !_topology.Contains(new(player3.X, player3.Z))
            || Math.Abs(player3.Y - _topology.ReferenceY) > 6
            || DistanceToTopologyCenter(new(player3.X, player3.Z)) > TopologyRecenterDistance(desiredRadius)
            || Math.Abs(_topologySampleRadius - desiredRadius) > desiredResolution * 1.5f
            || Math.Abs(_topology.Resolution - desiredResolution) > .05f;
        if (needsReset)
        {
            ResetLocalTopologyGrid(player3, desiredRadius, desiredResolution);
            _topologyAnalysis = null;
            _topologyAnalysisComplete = false;
            _topologyFingerprint = "";
            _topologySweepRequested = true;
            _topologyHardInvalidation = true;
            _topologySweepInProgress = false;
            _topologyMeshPrimary = false;
        }
        // The combat-only radial accelerator is an independent live overlay for pull barriers and dynamic walls.
        SampleNativeArenaBoundary(module, player3, now);

        if (_collisionRasterTask == null && !_topologySweepInProgress && _topologySweepRequested
            && now >= _topologyRescanAfter && now >= _topologyMeshRetryAfter)
        {
            if (TryStartCollisionRaster(module, player3, desiredRadius, desiredResolution, now))
                return;
            ++_topologyMeshFallbacks;
            _topologyMeshRetryAfter = now.AddSeconds(15);
            if (_topologyMeshPrimary && _topologyAnalysis is { PassableCells: > 0 })
            {
                // A transient streaming/capture miss must not replace a useful mesh with a growing one-cell scan.
                _topologySweepRequested = false;
                _topologyRescanAfter = _topologyMeshRetryAfter;
                return;
            }
        }
        if (_collisionRasterTask != null)
            return;
        if (!_topologySweepInProgress && _topologySweepRequested && now >= _topologyRescanAfter)
            StartTopologySweep(new(player3.X, player3.Z));
        if (!_topologySweepInProgress)
            return;

        var started = Stopwatch.GetTimestamp();
        var frameBudget = now < _topologyBurstUntil ? TopologyBurstMillisecondsPerFrame : TopologySteadyMillisecondsPerFrame;
        var sampled = 0;
        try
        {
            while (sampled < MaxTopologyRaysPerFrame && Stopwatch.GetElapsedTime(started).TotalMilliseconds < frameBudget
                && _topologyFrontier.TryDequeue(_topology, out var probe))
            {
                if (probe.Kind == TopologyProbeKind.Edge)
                {
                    var heightBlocked = Math.Abs(_topology.Heights[probe.From] - _topology.Heights[probe.To]) > MaximumTopologyStepHeight;
                    var blocked = heightBlocked || ProbeTopologyEdge(probe.From, probe.To);
                    _topologyFrontier.CommitEdge(_topology, probe.From, probe.To, blocked);
                    if (heightBlocked)
                        continue;
                    ++sampled;
                    ++_topologyRays;
                    ++_topologyEdgeSamples;
                    continue;
                }

                var point = _topology.CellCenter(probe.To);
                // Follow the already reached surface rather than the actor's initial elevation. This lets the
                // survey climb ordinary stairs and ramps before the actor has traversed them, while the step and
                // edge checks still prevent it from leaking onto unrelated stacked floors.
                var referenceY = ForetellTopologyProbeRules.FloorReferenceY(
                    probe.From >= 0 ? _topology.Heights[probe.From] : float.NaN, player3.Y);
                var origin = new Vector3(point.X, referenceY + 3.5f, point.Y);
                if (!BGCollisionModule.RaycastMaterialFilter(origin, -Vector3.UnitY, out var hit, 12f))
                {
                    _topology.Set(probe.To, TopologyCell.Void);
                }
                else
                {
                    var floorLike = ForetellTopologyProbeRules.IsFloorHit(hit.Normal.Y, hit.Point.Y, referenceY);
                    _topology.Set(probe.To, floorLike ? TopologyCell.Passable : TopologyCell.Blocked, hit.Point.Y);
                }
                _topologyFrontier.CommitFloor(_topology, probe.To);
                _topology.Cursor = _topologyFrontier.Sampled;
                ++sampled;
                ++_topologyRays;
                ++_topologyFloorSamples;
            }
        }
        catch (Exception e)
        {
            ++_topologyFailures;
            SuspendTopology(now, $"collision probe rejected safely: {e.GetType().Name}");
            return;
        }

        var player2 = new Vector2(player3.X, player3.Z);
        if (_topologySweepInProgress && _topologyFrontier.Complete)
        {
            _topologySweepInProgress = false;
            ++_topologySweeps;
            _topologyLastSweepMilliseconds = Math.Max(0, (now - _topologySweepStartedAt).TotalMilliseconds);
            _topologyPeakSweepMilliseconds = Math.Max(_topologyPeakSweepMilliseconds, _topologyLastSweepMilliseconds);
            _topologyRescanAfter = now.AddSeconds(inCombat ? 6 : 12);
            RequestTopologyAnalysis(player2, complete: true);
        }
        else if (_topologyPublishProgress && sampled != 0 && (now - _topologyLastAnalysisAt).TotalMilliseconds >= 75)
        {
            RequestTopologyAnalysis(player2, complete: false);
        }

        _lastTopologyMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _peakTopologyMilliseconds = Math.Max(_peakTopologyMilliseconds, _lastTopologyMilliseconds);
        if (_lastTopologyMilliseconds > 1.5)
        {
            ++_topologyOverruns;
            if (++_topologyConsecutiveOverruns >= 3)
                SuspendTopology(now, $"watchdog suspended probes after {_lastTopologyMilliseconds:F2} ms frame cost");
        }
        else
        {
            _topologyConsecutiveOverruns = 0;
            RegisterCapability("native.topology.collision", typeof(BGCollisionModule), "RaycastMaterialFilter", true, false, "bounded reachable-frontier floor and barrier probes");
        }
    }

    private unsafe bool TryStartCollisionRaster(BGCollisionModule* module, Vector3 player, float radius, float resolution, DateTime now)
    {
        if (!ForetellCollisionMeshSource.TryCapture(module, player, radius, resolution, out var snapshot, out var reason)
            || snapshot == null)
        {
            RegisterCapability("native.topology.mesh", typeof(ColliderMesh), "MeshPCB", false, false, reason);
            return false;
        }

        ++_topologyGeneration;
        var generation = _topologyGeneration;
        _topologySweepRequested = false;
        _topologySweepInProgress = false;
        _topologyHardInvalidation = false;
        _topologyFrontier.Clear();
        _topologySweepStartedAt = now;
        _topologyMeshCaptureMilliseconds = snapshot.CaptureMilliseconds;
        _topologyMeshPeakCaptureMilliseconds = Math.Max(_topologyMeshPeakCaptureMilliseconds, snapshot.CaptureMilliseconds);
        _topologyMeshTriangles = snapshot.Triangles.Length;
        _topologyMeshColliders = snapshot.Colliders;
        ++_topologyMeshCaptures;
        RegisterCapability("native.topology.mesh", typeof(ColliderMesh), "MeshPCB", true, false,
            $"{snapshot.Triangles.Length:N0} local triangles copied in {snapshot.CaptureMilliseconds:F2} ms; managed raster queued");
        _collisionRasterTask = Task.Run(() => new CollisionRasterWork(generation, ForetellCollisionRasterizer.Build(snapshot)));
        return true;
    }

    private void PollCompletedCollisionRaster()
    {
        var task = _collisionRasterTask;
        if (task == null || !task.IsCompleted)
            return;
        _collisionRasterTask = null;
        CollisionRasterWork work;
        try
        {
            work = task.GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            ++_topologyFailures;
            ++_topologyMeshFallbacks;
            _topologyMeshPrimary = false;
            _topologySweepRequested = true;
            _topologyRescanAfter = default;
            RegisterCapability("native.topology.raster", typeof(ForetellCollisionRasterizer), "Build", false, false,
                $"managed raster rejected safely: {e.GetType().Name}");
            return;
        }
        if (work.Generation != _topologyGeneration)
            return;

        var result = work.Result;
        var rebuildImmediately = _topologyRescanAfterRaster;
        _topologyRescanAfterRaster = false;
        var hadUsefulMesh = _topologyMeshPrimary && _topologyAnalysis is { PassableCells: > 0 };
        _topologyMeshPrimary = result.Analysis.PassableCells > 0;
        _topologyMeshFloorTriangles = result.FloorTriangles;
        _topologyMeshWallTriangles = result.WallTriangles;
        _topologyMeshCandidateSamples = result.CandidateSamples;
        _topologyMeshRasterMilliseconds = result.RasterMilliseconds;
        _topologyMeshPeakRasterMilliseconds = Math.Max(_topologyMeshPeakRasterMilliseconds, result.RasterMilliseconds);
        _topologyLastSweepMilliseconds = _topologyMeshCaptureMilliseconds + result.RasterMilliseconds;
        _topologyPeakSweepMilliseconds = Math.Max(_topologyPeakSweepMilliseconds, _topologyLastSweepMilliseconds);
        _topologySweepInProgress = false;
        _topologySweepRequested = !_topologyMeshPrimary || rebuildImmediately;
        ++_topologySweeps;
        var inCombat = Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];
        _topologyRescanAfter = rebuildImmediately ? default : DateTime.UtcNow.AddSeconds(inCombat ? 6 : 12);
        if (_topologyMeshPrimary)
        {
            _topology.ReplaceWith(result.Grid);
            _topologySampleRadius = Math.Min(result.Grid.Width, result.Grid.Height) * result.Grid.Resolution * .5f;
            _topologyFirstSurfaceMilliseconds = _topologySweepStartedAt == default
                ? 0
                : Math.Max(0, (DateTime.UtcNow - _topologySweepStartedAt).TotalMilliseconds);
            _topologyAnalysisComplete = true;
            ApplyTopologyAnalysis(result.Grid, result.Analysis, complete: true);
            RegisterCapability("native.topology.raster", typeof(ForetellCollisionRasterizer), "Build", true, false,
                $"{result.Analysis.PassableCells:N0} reachable cells from {result.FloorTriangles:N0} floor and {result.WallTriangles:N0} wall triangles in {result.RasterMilliseconds:F2} ms worker time");
        }
        else
        {
            ++_topologyMeshFallbacks;
            _topologyMeshRetryAfter = DateTime.UtcNow.AddSeconds(15);
            if (hadUsefulMesh)
            {
                _topologyMeshPrimary = true;
                _topologySweepRequested = false;
                _topologyRescanAfter = _topologyMeshRetryAfter;
                _topologyAnalysisComplete = true;
            }
            RegisterCapability("native.topology.raster", typeof(ForetellCollisionRasterizer), "Build", false, false,
                hadUsefulMesh
                    ? "replacement PCB raster found no connected surface; previous useful mesh retained"
                    : "PCB raster found no surface connected to the actor; ray frontier fallback enabled");
        }
    }

    private float DesiredTopologyRadius()
    {
        var visibleCapacity = _cfg.RadarZoom == ForetellRadarZoom.Automatic ? _cfg.RadarAutoMaximumRadius : _cfg.RadarWorldRadius;
        return Math.Clamp(visibleCapacity + 10, MinimumTopologyRadius, 128f);
    }

    private float TopologyRecenterDistance(float sampleRadius)
    {
        var visibleRadius = _radarDisplayedWorldRadius > 0
            ? _radarDisplayedWorldRadius
            : _cfg.RadarZoom == ForetellRadarZoom.Automatic ? _cfg.RadarAutoMinimumRadius : _cfg.RadarWorldRadius;
        var displayMargin = Math.Max(8, sampleRadius - visibleRadius - 6);
        return Math.Min(sampleRadius * .55f, displayMargin);
    }

    private static float DesiredTopologyResolution(float radius)
        => Math.Clamp(radius / TargetTopologyHalfCells, MinimumTopologyResolution, MaximumTopologyResolution);

    private void ResetLocalTopologyGrid(Vector3 player, float radius, float resolution)
    {
        ++_topologyGeneration;
        _topology.Reset(player, radius, resolution);
        _topologySampleRadius = radius;
        var center = TopologyCenter();
        var limit = radius + resolution * .35f;
        for (var i = 0; i < _topology.CellCount; ++i)
            if (Vector2.Distance(_topology.CellCenter(i), center) > limit)
                _topology.Set(i, TopologyCell.Void);
        _topologyFrontier.Clear();
        _topology.Cursor = 0;
    }

    private void StartTopologySweep(Vector2 player)
    {
        ++_topologyGeneration;
        _topology.ClearEdges();
        _topologyFrontier.Start(_topology, player, TopologyCenter(), _topologySampleRadius);
        _topology.Cursor = 0;
        _topologySweepInProgress = true;
        _topologySweepRequested = false;
        _topologyPublishProgress = _topologyHardInvalidation || _topologyAnalysis == null;
        if (_topologyAnalysis == null)
            _topologyAnalysisComplete = false;
        _topologyHardInvalidation = false;
        _topologySweepStartedAt = DateTime.UtcNow;
        _topologyBurstUntil = _topologySweepStartedAt.AddSeconds(_topologyPublishProgress ? 2 : .5);
        if (_topologyAnalysis == null)
            _topologyFirstSurfaceMilliseconds = 0;
        // An arena barrier can appear exactly at pull start. Keep an already useful published component until the
        // outward rescan has caught up instead of replacing it immediately with a one-cell island.
        if (_topologyPublishProgress && _topologyAnalysis == null)
            RequestTopologyAnalysis(player, complete: false);
    }

    private unsafe bool ProbeTopologyEdge(int from, int to)
    {
        var a = _topology.CellCenter(from);
        var b = _topology.CellCenter(to);
        var start = new Vector3(a.X, _topology.Heights[from] + TopologyWallProbeHeight, a.Y);
        var end = new Vector3(b.X, _topology.Heights[to] + TopologyWallProbeHeight, b.Y);
        var delta = end - start;
        var distance = delta.Length();
        return !float.IsFinite(distance) || distance <= .05f
            || BGCollisionModule.RaycastMaterialFilter(start, delta / Math.Max(.05f, distance), out _, distance - .05f);
    }

    private Vector2 TopologyCenter()
        => new(_topology.OriginX + _topology.Width * _topology.Resolution * .5f,
            _topology.OriginZ + _topology.Height * _topology.Resolution * .5f);

    private float DistanceToTopologyCenter(Vector2 player)
        => Vector2.Distance(player, TopologyCenter());

    private void SuspendTopology(DateTime now, string reason)
    {
        _topologySuspendedUntil = now.AddSeconds(30);
        _topologyConsecutiveOverruns = 0;
        RegisterCapability("native.topology.collision", typeof(BGCollisionModule), "RaycastMaterialFilter", false, false, reason);
        Service.Log($"[Foretell] Native topology entered safe cooldown for 30s: {reason}");
    }

    private void RequestTopologyAnalysis(Vector2 player, bool complete)
    {
        _topologyAnalysisPlayer = player;
        _topologyAnalysisQueued = true;
        _topologyAnalysisQueuedComplete |= complete;
        TryStartTopologyAnalysis();
    }

    private void TryStartTopologyAnalysis()
    {
        if (_topologyAnalysisTask != null || !_topologyAnalysisQueued || _topology.CellCount == 0)
            return;
        var snapshot = _topology.Snapshot();
        var generation = _topologyGeneration;
        var player = _topologyAnalysisPlayer;
        var complete = _topologyAnalysisQueuedComplete;
        _topologyAnalysisQueued = false;
        _topologyAnalysisQueuedComplete = false;
        _topologyLastAnalysisAt = DateTime.UtcNow;
        _topologyAnalysisTask = Task.Run(() => new TopologyAnalysisWork(generation, snapshot, player, complete,
            snapshot.Analyze(player, MaximumTopologyStepHeight, requireKnownEdges: true)));
    }

    private void PollCompletedTopologyAnalysis()
    {
        var task = _topologyAnalysisTask;
        if (task == null || !task.IsCompleted)
            return;
        _topologyAnalysisTask = null;
        TopologyAnalysisWork work;
        try
        {
            work = task.GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            ++_topologyFailures;
            RegisterCapability("native.topology.analysis", typeof(ForetellTopologyGrid), "Analyze", false, false, $"managed analysis rejected safely: {e.GetType().Name}");
            return;
        }
        if (work.Generation == _topologyGeneration)
            ApplyTopologyAnalysis(work.Grid, work.Analysis, work.Complete);
        TryStartTopologyAnalysis();
    }

    private void ApplyTopologyAnalysis(ForetellTopologyGrid grid, TopologyAnalysis analysis, bool complete)
    {
        if (!ForetellInferenceCore.ShouldReplaceTopologyAnalysis(_topologyAnalysis?.PassableCells ?? 0, analysis.PassableCells, complete))
            return;
        if (_topologyAnalysis == null && analysis.PassableCells > 0 && _topologySweepStartedAt != default)
            _topologyFirstSurfaceMilliseconds = Math.Max(0, (DateTime.UtcNow - _topologySweepStartedAt).TotalMilliseconds);
        _topologyAnalysis = analysis;
        _topologyAnalysisComplete = complete;
        // Frontier completion proves that every route from the seed ended at a wall, a void/drop or the local
        // survey radius. Unknown cells behind those boundaries are intentionally unsampled and are not a reason
        // to withhold a closed room or its bounded persistent snapshot.
        if (!complete || analysis.PassableCells == 0 || analysis.Fingerprint == _topologyFingerprint)
            return;

        ++_topologyChanges;
        _topologyFingerprint = analysis.Fingerprint;
        var now = DateTime.UtcNow;
        var encounter = Encounter(_territory);
        if (!encounter.Topologies.TryGetValue(analysis.Fingerprint, out var memory))
        {
            memory = new()
            {
                Fingerprint = analysis.Fingerprint,
                OriginX = grid.OriginX,
                OriginZ = grid.OriginZ,
                ReferenceY = grid.ReferenceY,
                Resolution = grid.Resolution,
                Width = grid.Width,
                Height = grid.Height,
                Cells = analysis.ConnectedCells.ToArray(),
                HeightCentimeters = analysis.HeightCentimeters.ToArray(),
                KnownEdges = analysis.KnownEdges.ToArray(),
                BlockedEdges = analysis.BlockedEdges.ToArray(),
                Contours = analysis.Contours.Select((loop, index) => new TopologyContourMemory
                {
                    Hole = SignedArea(loop) < 0,
                    Points = loop.Select(p => new TopologyPoint { X = p.X, Z = p.Y }).ToList()
                }).ToList(),
                PassableCells = analysis.PassableCells,
                BlockedCells = analysis.BlockedCells,
                UnknownCells = analysis.UnknownCells,
                Components = analysis.Components,
                FirstSeen = now
            };
            encounter.Topologies[analysis.Fingerprint] = memory;
            while (encounter.Topologies.Count > 8)
            {
                var oldest = encounter.Topologies.Values.OrderBy(t => t.LastSeen).First();
                encounter.Topologies.Remove(oldest.Fingerprint);
            }
        }
        memory.LastSeen = now;
        ++memory.Observations;

        var obs = Observation(ObservationKind.TopologySnapshot, detail: $"collision:{analysis.Fingerprint}");
        obs.SourceKind = SourceKind.Environment;
        StoreNative(obs, "native.topology.origin.x", grid.OriginX);
        StoreNative(obs, "native.topology.origin.z", grid.OriginZ);
        StoreNative(obs, "native.topology.referenceY", grid.ReferenceY);
        StoreNative(obs, "native.topology.resolution", grid.Resolution);
        StoreNative(obs, "native.topology.width", grid.Width);
        StoreNative(obs, "native.topology.height", grid.Height);
        StoreNative(obs, "native.topology.passableCells", analysis.PassableCells);
        StoreNative(obs, "native.topology.blockedCells", analysis.BlockedCells);
        StoreNative(obs, "native.topology.components", analysis.Components);
        StoreNative(obs, "native.topology.contours", analysis.Contours.Count);
        obs.Text["native.topology.fingerprint"] = analysis.Fingerprint;
        obs.Binary["native.topology.cells"] = analysis.ConnectedCells.ToArray();
        ProcessObservation(obs, enriched: true);
    }

    private bool TryRestoreKnownTopology(Vector3 player)
    {
        if (!_store.Encounters.TryGetValue(_territory, out var encounter))
            return false;
        var memory = encounter.Topologies.Values
            .Where(item => Math.Abs(player.Y - item.ReferenceY) <= 6
                && player.X >= item.OriginX && player.Z >= item.OriginZ
                && player.X < item.OriginX + item.Width * item.Resolution
                && player.Z < item.OriginZ + item.Height * item.Resolution)
            .OrderByDescending(item => item.LastSeen)
            .FirstOrDefault();
        if (memory == null || !_topology.Restore(memory.OriginX, memory.OriginZ, memory.ReferenceY, memory.Resolution,
            memory.Width, memory.Height, memory.Cells, memory.HeightCentimeters, memory.KnownEdges, memory.BlockedEdges))
            return false;
        _topologySampleRadius = Math.Min(memory.Width, memory.Height) * memory.Resolution * .5f;
        _topologyFingerprint = memory.Fingerprint;
        _topologyAnalysis = new(memory.Fingerprint, memory.Cells.ToArray(), memory.Cells.ToArray(), memory.HeightCentimeters.ToArray(),
            memory.KnownEdges.ToArray(), memory.BlockedEdges.ToArray(),
            memory.Contours.Select(contour => contour.Points.Select(point => new Vector2(point.X, point.Z)).ToList()).ToList(),
            memory.PassableCells, memory.BlockedCells, memory.UnknownCells, memory.Components);
        _topologyAnalysisComplete = true;
        return true;
    }

    internal bool? IsTopologyPassable(Vector2 world)
    {
        var mesh = _topology.IsConnectedPassable(world, _topologyAnalysis?.ConnectedCells);
        if (mesh == true && CurrentArenaBoundary is { ArenaLike: true } boundary
            && !ForetellArenaBoundaryCore.Contains(boundary.Points, world))
            return false;
        return mesh ?? IsArenaBoundaryPassable(world);
    }

    private static float SignedArea(IReadOnlyList<Vector2> points)
    {
        float area = 0;
        for (var i = 0; i < points.Count; ++i)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            area += a.X * b.Y - b.X * a.Y;
        }
        return area * .5f;
    }
}
