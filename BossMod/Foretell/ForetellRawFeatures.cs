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
            var obs = RawWindowObservation(window);
            obs.Sequence = ++_sequence;
            RegisterRecordedFeatures(obs);
            ProcessObservation(obs, enriched: true);
            ++_rawFeatureWindowsProcessed;
        }
        _lastRawFeatureDrainMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _peakRawFeatureDrainMilliseconds = Math.Max(_peakRawFeatureDrainMilliseconds, _lastRawFeatureDrainMilliseconds);
    }
}
