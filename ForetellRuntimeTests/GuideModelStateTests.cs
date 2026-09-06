using BossMod.Foretell;
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
            foreach (var stage in Enum.GetValues<GuideModelStage>())
                Check(!string.IsNullOrWhiteSpace(ForetellEngine.GuideModelStageLabel(stage, language)), "Missing runtime label");
        Tokenizer().GetAwaiter().GetResult();
        Console.WriteLine("Guide model states, context/RAM bounds and complete template/tokenizer preflight passed without model inference.");
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
