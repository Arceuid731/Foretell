using System.Text.Json;
using System.Threading;

namespace BossMod.Foretell;

internal sealed class GuideAnalysisMemory
{
    internal readonly Dictionary<string, string> Responses = [];
    internal long Characters;
}

internal sealed class GuideResumableModel(IGuideSummaryModel model, GuideAnalysisMemory memory) : IGuideSummaryModel
{
    public GuideModelRuntime Runtime => model.Runtime;
    public Task Start(Action<GuideModelProgress> progress, CancellationToken cancellation) => model.Start(progress, cancellation);
    public Task<string> Summarize(string source, GuideLanguage language, CancellationToken cancellation) => model.Summarize(source, language, cancellation);
    public async Task<string> Analyze(string system, string source, object schema, int outputTokens, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var key = GuideNames.Hash(JsonSerializer.Serialize(new { system, source, schema, outputTokens }));
        if (memory.Responses.TryGetValue(key, out var response)) return response;
        response = await model.Analyze(system, source, schema, outputTokens, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        if (memory.Responses.Count < 512 && memory.Characters + response.Length <= 4 * 1024 * 1024)
        {
            memory.Responses[key] = response;
            memory.Characters += response.Length;
        }
        return response;
    }
    public void Dispose() => model.Dispose();
}
