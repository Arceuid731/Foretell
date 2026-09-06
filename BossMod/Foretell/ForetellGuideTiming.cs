using System.IO;
using System.Text.Json;

namespace BossMod.Foretell;

internal sealed class GuideTimingHistory
{
    private readonly string _path;
    private readonly Queue<double> _samples = new();
    public double? Estimate => _samples.Count == 0 ? null : _samples.Order().ElementAt(_samples.Count / 2);

    public GuideTimingHistory(string directory)
    {
        _path = Path.Combine(directory, "preparation-timing.json");
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length < 8192)
                foreach (var sample in (JsonSerializer.Deserialize<double[]>(File.ReadAllText(_path)) ?? []).TakeLast(16))
                    if (double.IsFinite(sample) && sample is > 0 and < 120) _samples.Enqueue(sample);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public void Record(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds is <= 0 or >= 120) return;
        _samples.Enqueue(seconds);
        while (_samples.Count > 16) _samples.Dequeue();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_samples.ToArray()));
            File.Move(_path + ".tmp", _path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
