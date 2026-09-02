using System.Diagnostics;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private const int MaxRawFeatureWindowsPerFrame = 4;
    private const double MaxRawFeatureDrainMilliseconds = 0.35;
    private long _rawFeatureWindowsProcessed;
    private double _lastRawFeatureDrainMilliseconds;
    private double _peakRawFeatureDrainMilliseconds;

    private void DrainRawFeatureWindows()
    {
        var started = Stopwatch.GetTimestamp();
        var processed = 0;
        while (processed < MaxRawFeatureWindowsPerFrame
            && Stopwatch.GetElapsedTime(started).TotalMilliseconds < MaxRawFeatureDrainMilliseconds
            && _raw.TryDequeueFeature(out var window))
        {
            ++processed;
            var obs = new ForetellObservation
            {
                Sequence = ++_sequence,
                At = NormalizeObservationTime(window.At),
                TerritoryID = window.TerritoryID,
                Kind = ObservationKind.GenericFeature,
                SourceKind = SourceKind.Environment,
                Detail = "raw:250ms-window"
            };
            obs.Numeric["raw.window.serverPackets"] = window.ServerPackets;
            obs.Numeric["raw.window.clientPackets"] = window.ClientPackets;
            obs.Numeric["raw.window.actorControls"] = window.ActorControls;
            obs.Numeric["raw.window.payloadBytes"] = window.PayloadBytes;
            foreach (var (opcode, count) in window.Opcodes)
                obs.Numeric[$"raw.window.opcode[{opcode:X8}]"] = count;
            for (var i = 0; i < window.BinaryBuckets.Length; ++i)
                obs.Numeric[$"raw.window.binaryBucket[{i}]"] = window.BinaryBuckets[i];
            ProcessObservation(obs, enriched: true);
            ++_rawFeatureWindowsProcessed;
        }
        _lastRawFeatureDrainMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _peakRawFeatureDrainMilliseconds = Math.Max(_peakRawFeatureDrainMilliseconds, _lastRawFeatureDrainMilliseconds);
    }
}
