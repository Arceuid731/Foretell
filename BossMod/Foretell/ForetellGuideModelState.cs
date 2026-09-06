namespace BossMod.Foretell;

internal enum GuideModelStage { Unloaded, Verifying, Downloading, Loading, Loaded, Tokenizing, Generating, Stopping, Failed }

internal sealed record GuideModelRuntime(GuideModelStage Stage = GuideModelStage.Unloaded, int? ProcessID = null,
    string Backend = "", int ContextTokens = 0, int? PromptTokens = null, bool VerifiedThisSession = false, string ModelID = GuideModelCatalog.DefaultID);

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
