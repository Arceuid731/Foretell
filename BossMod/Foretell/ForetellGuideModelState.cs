using System.Diagnostics;

namespace BossMod.Foretell;

internal enum GuideModelStage { Unloaded, Verifying, Downloading, Loading, Loaded, Tokenizing, Generating, Stopping, Failed }

internal sealed record GuideModelRuntime(GuideModelStage Stage = GuideModelStage.Unloaded, int? ProcessID = null,
    string Backend = "", int ContextTokens = 0, int? PromptTokens = null, bool VerifiedThisSession = false, string ModelID = GuideModelCatalog.DefaultID);

internal enum GuideModelFileState { Missing, Partial, OnDisk, InvalidSize, Unavailable }
internal sealed record GuideModelFileStatus(GuideModelFileState State, long Bytes, long Total);
internal sealed record GuideModelStorage(GuideModelFileStatus Model, GuideModelFileStatus Engine);

internal sealed record GuideAnalysisTiming(double Seconds = 0, long? StartedAt = null)
{
    internal double Elapsed(long timestamp) => Seconds + (StartedAt is { } started ? Math.Max(0, Stopwatch.GetElapsedTime(started, timestamp).TotalSeconds) : 0);

    internal GuideAnalysisTiming AtStage(string stage, long timestamp) => stage is "Analyzing" or "Summarizing"
        ? StartedAt != null ? this : this with { StartedAt = timestamp }
        : new(Elapsed(timestamp));
}

internal static class GuideModelLimits
{
    internal const int DefaultContext = 32768;
    internal const int DefaultMemoryGiB = 6;
    internal const int OutputTokens = 600;
    internal const int TemplateReserve = 64;
    internal static int Context(int value) => Math.Clamp(value, 4096, 131072);
    internal static int MemoryGiB(int value) => Math.Clamp(value, 4, 12);
    internal static bool Fits(int promptTokens, int contextTokens, int outputTokens = OutputTokens) => promptTokens > 0 && outputTokens > 0
        && (long)promptTokens + outputTokens + TemplateReserve <= Context(contextTokens);
}
