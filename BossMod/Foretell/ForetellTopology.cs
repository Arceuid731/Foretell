using System.Diagnostics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private const float TopologyRadius = 48f;
    private const float TopologyResolution = 1f;
    private const int MaxTopologyRaysPerFrame = 16;
    private const double MaxTopologyMillisecondsPerFrame = .30;
    private readonly ForetellTopologyGrid _topology = new();
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
        _topologyAnalysis = null;
        _topologyFingerprint = "";
        _topology.Cursor = 0;
        _topologyConsecutiveOverruns = 0;
        _topologySuspendedUntil = default;
        _topologySweepRequested = true;
        _topologySweepInProgress = false;
        _topologyRescanAfter = default;
    }

    private void InvalidateTopology()
    {
        ++_topologyInvalidations;
        _topologySweepRequested = true;
        _topologyRescanAfter = DateTime.UtcNow.AddMilliseconds(500);
    }

    // Collision calls already back BMR line-of-sight checks. Foretell performs a bounded downward probe sweep on
    // the framework thread: no collision pointers cross threads and one slow probe trips the watchdog.
    private unsafe void SampleNativeTopology()
    {
        var now = DateTime.UtcNow;
        if (now < _topologySuspendedUntil)
            return;
        var player = _ws.Party[PartyState.PlayerSlot];
        if (player == null || (!_inPull && _ws.CurrentCFCID == 0))
            return;
        if (!_topologySweepInProgress && (!_topologySweepRequested || now < _topologyRescanAfter))
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
        ++_topologySweeps;
        var analysis = _topology.Analyze(player);
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
                OriginX = _topology.OriginX,
                OriginZ = _topology.OriginZ,
                ReferenceY = _topology.ReferenceY,
                Resolution = _topology.Resolution,
                Width = _topology.Width,
                Height = _topology.Height,
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
        StoreNative(obs, "native.topology.origin.x", _topology.OriginX);
        StoreNative(obs, "native.topology.origin.z", _topology.OriginZ);
        StoreNative(obs, "native.topology.referenceY", _topology.ReferenceY);
        StoreNative(obs, "native.topology.resolution", _topology.Resolution);
        StoreNative(obs, "native.topology.width", _topology.Width);
        StoreNative(obs, "native.topology.height", _topology.Height);
        StoreNative(obs, "native.topology.passableCells", analysis.PassableCells);
        StoreNative(obs, "native.topology.blockedCells", analysis.BlockedCells);
        StoreNative(obs, "native.topology.components", analysis.Components);
        StoreNative(obs, "native.topology.contours", analysis.Contours.Count);
        obs.Text["native.topology.fingerprint"] = analysis.Fingerprint;
        obs.Binary["native.topology.cells"] = analysis.ConnectedCells.ToArray();
        ProcessObservation(obs, enriched: true);
    }

    internal bool? IsTopologyPassable(Vector2 world)
        => _topology.IsConnectedPassable(world, _topologyAnalysis?.ConnectedCells);

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
