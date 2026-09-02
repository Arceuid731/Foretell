using System.IO;

namespace BossMod.Foretell;

public sealed record ForetellStorageMaintenanceResult
{
    public DateTime CompletedAt { get; init; }
    public int Examined { get; init; }
    public int Deleted { get; init; }
    public long BytesBefore { get; init; }
    public long BytesAfter { get; init; }
    public string Error { get; init; } = "";
}

public static class ForetellStorageMaintenance
{
    private sealed record Candidate(string Path, long Bytes, DateTime Updated);

    public static ForetellStorageMaintenanceResult Run(string rawDirectory, string replayDirectory, IEnumerable<string> protectedPaths,
        DateTime nowUtc, int retentionDays, long maximumBytes)
    {
        try
        {
            var protectedFullPaths = protectedPaths.Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var candidates = Enumerate(rawDirectory, "*.ftraw.gz").Concat(Enumerate(replayDirectory, "*.jsonl"))
                .Where(candidate => !protectedFullPaths.Contains(candidate.Path)).OrderBy(candidate => candidate.Updated).ToList();
            var before = candidates.Sum(candidate => candidate.Bytes);
            var remaining = before;
            var deleted = 0;
            var cutoff = nowUtc.AddDays(-Math.Clamp(retentionDays, 1, 3650));
            var quota = Math.Max(1024L * 1024, maximumBytes);

            foreach (var candidate in candidates)
            {
                if (candidate.Updated >= cutoff && remaining <= quota) continue;
                try
                {
                    File.Delete(candidate.Path);
                    remaining -= candidate.Bytes;
                    ++deleted;
                }
                catch
                {
                    // One locked/corrupt file must not stop maintenance of independent recordings.
                }
            }
            return new() { CompletedAt = nowUtc, Examined = candidates.Count, Deleted = deleted, BytesBefore = before, BytesAfter = Math.Max(0, remaining) };
        }
        catch (Exception e)
        {
            return new() { CompletedAt = nowUtc, Error = e.Message };
        }
    }

    private static IEnumerable<Candidate> Enumerate(string directory, string pattern)
    {
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly))
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            FileInfo info;
            try { info = new(full); }
            catch { continue; }
            if (info.Exists) yield return new(full, info.Length, info.LastWriteTimeUtc);
        }
    }
}
