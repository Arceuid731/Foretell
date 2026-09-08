using BossMod.Foretell;
using System.Numerics;
using System.Text.Json;

internal static class MovingHazardReview
{
    public static void Run(string zip, string output)
    {
        var path = Path.Combine(Path.GetFullPath(output), "moving-hazards.json");
        if (string.Equals(Path.GetFullPath(zip), path, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output would overwrite input.");
        var tracker = new ForetellMovingHazards();
        var reader = new ForetellRecordingReader(zip);
        var pending = new Dictionary<(ulong Source, uint Action), ActivePrediction>();
        var sources = new HashSet<(ulong Source, uint Action)>();
        var errors = new List<double>();
        var samples = new List<object>();
        long observations = 0;
        long forecastFrames = 0;
        var renderAt = DateTime.MinValue;
        foreach (var observation in reader.Read())
        {
            if (++observations > 500000) throw new InvalidDataException("Observation limit exceeded.");
            if (observation.Kind == ObservationKind.ActionResolved && pending.Remove((observation.ActorID, observation.PrimaryID), out var prior))
            {
                var delta = (observation.At - prior.Activation).TotalSeconds;
                if (Math.Abs(delta) <= .2 && errors.Count < 100000)
                    errors.Add(Vector2.Distance(ForetellDecisionCore.OriginAt(prior, prior.Activation), new(observation.X, observation.Z)));
            }
            tracker.Observe(observation, observation.SourceKind);
            if (observation.At > renderAt) renderAt = observation.At;
            var hazards = tracker.Snapshot(renderAt);
            if (hazards.Length > 0) ++forecastFrames;
            foreach (var hazard in hazards)
            {
                var prediction = hazard.Prediction;
                var key = (prediction.CasterID, prediction.ActionID);
                pending[key] = prediction;
                if (sources.Add(key)) samples.Add(new { Source = key.CasterID, Action = key.ActionID, At = observation.At,
                    X = prediction.Origin.X, Z = prediction.Origin.Y, Radius = prediction.P1,
                    VelocityX = prediction.Velocity.X, VelocityZ = prediction.Velocity.Y, prediction.Confidence, hazard.AdvisoryOnly });
            }
            foreach (var key in pending.Where(pair => observation.At > pair.Value.Activation.AddSeconds(.2)).Select(pair => pair.Key).ToArray()) pending.Remove(key);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var report = new { Recording = Path.GetFileName(zip), observations, forecastFrames, emitterActions = sources.Count,
            verifiedNextImpacts = errors.Count, meanCenterError = errors.Count == 0 ? (double?)null : errors.Average(),
            maximumCenterError = errors.Count == 0 ? (double?)null : errors.Max(), samples,
            semantics = "Retrospective generic pulse forecasts from captured observations, not live UI validation. No boss IDs or duty-specific motion parameters supplied." };
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
    }
}
