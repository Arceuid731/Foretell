using BossMod.Foretell;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

internal static class GuideModelStateTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static void Run()
    {
        Check(GuideModelLimits.Context(int.MinValue) == 4096 && GuideModelLimits.Context(int.MaxValue) == 131072, "Invalid context bounds");
        Check(GuideModelLimits.MemoryGiB(int.MinValue) == 4 && GuideModelLimits.MemoryGiB(int.MaxValue) == 12, "Invalid RAM bounds");
        Check(!GuideModelLimits.Fits(0, 4096) && !GuideModelLimits.Fits(int.MaxValue, 32768), "Invalid prompt count accepted");
        var boundary = 16384 - GuideModelLimits.OutputTokens - GuideModelLimits.TemplateReserve;
        Check(GuideModelLimits.Fits(boundary, 16384) && !GuideModelLimits.Fits(boundary + 1, 16384), "Output/template reserve not enforced");
        Check(!GuideModelLimits.Fits(8000, 4096) && GuideModelLimits.Fits(8000, 16384), "Larger context did not permit the complete prompt");
        foreach (var language in Enum.GetValues<GuideLanguage>())
        {
            foreach (var stage in Enum.GetValues<GuideModelStage>())
                Check(!string.IsNullOrWhiteSpace(ForetellEngine.GuideModelStageLabel(stage, language)), "Missing runtime label");
            foreach (var state in Enum.GetValues<GuideModelFileState>())
                Check(!string.IsNullOrWhiteSpace(ForetellEngine.GuideModelFileLabel(new(state, 5, 10), language)), "Missing file status label");
        }
        Storage();
        AnalysisTiming();
        Tokenizer().GetAwaiter().GetResult();
        Console.WriteLine("Guide model states, context/RAM bounds and complete template/tokenizer preflight passed without model inference.");
    }

    private static void Storage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "foretell-model-state-" + Guid.NewGuid().ToString("N"));
        var asset = new GuideModelAsset("test.gguf", "https://example.invalid/test.gguf", 10, "unused");
        var path = Path.Combine(directory, asset.Name);
        try
        {
            Check(ForetellGuideLocalModel.InspectAsset(directory, asset).State == GuideModelFileState.Missing, "Missing directory reported as installed");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(path + ".part", []);
            Check(ForetellGuideLocalModel.InspectAsset(directory, asset).State == GuideModelFileState.Missing, "Empty download reported as saved");
            File.WriteAllBytes(path + ".part", new byte[5]);
            Check(ForetellGuideLocalModel.InspectAsset(directory, asset) == new GuideModelFileStatus(GuideModelFileState.Partial, 5, 10), "Partial download was not retained");
            File.WriteAllBytes(path + ".part", new byte[10]);
            Check(ForetellGuideLocalModel.InspectAsset(directory, asset).State == GuideModelFileState.Partial, "Unverified temporary download reported as installed");
            File.WriteAllBytes(path + ".part", new byte[11]);
            Check(ForetellGuideLocalModel.InspectAsset(directory, asset).State == GuideModelFileState.InvalidSize, "Oversized download reported as resumable");
            File.WriteAllBytes(path, new byte[10]);
            Check(ForetellGuideLocalModel.InspectAsset(directory, asset).State == GuideModelFileState.OnDisk, "Complete model hidden by a stale partial download");
            File.Delete(path + ".part");
            File.WriteAllBytes(path, new byte[9]);
            Check(ForetellGuideLocalModel.InspectAsset(directory, asset).State == GuideModelFileState.InvalidSize, "Incomplete model reported as on disk");
            File.WriteAllBytes(path + ".part", new byte[5]);
            Check(ForetellGuideLocalModel.InspectAsset(directory, asset).State == GuideModelFileState.Partial, "Replacement download hidden by an incomplete model");
            File.Delete(path);
            File.Delete(path + ".part");
            Check(ForetellGuideLocalModel.InspectAsset(directory, asset).State == GuideModelFileState.Missing, "Removed model still reported as on disk");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void AnalysisTiming()
    {
        var second = Stopwatch.Frequency;
        var timing = new GuideAnalysisTiming().AtStage("InstallingOrStarting", 0);
        Check(timing.Elapsed(10 * second) == 0, "Download time included in analysis");
        timing = timing.AtStage("Analyzing", 10 * second).AtStage("Analyzing", 12 * second);
        Check(timing.Elapsed(15 * second) == 5, "Progress reset the running analysis timer");
        timing = timing.AtStage("PausedInCombat", 15 * second);
        Check(timing.Elapsed(25 * second) == 5, "Analysis timer advanced during combat");
        timing = timing.AtStage("InstallingOrStarting", 25 * second).AtStage("Summarizing", 30 * second);
        Check(timing.Elapsed(32 * second) == 7, "Resumed analysis lost earlier elapsed time or counted startup");
        timing = timing.AtStage("Ready", 32 * second);
        Check(timing.StartedAt == null && timing.Elapsed(50 * second) == 7, "Completed analysis timer kept running");
        var failed = new GuideAnalysisTiming().AtStage("Analyzing", 0).AtStage("Unavailable: IOException", second);
        Check(failed.Elapsed(10 * second) == 1, "Failed analysis timer kept running");
    }

    private static async Task Tokenizer()
    {
        var source = "<source>" + new string('é', 14000) + " 日本語 FINAL CONDITION</source>";
        var messages = new[] { new { role = "system", content = "Translate without dropping conditions." }, new { role = "user", content = source } };
        using var handler = new TokenizerHandler(source);
        using var client = new HttpClient(handler) { BaseAddress = new("http://127.0.0.1/") };
        var count = await ForetellGuideLocalModel.CountPromptTokens(client, messages, CancellationToken.None);
        Check(count == 8200 && handler.Requests == 2, "Preflight did not use the full chat template then tokenize");
        handler.EmptyTemplate = true;
        try { await ForetellGuideLocalModel.CountPromptTokens(client, messages, CancellationToken.None); throw new Exception("Empty template accepted"); }
        catch (InvalidDataException) { }
        handler.EmptyTemplate = false;
        handler.OversizedTemplate = true;
        try { await ForetellGuideLocalModel.CountPromptTokens(client, messages, CancellationToken.None); throw new Exception("Oversized template response accepted"); }
        catch (InvalidDataException) { }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try { await ForetellGuideLocalModel.CountPromptTokens(client, messages, canceled.Token); throw new Exception("Tokenizer ignored combat cancellation"); }
        catch (OperationCanceledException) { }
    }

    private sealed class TokenizerHandler(string source) : HttpMessageHandler
    {
        public int Requests;
        public bool EmptyTemplate;
        public bool OversizedTemplate;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            ++Requests;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
            string response;
            if (request.RequestUri!.AbsolutePath == "/apply-template")
            {
                Check(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString() == source, "Long source was clipped before templating");
                Check(body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString() == "system", "Prompt omitted system instructions");
                response = JsonSerializer.Serialize(new { prompt = EmptyTemplate ? "" : OversizedTemplate ? new string('x', 2 * 1024 * 1024 + 1) : "TEMPLATE " + source });
            }
            else
            {
                Check(request.RequestUri.AbsolutePath == "/tokenize", "Preflight attempted inference");
                Check(body.RootElement.GetProperty("content").GetString() == "TEMPLATE " + source, "Token count is not based on complete rendered prompt");
                Check(body.RootElement.GetProperty("add_special").GetBoolean() && body.RootElement.GetProperty("parse_special").GetBoolean(), "Special tokens not counted");
                response = JsonSerializer.Serialize(new { tokens = Enumerable.Repeat(123, 8200).ToArray() });
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
