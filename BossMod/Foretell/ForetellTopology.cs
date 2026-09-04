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
    private const int MaxTopologyRaysPerFrame = 12;
    private const double MaxTopologyMillisecondsPerFrame = .30;
    private readonly ForetellTopologyGrid _topology = new();
    private readonly List<int> _topologyProbeOrder = [];
    private readonly Queue<(int From, int To)> _topologyPendingEdges = [];
    private readonly HashSet<long> _topologyQueuedEdges = [];
    private sealed record TopologyAnalysisWork(long Generation, ForetellTopologyGrid Grid, Vector2 Player, bool Complete, TopologyAnalysis Analysis);
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
    private bool _topologyHardInvalidation = true;
    private bool _topologyPublishProgress;
    private DateTime _topologyRescanAfter;
    private DateTime _topologyLastAnalysisAt;
    private Vector2 _topologyAnalysisPlayer;
    private bool _topologyAnalysisQueued;
    private bool _topologyAnalysisQueuedComplete;
    private float _topologySampleRadius;
    private int _topologyProbeCursor;
    private int _topologyFloorSamples;
    private int _topologyEdgeSamples;
    private bool _topologyCombatStateKnown;
    private bool _topologyLastCombatState;

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
        _topologyHardInvalidation = true;
        _topologyPublishProgress = false;
        _topologyRescanAfter = default;
        _topologyLastAnalysisAt = default;
        _topologyAnalysisQueued = false;
        _topologyAnalysisQueuedComplete = false;
        _topologySampleRadius = 0;
        _topologyProbeCursor = 0;
        _topologyCombatStateKnown = false;
        _topologyLastCombatState = false;
        _topologyProbeOrder.Clear();
        _topologyPendingEdges.Clear();
        _topologyQueuedEdges.Clear();
        ResetArenaBoundary();
    }

    private void InvalidateTopology(bool immediate = false, bool hard = true)
    {
        ++_topologyInvalidations;
        _topologySweepRequested = true;
        _topologyHardInvalidation |= hard;
        if (hard)
        {
            ++_topologyGeneration;
            _topologySweepInProgress = false;
            _topologyAnalysisQueued = false;
            _topologyAnalysisQueuedComplete = false;
            _topologyPendingEdges.Clear();
            _topologyQueuedEdges.Clear();
        }
        _topologyRescanAfter = immediate ? default : DateTime.UtcNow.AddMilliseconds(150);
        InvalidateArenaBoundary(immediate);
    }

    // Collision pointers never cross threads. Bounded ray slices run on the framework thread in and out of combat;
    // only copied primitive arrays cross to the managed flood/contour worker. The grid is local, player-centred and
    // resolution-adaptive, so travelling through a territory never turns the radar into a full-map reveal.
    private unsafe void SampleNativeTopology()
    {
        var now = DateTime.UtcNow;
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
            _topologyFingerprint = "";
            _topologySweepRequested = true;
            _topologyHardInvalidation = true;
            _topologySweepInProgress = false;
        }
        if (!_topologySweepInProgress && _topologySweepRequested && now >= _topologyRescanAfter)
            StartTopologySweep(new(player3.X, player3.Z));
        if (SampleNativeArenaBoundary(module, player3, player, now))
            return;
        if (!_topologySweepInProgress)
            return;

        var started = Stopwatch.GetTimestamp();
        var sampled = 0;
        try
        {
            while (sampled < MaxTopologyRaysPerFrame && Stopwatch.GetElapsedTime(started).TotalMilliseconds < MaxTopologyMillisecondsPerFrame)
            {
                if (_topologyPendingEdges.TryDequeue(out var edge))
                {
                    _topologyQueuedEdges.Remove(EdgeKey(edge.From, edge.To));
                    if (_topology.IsEdgeKnown(edge.From, edge.To)
                        || _topology.Cells[edge.From] != (byte)TopologyCell.Passable
                        || _topology.Cells[edge.To] != (byte)TopologyCell.Passable)
                        continue;
                    if (Math.Abs(_topology.Heights[edge.From] - _topology.Heights[edge.To]) > MaximumTopologyStepHeight)
                    {
                        _topology.SetEdge(edge.From, edge.To, true);
                        continue;
                    }
                    ProbeTopologyEdge(edge.From, edge.To);
                    ++sampled;
                    ++_topologyRays;
                    ++_topologyEdgeSamples;
                    continue;
                }

                if (_topologyProbeCursor >= _topologyProbeOrder.Count)
                    break;
                var index = _topologyProbeOrder[_topologyProbeCursor++];
                _topology.Cursor = _topologyProbeCursor;
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
                QueueTopologyEdges(index);
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
        if (_topologySweepInProgress && _topologyProbeCursor >= _topologyProbeOrder.Count && _topologyPendingEdges.Count == 0)
        {
            _topologySweepInProgress = false;
            ++_topologySweeps;
            _topologyRescanAfter = now.AddSeconds(inCombat ? 6 : 12);
            RequestTopologyAnalysis(player2, complete: true);
        }
        else if (_topologyPublishProgress && sampled != 0 && (now - _topologyLastAnalysisAt).TotalMilliseconds >= 250)
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
            RegisterCapability("native.topology.collision", typeof(BGCollisionModule), "RaycastMaterialFilter", true, false, "bounded local walkable-surface and barrier probes");
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
        _topologyPendingEdges.Clear();
        _topologyQueuedEdges.Clear();
        _topologyProbeCursor = 0;
        _topology.Cursor = 0;
    }

    private void StartTopologySweep(Vector2 player)
    {
        ++_topologyGeneration;
        _topology.ClearEdges();
        BuildTopologyProbeOrder(player);
        _topologyPendingEdges.Clear();
        _topologyQueuedEdges.Clear();
        _topologyProbeCursor = 0;
        _topology.Cursor = 0;
        _topologySweepInProgress = true;
        _topologySweepRequested = false;
        _topologyPublishProgress = _topologyHardInvalidation || _topologyAnalysis == null;
        _topologyHardInvalidation = false;
        // An arena barrier can appear exactly at pull start. Requiring freshly observed edges makes the old open
        // component disappear as soon as the managed worker runs, before the outward scan grows it again.
        if (_topologyPublishProgress)
            RequestTopologyAnalysis(player, complete: false);
    }

    private void BuildTopologyProbeOrder(Vector2 origin)
    {
        _topologyProbeOrder.Clear();
        var included = new bool[_topology.CellCount];
        var limitSq = (_topologySampleRadius + _topology.Resolution * .35f) * (_topologySampleRadius + _topology.Resolution * .35f);
        var center = TopologyCenter();
        var seedX = Math.Clamp((int)((origin.X - _topology.OriginX) / _topology.Resolution), 0, _topology.Width - 1);
        var seedZ = Math.Clamp((int)((origin.Y - _topology.OriginZ) / _topology.Resolution), 0, _topology.Height - 1);
        var maxRing = Math.Max(_topology.Width, _topology.Height);
        for (var ring = 0; ring < maxRing; ++ring)
        {
            var minX = Math.Max(0, seedX - ring);
            var maxX = Math.Min(_topology.Width - 1, seedX + ring);
            var minZ = Math.Max(0, seedZ - ring);
            var maxZ = Math.Min(_topology.Height - 1, seedZ + ring);
            for (var x = minX; x <= maxX; ++x)
            {
                Add(x, minZ);
                if (maxZ != minZ) Add(x, maxZ);
            }
            for (var z = minZ + 1; z < maxZ; ++z)
            {
                Add(minX, z);
                if (maxX != minX) Add(maxX, z);
            }
            if (minX == 0 && maxX == _topology.Width - 1 && minZ == 0 && maxZ == _topology.Height - 1)
                break;
        }

        void Add(int x, int z)
        {
            var index = z * _topology.Width + x;
            if (included[index]) return;
            included[index] = true;
            if (_topology.Cells[index] == (byte)TopologyCell.Unknown || Vector2.DistanceSquared(_topology.CellCenter(index), center) <= limitSq)
                _topologyProbeOrder.Add(index);
        }
    }

    private void QueueTopologyEdges(int index)
    {
        if (_topology.Cells[index] != (byte)TopologyCell.Passable)
            return;
        var x = index % _topology.Width;
        var z = index / _topology.Width;
        Queue(x - 1, z);
        Queue(x + 1, z);
        Queue(x, z - 1);
        Queue(x, z + 1);

        void Queue(int neighborX, int neighborZ)
        {
            if ((uint)neighborX >= (uint)_topology.Width || (uint)neighborZ >= (uint)_topology.Height)
                return;
            var neighbor = neighborZ * _topology.Width + neighborX;
            if (_topology.Cells[neighbor] != (byte)TopologyCell.Passable || _topology.IsEdgeKnown(index, neighbor))
                return;
            var key = EdgeKey(index, neighbor);
            if (_topologyQueuedEdges.Add(key))
                _topologyPendingEdges.Enqueue((index, neighbor));
        }
    }

    private unsafe void ProbeTopologyEdge(int from, int to)
    {
        var a = _topology.CellCenter(from);
        var b = _topology.CellCenter(to);
        var start = new Vector3(a.X, _topology.Heights[from] + TopologyWallProbeHeight, a.Y);
        var end = new Vector3(b.X, _topology.Heights[to] + TopologyWallProbeHeight, b.Y);
        var delta = end - start;
        var distance = delta.Length();
        var blocked = !float.IsFinite(distance) || distance <= .05f
            || BGCollisionModule.RaycastMaterialFilter(start, delta / Math.Max(.05f, distance), out _, distance - .05f);
        _topology.SetEdge(from, to, blocked);
    }

    private static long EdgeKey(int a, int b)
    {
        var lower = Math.Min(a, b);
        var upper = Math.Max(a, b);
        return ((long)lower << 32) | (uint)upper;
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
        _topologyAnalysis = analysis;
        if (!complete || analysis.UnknownCells != 0 || analysis.PassableCells == 0 || analysis.Fingerprint == _topologyFingerprint)
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
        return true;
    }

    internal bool? IsTopologyPassable(Vector2 world)
        => _topology.IsConnectedPassable(world, _topologyAnalysis?.ConnectedCells) ?? IsArenaBoundaryPassable(world);

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
