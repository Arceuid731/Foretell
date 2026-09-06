using BossMod.Foretell;
using System.Net;
using System.Text;
using System.Text.Json;

internal static class GuideModelTransportTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static void Run()
    {
        Exchanges().GetAwaiter().GetResult();
        Failures().GetAwaiter().GetResult();
        Deadlines().GetAwaiter().GetResult();
        Console.WriteLine("Local model exchange diagnostics, failed responses and complete-body deadlines passed without inference or network.");
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new("http://127.0.0.1/"), Timeout = Timeout.InfiniteTimeSpan };
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static string Completion(string content = "{}", string finish = "stop", bool usage = true)
        => JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = finish, message = new { content, reasoning_content = "Source checked." } } },
            usage = usage ? new { prompt_tokens = 17, completion_tokens = 29 } : null
        });

    private static async Task Exchanges()
    {
        var exchanges = new List<GuideModelExchange>();
        using var handler = new ModelHandler(_ => Json(Completion()));
        using var client = Client(handler);
        using var model = new ForetellGuideLocalModel(client);
        IGuideSummaryModel adapter = model;
        adapter.ExchangeTrace = exchanges.Add;
        var source = "Full guide: é 日本語; keep the final exception.";
        var schema = new { type = "object", additionalProperties = false };
        var output = await model.Analyze("Extract the guide.", source, schema, 100, CancellationToken.None);
        Check(output == "{}", "Diagnostics changed successful model content");
        Check(exchanges.Select(exchange => exchange.State).SequenceEqual(["Started", "Tokenized", "Completed"]), "Missing or duplicated model exchange stages");
        Check(exchanges[0].System == "Extract the guide.\nRequired JSON schema:\n" + JsonSerializer.Serialize(schema), "Trace omitted the actual appended schema");
        Check(exchanges.All(exchange => exchange.Source == source), "Trace truncated the original guide source");
        Check(exchanges[0].RequestJson == handler.CompletionRequest, "Trace does not contain the actual request JSON");
        Check(exchanges[0].PromptTokens == null && exchanges[1].PromptTokens == 7, "Tokenization stages contain incorrect token counts");
        Check(exchanges[^1] is { Response: "{}", Reasoning: "Source checked.", FinishReason: "stop", PromptTokens: 17, OutputTokens: 29, Error: "" }, "Response metadata was not preserved");
        Check(exchanges.Zip(exchanges.Skip(1)).All(pair => pair.First.Seconds <= pair.Second.Seconds), "Request duration moved backwards");

        handler.Respond = _ => Json(Completion(usage: false));
        exchanges.Clear();
        await model.Analyze("Extract.", source, schema, 100, CancellationToken.None);
        Check(exchanges[^1].PromptTokens == null && exchanges[^1].OutputTokens == null, "Missing usage was fabricated instead of remaining unknown");

        model.ExchangeTrace = _ => throw new InvalidOperationException("Diagnostic subscriber failed.");
        model.AnalysisTrace = (_, _) => throw new InvalidOperationException("Legacy diagnostic subscriber failed.");
        Check(await model.Analyze("Extract.", source, schema, 100, CancellationToken.None) == "{}", "A diagnostic subscriber interrupted inference");
    }

    private static async Task Failures()
    {
        var exchanges = new List<GuideModelExchange>();
        using var handler = new ModelHandler(_ => Json(Completion("{\"bosses\":[", "length")));
        using var client = Client(handler);
        const string secret = "per-process-test-key-not-for-the-journal";
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        using var model = new ForetellGuideLocalModel(client) { ExchangeTrace = exchanges.Add };
        var schema = new { type = "object" };
        try { await model.Analyze("Extract.", "Complete guide.", schema, 100, CancellationToken.None); throw new Exception("Truncated output accepted"); }
        catch (GuideOutputException) { }
        Check(exchanges[^1] is { State: "Failed", Response: "{\"bosses\":[", Reasoning: "Source checked.", FinishReason: "length", OutputTokens: 29 }, "Truncated content or reasoning was lost before the failure trace");
        Check(exchanges[^1].Error.Contains(nameof(GuideOutputException), StringComparison.Ordinal), "Truncation cause omitted");

        foreach (var response in new[] { "invalid { completion", "{\"choices\":[]}", "null", "{\"choices\":[null]}" })
        {
            exchanges.Clear();
            handler.Respond = _ => Json(response);
            try { await model.Analyze("Extract.", "Complete guide.", schema, 100, CancellationToken.None); throw new Exception("Malformed completion accepted"); }
            catch (InvalidDataException) { }
            Check(exchanges[^1].State == "Failed" && exchanges[^1].Response == response, "Malformed response body was not retained");
            Check(exchanges[^1].Error.Contains("v1/chat/completions", StringComparison.Ordinal), "Malformed response omitted the endpoint");
        }

        exchanges.Clear();
        handler.Respond = _ => Json(secret + new string('x', 30000), HttpStatusCode.InternalServerError);
        try { await model.Analyze("Extract.", "Complete guide.", schema, 100, CancellationToken.None); throw new Exception("HTTP error accepted"); }
        catch (HttpRequestException error) { Check(error.StatusCode == HttpStatusCode.InternalServerError, "HTTP status was lost"); }
        Check(exchanges[^1].Response.Length <= 16384 && exchanges[^1].Response.Contains("[redacted]", StringComparison.Ordinal), "HTTP error body was unbounded or omitted");
        Check(exchanges[^1].Error.Contains("HTTP 500", StringComparison.Ordinal) && exchanges[^1].Error.Length < 1200, "HTTP failure status or bounded detail missing");
        Check(!JsonSerializer.Serialize(exchanges).Contains(secret, StringComparison.Ordinal), "Loopback authorization leaked into exchange diagnostics");

        exchanges.Clear();
        handler.FailTemplate = true;
        handler.Respond = _ => Json("{\"error\":\"template unavailable\"}", HttpStatusCode.ServiceUnavailable);
        try { await model.Analyze("Extract.", "Complete guide.", schema, 100, CancellationToken.None); throw new Exception("Tokenizer failure accepted"); }
        catch (HttpRequestException) { }
        Check(exchanges.Select(exchange => exchange.State).SequenceEqual(["Started", "Failed"]), "Prompt was not traced before tokenization failed");
        Check(exchanges[^1].Response.Contains("template unavailable", StringComparison.Ordinal) && exchanges[^1].Error.Contains("apply-template", StringComparison.Ordinal), "Tokenizer failure body or endpoint was lost");

        exchanges.Clear();
        handler.FailTemplate = false;
        handler.TokenCount = 40000;
        var requests = handler.CompletionRequests;
        try { await model.Analyze("Extract.", "Complete guide.", schema, 100, CancellationToken.None); throw new Exception("Context overflow accepted"); }
        catch (GuideContextException) { }
        Check(exchanges[^1].State == "Failed" && exchanges[^1].PromptTokens == 40000 && handler.CompletionRequests == requests, "Context overflow was not traced before rejecting inference");

        exchanges.Clear();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try { await model.Analyze("Extract.", "Complete guide.", schema, 100, canceled.Token); throw new Exception("Canceled inference accepted"); }
        catch (OperationCanceledException) { }
        Check(exchanges[^1].Error == "Request canceled.", "Caller cancellation was labeled as a model timeout");
    }

    private static async Task Deadlines()
    {
        var partial = "";
        using var stalled = new StalledStream();
        using var handler = new BodyHandler(stalled);
        using var client = Client(handler);
        try
        {
            using var result = await ForetellGuideLocalModel.PostJson(client, "v1/chat/completions", new { }, 4096, CancellationToken.None,
                response => partial = response, TimeSpan.FromMilliseconds(75));
            throw new Exception("Response body read escaped its deadline");
        }
        catch (TimeoutException error) { Check(error.Message.Contains("v1/chat/completions", StringComparison.Ordinal), "Body timeout omitted the endpoint"); }
        Check(stalled.Interrupted && partial == "{\"partial\":", "Timeout did not cancel body reading or retain partial output");

        using var callerStream = new StalledStream();
        using var callerHandler = new BodyHandler(callerStream);
        using var callerClient = Client(callerHandler);
        using var canceled = new CancellationTokenSource();
        canceled.CancelAfter(TimeSpan.FromMilliseconds(75));
        try
        {
            using var result = await ForetellGuideLocalModel.PostJson(callerClient, "tokenize", new { }, 4096, canceled.Token,
                _ => throw new InvalidOperationException("Trace failed."), TimeSpan.FromSeconds(5));
            throw new Exception("Body reading ignored caller cancellation");
        }
        catch (OperationCanceledException) { }
        Check(callerStream.Interrupted, "Caller cancellation did not reach the response stream");
    }

    private sealed class ModelHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = respond;
        public string CompletionRequest = "";
        public int CompletionRequests;
        public int TokenCount = 7;
        public bool FailTemplate;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/apply-template": return FailTemplate ? Respond(request) : Json("{\"prompt\":\"Full rendered prompt.\"}");
                case "/tokenize": return Json(JsonSerializer.Serialize(new { tokens = Enumerable.Repeat(42, TokenCount).ToArray() }));
                case "/v1/chat/completions":
                    CompletionRequest = await request.Content!.ReadAsStringAsync(cancellation);
                    ++CompletionRequests;
                    return Respond(request);
                default: throw new InvalidOperationException("Unexpected endpoint.");
            }
        }
    }

    private sealed class BodyHandler(Stream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
    }

    private sealed class StalledStream : Stream
    {
        private bool _prefixSent;
        public bool Interrupted;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default)
        {
            if (!_prefixSent)
            {
                _prefixSent = true;
                var prefix = Encoding.UTF8.GetBytes("{\"partial\":");
                prefix.CopyTo(buffer);
                return prefix.Length;
            }
            try { await Task.Delay(Timeout.Infinite, cancellation); }
            catch (OperationCanceledException) { Interrupted = true; throw; }
            return 0;
        }
    }
}
