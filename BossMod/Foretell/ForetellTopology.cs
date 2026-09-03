using System.Diagnostics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private const float TopologyRadius = 36f;
    private const float TopologyResolution = 4f;
    private const int MaxTopologyRaysPerFrame = 4;
    private const double MaxTopologyMillisecondsPerFrame = .12;
    private readonly ForetellTopologyGrid _topology = new();
    private sealed record TopologyAnalysisWork(long Generation, ForetellTopologyGrid Grid, Vector2 Player, TopologyAnalysis Analysis);
    private Task<TopologyAnalysisWork>? _topologyAnalysisTask;
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
    private DateTime _topologyRescanAfter;

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
        _topologyRescanAfter = default;
        ResetArenaBoundary();
    }

    private void InvalidateTopology()
    {
        ++_topologyInvalidations;
        _topologySweepRequested = true;
        _topologyRescanAfter = DateTime.UtcNow.AddMilliseconds(500);
        InvalidateArenaBoundary();
    }

    // Collision pointers never cross threads. A tiny bounded probe slice runs on the framework thread while out of
    // combat; the managed flood/contour analysis runs asynchronously and the learned result is reused in combat.
    private unsafe void SampleNativeTopology()
    {
        var now = DateTime.UtcNow;
        PollCompletedTopologyAnalysis();
        // Never probe during combat: one driver/game-scene call cannot be pre-empted by a managed stopwatch after
        // it has entered native code. A completed pre-pull or remembered topology remains active for rendering.
        if (Service.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat] || now < _topologySuspendedUntil)
            return;
        var player = _ws.Party[PartyState.PlayerSlot];
        if (player == null || _ws.CurrentCFCID == 0)
            return;

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
        if (SampleNativeArenaBoundary(module, player3, player, now))
            return;
        // The inexpensive radial observer is also encounter-classification evidence, so it is independent of the
        // chosen radar presentation. The denser floor fallback is only useful when Auto needs a rendered frame.
        if (_cfg.RadarShape != ForetellRadarShape.Auto)
            return;
        // A complete radial wall boundary is both faster and more precise for normal enclosed rooms. The floor
        // grid remains the fallback for open platforms and arenas whose edge has no vertical collision wall.
        if (CurrentArenaBoundary != null)
            return;
        if (!_topologySweepInProgress && (!_topologySweepRequested || now < _topologyRescanAfter))
            return;
        if (_topologyAnalysisTask != null)
            return;
        if (_topology.CellCount == 0)
            TryRestoreKnownTopology(player3);
        var needsReset = _topology.CellCount == 0
            || !_topology.Contains(new(player3.X, player3.Z))
            || Math.Abs(player3.Y - _topology.ReferenceY) > 6
            || DistanceToTopologyCenter(new(player3.X, player3.Z)) > TopologyRadius * .34f;
        if (needsReset)
        {
            _topology.Reset(player3, TopologyRadius, TopologyResolution);
            _topologyAnalysis = null;
            _topologyFingerprint = "";
            _topologySweepRequested = true;
            _topologySweepInProgress = false;
        }
        if (!_topologySweepInProgress)
        {
            _topology.Cursor = 0;
            _topologySweepInProgress = true;
            _topologySweepRequested = false;
        }

        var started = Stopwatch.GetTimestamp();
        var sampled = 0;
        try
        {
            while (sampled < MaxTopologyRaysPerFrame && Stopwatch.GetElapsedTime(started).TotalMilliseconds < MaxTopologyMillisecondsPerFrame)
            {
                if (_topology.Cursor >= _topology.CellCount)
                {
                    CompleteTopologySweep(new(player3.X, player3.Z));
                    _topologySweepInProgress = false;
                    break;
                }

                var index = _topology.Cursor++;
                var point = _topology.CellCenter(index);
                var origin = new Vector3(point.X, player3.Y + 3.5f, point.Y);
                if (!BGCollisionModule.RaycastMaterialFilter(origin, -Vector3.UnitY, out var hit, 12f))
                {
                    _topology.Set(index, TopologyCell.Void);
                }
                else
                {
                    var delta = hit.Point.Y - player3.Y;
                    var floorLike = hit.Normal.Y >= .35f && delta is >= -6f and <= 2.25f;
                    _topology.Set(index, floorLike ? TopologyCell.Passable : TopologyCell.Blocked, hit.Point.Y);
                }
                ++sampled;
                ++_topologyRays;
            }
        }
        catch (Exception e)
        {
            ++_topologyFailures;
            SuspendTopology(now, $"collision probe rejected safely: {e.GetType().Name}");
            return;
        }

        if (_topologySweepInProgress && _topology.Cursor >= _topology.CellCount)
        {
            CompleteTopologySweep(new(player3.X, player3.Z));
            _topologySweepInProgress = false;
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
            RegisterCapability("native.topology.collision", typeof(BGCollisionModule), "RaycastMaterialFilter", true, false, "bounded adaptive collision probes");
        }
    }

    private float DistanceToTopologyCenter(Vector2 player)
    {
        var center = new Vector2(_topology.OriginX + _topology.Width * _topology.Resolution * .5f,
            _topology.OriginZ + _topology.Height * _topology.Resolution * .5f);
        return Vector2.Distance(player, center);
    }

    private void SuspendTopology(DateTime now, string reason)
    {
        _topologySuspendedUntil = now.AddSeconds(30);
        _topologyConsecutiveOverruns = 0;
        RegisterCapability("native.topology.collision", typeof(BGCollisionModule), "RaycastMaterialFilter", false, false, reason);
        Service.Log($"[Foretell] Native topology entered safe cooldown for 30s: {reason}");
    }

    private void CompleteTopologySweep(Vector2 player)
    {
        if (_topologyAnalysisTask != null)
            return;
        ++_topologySweeps;
        var snapshot = _topology.Snapshot();
        var generation = _topologyGeneration;
        _topologyAnalysisTask = Task.Run(() => new TopologyAnalysisWork(generation, snapshot, player, snapshot.Analyze(player)));
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
        if (work.Generation != _topologyGeneration)
            return;
        ApplyTopologyAnalysis(work.Grid, work.Analysis);
    }

    private void ApplyTopologyAnalysis(ForetellTopologyGrid grid, TopologyAnalysis analysis)
    {
        _topologyAnalysis = analysis;
        if (analysis.UnknownCells != 0 || analysis.PassableCells == 0 || analysis.Fingerprint == _topologyFingerprint)
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
            memory.Width, memory.Height, memory.Cells, memory.HeightCentimeters))
            return false;
        _topologyFingerprint = memory.Fingerprint;
        _topologyAnalysis = new(memory.Fingerprint, memory.Cells.ToArray(), memory.HeightCentimeters.ToArray(),
            memory.Contours.Select(contour => contour.Points.Select(point => new Vector2(point.X, point.Z)).ToList()).ToList(),
            memory.PassableCells, memory.BlockedCells, memory.UnknownCells, memory.Components);
        return true;
    }

    internal bool? IsTopologyPassable(Vector2 world)
        => IsArenaBoundaryPassable(world) ?? _topology.IsConnectedPassable(world, _topologyAnalysis?.ConnectedCells);

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
