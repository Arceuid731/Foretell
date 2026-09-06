using BossMod.Foretell;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class GuideProviderTests
{
    private static readonly GuideDuty Duty = new(824, 826, "The Orbonne Monastery");
    private static readonly DateTime Modified = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string GuideHtml = "<h2>Encounter reminders</h2><p>Move behind the boss when the front attack begins.</p><img alt='Stack marker' title='Group together'><i class='fa fa-shield-alt' aria-label='Tank'></i>";
    private const string CatalogHtml = "<a href='/tldrguide/orbonne-monastery-the/'>Orbonne Monastery, The</a><a href='/tldrguide/orbonne-monastery-the-hard/'>Orbonne Monastery, The (Hard)</a>";

    public static void Run()
    {
        CatalogMatching();
        HtmlLabels();
        IndependentProviders().GetAwaiter().GetResult();
        EmptyAndWrongWiki().GetAwaiter().GetResult();
        ConditionalAndFallback().GetAwaiter().GetResult();
        SharedDownloads().GetAwaiter().GetResult();
        ExplicitRefresh().GetAwaiter().GetResult();
        Cancellation().GetAwaiter().GetResult();
        Redirects().GetAwaiter().GetResult();
        ByteBounds().GetAwaiter().GetResult();
        DiskIntegrityAndBounds().GetAwaiter().GetResult();
        Console.WriteLine("Guide providers: independent failures, catalog variants, conditional refresh, verified cache fallback, cancellation, redirects and byte bounds passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static GuideProviderState State(GuideSourceBundle bundle, string provider) => bundle.States.Single(state => state.Provider == provider);
    private static GuideSourcePage Page(GuideSourceBundle bundle, string provider) => bundle.Sources.Single(page => page.Provider == provider);
    private static bool Wiki(HttpRequestMessage request) => request.RequestUri!.Host == "ffxiv.consolegameswiki.com";
    private static bool Gamer(HttpRequestMessage request) => request.RequestUri!.Host == "ffxiv.gamerescape.com";
    private static bool Index(HttpRequestMessage request) => request.RequestUri!.AbsoluteUri == ForetellGuideProviders.RavenIndex;
    private static bool Workbook(HttpRequestMessage request) => request.RequestUri!.Host == "docs.google.com";
    private static HttpResponseMessage Status(HttpStatusCode status) => new(status);
    private static HttpResponseMessage Text(string text, string format = "text/html")
        => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, format) };
    private static string WikiJson(string html = GuideHtml, string title = "The Orbonne Monastery")
        => JsonSerializer.Serialize(new { parse = new { title, revid = 42, text = html } });
    private static HttpResponseMessage WikiResponse() => Text(WikiJson(), "application/json");

    private static HttpResponseMessage Validated(HttpResponseMessage response)
    {
        response.Headers.ETag = new EntityTagHeaderValue("\"guide-version-1\"");
        response.Content.Headers.LastModified = Modified;
        return response;
    }

    private static void CheckConditional(HttpRequestMessage request)
    {
        Check(request.Headers.IfNoneMatch.SingleOrDefault()?.Tag == "\"guide-version-1\"", "ETag was not sent on refresh.");
        Check(request.Headers.IfModifiedSince?.UtcDateTime == Modified, "Last-Modified was not sent on refresh.");
    }

    private static void CatalogMatching()
    {
        var ordinary = ForetellGuideProviders.MatchRavenGuide(CatalogHtml, Duty);
        Check(ordinary == "https://ravensreminders.com/tldrguide/orbonne-monastery-the/", "Catalog trailing article did not match the official title.");
        Check(ForetellGuideProviders.MatchRavenGuide(CatalogHtml, Duty with { EnglishName = Duty.EnglishName + " (Hard)" })?.EndsWith("-hard/", StringComparison.Ordinal) == true,
            "Catalog dropped the requested variant.");
        Check(ForetellGuideProviders.MatchRavenGuide(CatalogHtml, Duty with { EnglishName = Duty.EnglishName + " (Extreme)" }) == null, "Catalog substituted a different variant.");
        Check(ForetellGuideProviders.MatchRavenGuide(CatalogHtml + "<a href='/tldrguide/other/'>The Orbonne Monastery</a>", Duty) == null, "Ambiguous catalog links were accepted.");
        Check(ForetellGuideProviders.MatchRavenGuide(CatalogHtml + CatalogHtml, Duty) == ordinary, "Duplicate links to the same guide became ambiguous.");
        Check(ForetellGuideProviders.MatchRavenGuide("<!--" + CatalogHtml + "--><script>" + CatalogHtml + "</script>", Duty) == null, "Catalog used commented-out or scripted links.");
        foreach (var href in new[] { "https://ravensreminders.com.evil.example/tldrguide/other/", "http://ravensreminders.com/tldrguide/other/", "/resources/", "/tldrguide/other/?variant=hard" })
            Check(ForetellGuideProviders.MatchRavenGuide($"<a href='{href}'>The Orbonne Monastery</a>", Duty) == null, "Unsafe or non-guide catalog link was accepted.");
        Check(ForetellGuideProviders.MatchRavenGuide("<a href='/tldrguide/eye-hard/'>Howling Eye, The (Hard)</a>", Duty with { EnglishName = "The Howling Eye (Hard)" }) != null,
            "Trailing article before variant was not normalized.");
    }

    private static void HtmlLabels()
    {
        var text = ForetellGuideProviders.HtmlText(GuideHtml + "<script>SECRET SCRIPT</script><style>SECRET STYLE</style><iframe>SECRET FRAME</iframe>");
        foreach (var expected in new[] { "Encounter reminders", "Move behind", "Stack marker", "Group together", "Tank", "icon: shield-alt" })
            Check(text.Contains(expected, StringComparison.Ordinal), "HTML source lost heading, image label or icon semantics: " + expected);
        Check(!text.Contains("SECRET", StringComparison.Ordinal), "HTML extraction included ignored executable or styling content.");
    }

    private static async Task IndependentProviders()
    {
        using var directory = new TestDirectory();
        var started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeHandler(async (request, cancellation) =>
        {
            if (Interlocked.Increment(ref started) == 4) allStarted.TrySetResult();
            await allStarted.Task.WaitAsync(cancellation);
            if (Wiki(request)) return WikiResponse();
            if (Workbook(request)) return WorkbookResponse();
            return Status(HttpStatusCode.Forbidden);
        });
        using var providers = new ForetellGuideProviders(directory.Path, handler);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bundle = await providers.Fetch(Duty, timeout.Token);
        Check(started == 4 && bundle.States.Length == 4 && bundle.Sources.Length == 2, "A failed provider blocked independent concurrent sources.");
        Check(State(bundle, ForetellGuideProviders.GamerProvider).Status == "Unavailable" && State(bundle, ForetellGuideProviders.RavenProvider).Status == "Unavailable", "403 provider state was incorrect.");
        Check(State(bundle, ForetellGuideProviders.WikiProvider).Status == "Ready" && State(bundle, ForetellGuideProviders.WorkbookProvider).Status == "Ready", "Successful providers were not ready.");
        Check(Page(bundle, ForetellGuideProviders.WikiProvider).Original == GuideHtml, "Original HTML was modified or clipped.");
        Check(Page(bundle, ForetellGuideProviders.WorkbookProvider).Text.Contains("Move behind", StringComparison.Ordinal), "Workbook provider did not call the workbook reader.");
    }

    private static async Task EmptyAndWrongWiki()
    {
        using var directory = new TestDirectory();
        var wrongVariant = false;
        using var handler = new FakeHandler((request, _) => Task.FromResult(
            Wiki(request) ? Text(WikiJson(wrongVariant ? GuideHtml : "<h2></h2><p> </p>", wrongVariant ? Duty.EnglishName + " (Hard)" : Duty.EnglishName), "application/json")
            : Gamer(request) ? Text("{\"error\":{\"code\":\"missingtitle\"}}", "application/json") : Status(HttpStatusCode.NotFound)));
        using var providers = new ForetellGuideProviders(directory.Path, handler);
        var empty = await providers.Fetch(Duty, CancellationToken.None);
        Check(empty.Sources.Length == 0 && State(empty, ForetellGuideProviders.WikiProvider).Status == "Missing", "Empty wiki invented guide text.");
        Check(State(empty, ForetellGuideProviders.GamerProvider).Status == "Missing", "MediaWiki missingtitle was not Missing.");
        wrongVariant = true;
        var wrong = await providers.Fetch(Duty, CancellationToken.None);
        Check(wrong.Sources.Length == 0 && State(wrong, ForetellGuideProviders.WikiProvider).Status == "Unavailable", "Wrong wiki variant was accepted.");
    }

    private static async Task ConditionalAndFallback()
    {
        using var directory = new TestDirectory();
        var phase = 0;
        var indexRequests = 0;
        using var handler = new FakeHandler((request, _) =>
        {
            if (Index(request)) { ++indexRequests; return Task.FromResult(Validated(Text(CatalogHtml))); }
            if (!Wiki(request) && request.RequestUri!.Host != "ravensreminders.com") return Task.FromResult(Status(HttpStatusCode.NotFound));
            if (phase == 0) return Task.FromResult(Validated(Wiki(request) ? WikiResponse() : Text(GuideHtml)));
            CheckConditional(request);
            if (phase == 1) return Task.FromResult(Status(HttpStatusCode.NotModified));
            if (Wiki(request)) throw new HttpRequestException("Offline fixture");
            return Task.FromResult(Status(HttpStatusCode.Forbidden));
        });
        GuideSourceBundle original;
        using (var providers = new ForetellGuideProviders(directory.Path, handler))
        {
            original = await providers.Fetch(Duty, CancellationToken.None);
            phase = 1;
            var refreshed = await providers.Fetch(Duty, CancellationToken.None);
            Check(indexRequests == 1 && refreshed.Sources.Length == 2, "Index was downloaded twice within its ten-minute cache lifetime.");
            Check(refreshed.Sources.All(page => page.FromCache) && refreshed.States.Where(state => state.Status != "Missing").All(state => state.Status == "Cached" && state.Error.Length == 0), "304 did not reuse verified cached sources.");
            foreach (var page in refreshed.Sources)
                Check(page.Fingerprint == Page(original, page.Provider).Fingerprint && page.ModifiedAt == Modified, "304 changed content identity or modification time.");
        }
        phase = 2;
        using var restored = new ForetellGuideProviders(directory.Path, handler);
        var fallback = await restored.Fetch(Duty, CancellationToken.None);
        Check(fallback.Sources.Length == 2 && fallback.Sources.All(page => page.FromCache), "Network failure did not reuse persisted source bytes.");
        foreach (var page in fallback.Sources)
        {
            Check(page.Fingerprint == Page(original, page.Provider).Fingerprint, "Network diagnostics or timestamps contaminated cached content.");
            Check(State(fallback, page.Provider).Status == "Cached" && State(fallback, page.Provider).Error.Length != 0, "Cached fallback lost its diagnostic error.");
        }
    }

    private static async Task SharedDownloads()
    {
        using var directory = new TestDirectory();
        var phase = 0;
        var counts = new ConcurrentDictionary<string, int>();
        using var handler = new FakeHandler(async (request, cancellation) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            counts.AddOrUpdate(url, 1, (_, count) => count + 1);
            if (Index(request) || Workbook(request))
            {
                if (phase == 1) { CheckConditional(request); return Status(HttpStatusCode.NotModified); }
                await Task.Delay(25, cancellation);
                return Validated(Index(request) ? Text(CatalogHtml) : WorkbookResponse());
            }
            return Status(HttpStatusCode.NotFound);
        });
        using var first = new ForetellGuideProviders(directory.Path, handler);
        using var second = new ForetellGuideProviders(directory.Path, handler);
        var bundles = await Task.WhenAll(first.Fetch(Duty, CancellationToken.None), second.Fetch(Duty with { ContentID = 1, TerritoryID = 2, EnglishName = "Sastasha" }, CancellationToken.None));
        Check(counts[ForetellGuideProviders.RavenIndex] == 1 && counts[ForetellGuideProviders.WorkbookUrl] == 1, "Concurrent instance requests duplicated global downloads.");
        Check(State(bundles[1], ForetellGuideProviders.WorkbookProvider).Status == "Missing", "Cached workbook reused the previous duty's worksheet.");
        foreach (var path in Directory.GetFiles(directory.Cache, "*.json"))
        {
            var entry = JsonNode.Parse(File.ReadAllText(path))!;
            entry["RetrievedAt"] = DateTime.UtcNow.AddMinutes(-11);
            entry["CheckedAt"] = DateTime.UtcNow.AddMinutes(-11);
            File.WriteAllText(path, entry.ToJsonString());
        }
        phase = 1;
        var refreshed = await first.Fetch(Duty, CancellationToken.None);
        Check(counts[ForetellGuideProviders.RavenIndex] == 2 && counts[ForetellGuideProviders.WorkbookUrl] == 2, "Expired global cache did not refresh conditionally.");
        Check(State(refreshed, ForetellGuideProviders.WorkbookProvider) is { Status: "Cached", Error.Length: 0 }, "Workbook 304 was not reused.");
    }

    private static async Task ExplicitRefresh()
    {
        using var directory = new TestDirectory();
        var refresh = false;
        var sharedRequests = 0;
        using var handler = new FakeHandler((request, _) =>
        {
            if (!Index(request) && !Workbook(request)) return Task.FromResult(Status(HttpStatusCode.NotFound));
            Interlocked.Increment(ref sharedRequests);
            if (refresh)
            {
                CheckConditional(request);
                return Task.FromResult(Status(HttpStatusCode.NotModified));
            }
            return Task.FromResult(Validated(Index(request) ? Text(CatalogHtml) : WorkbookResponse()));
        });
        using var providers = new ForetellGuideProviders(directory.Path, handler);
        var original = await providers.Fetch(Duty, CancellationToken.None);
        refresh = true;
        await providers.Fetch(Duty, CancellationToken.None);
        Check(sharedRequests == 2, "Ordinary entry ignored the shared provider cache lifetime.");
        var refreshed = await providers.Fetch(Duty, CancellationToken.None, true);
        Check(sharedRequests == 4 && Page(refreshed, ForetellGuideProviders.WorkbookProvider).ContentFingerprint
            == Page(original, ForetellGuideProviders.WorkbookProvider).ContentFingerprint, "Explicit refresh skipped conditional provider checks or changed unchanged workbook identity.");
    }

    private static async Task Cancellation()
    {
        using var directory = new TestDirectory();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = false;
        var requests = 0;
        using var handler = new FakeHandler(async (request, cancellation) =>
        {
            Interlocked.Increment(ref requests);
            if (block)
            {
                waiting.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellation);
            }
            return Wiki(request) ? WikiResponse() : Status(HttpStatusCode.NotFound);
        });
        using var providers = new ForetellGuideProviders(directory.Path, handler);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try { await providers.Fetch(Duty, canceled.Token); throw new Exception("Already-canceled request completed."); }
        catch (OperationCanceledException) { }
        Check(requests == 0, "Already-canceled request touched the network.");
        await providers.Fetch(Duty, CancellationToken.None);
        block = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = providers.Fetch(Duty, cancellation.Token);
        await waiting.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        try { await pending; throw new Exception("Caller cancellation became cached fallback."); }
        catch (OperationCanceledException) { }
    }

    private static async Task Redirects()
    {
        using var directory = new TestDirectory();
        var forbiddenRequests = 0;
        using var handler = new FakeHandler((request, _) =>
        {
            if (Wiki(request)) return Task.FromResult(Redirect("http://ffxiv.consolegameswiki.com/insecure"));
            if (Gamer(request)) return Task.FromResult(Redirect("https://ffxiv.consolegameswiki.com/wrong-provider"));
            if (Index(request)) return Task.FromResult(Text(CatalogHtml));
            if (request.RequestUri!.Host == "ravensreminders.com") return Task.FromResult(Redirect("https://ravensreminders.com/tldrguide/wrong-variant/"));
            if (Workbook(request)) return Task.FromResult(Redirect("https://doc-0g-6c-sheets.googleusercontent.com/export/workbook"));
            if (request.RequestUri.Host == "doc-0g-6c-sheets.googleusercontent.com") return Task.FromResult(WorkbookResponse());
            ++forbiddenRequests;
            return Task.FromResult(Status(HttpStatusCode.OK));
        });
        using var providers = new ForetellGuideProviders(directory.Path, handler);
        var bundle = await providers.Fetch(Duty, CancellationToken.None);
        Check(forbiddenRequests == 0 && bundle.Sources.Length == 1 && bundle.Sources[0].Provider == ForetellGuideProviders.WorkbookProvider, "Redirect allowlist or Raven identity check failed.");
        Check(bundle.States.Count(state => state.Status == "Unavailable") == 3, "Rejected redirects were not independent provider failures.");
    }

    private static HttpResponseMessage Redirect(string url)
    {
        var response = Status(HttpStatusCode.TemporaryRedirect);
        response.Headers.Location = new Uri(url);
        return response;
    }

    private static async Task ByteBounds()
    {
        for (var mode = 0; mode < 3; ++mode)
        {
            using var directory = new TestDirectory();
            using var handler = new FakeHandler((request, _) =>
            {
                if (!Wiki(request) && !Workbook(request)) return Task.FromResult(Status(HttpStatusCode.NotFound));
                var limit = Wiki(request) ? ForetellGuideProviders.MaximumPageBytes : ForetellGuideProviders.MaximumWorkbookBytes;
                if (mode == 2) return Task.FromResult(Wiki(request) ? Text(WikiJson().PadRight(limit), "application/json") : WorkbookResponse());
                var response = mode == 0 ? WikiResponse() : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(new byte[limit + 1])) };
                response.Content.Headers.ContentType = new(Wiki(request) ? "application/json" : "application/octet-stream");
                if (mode == 0) response.Content.Headers.ContentLength = limit + 1;
                return Task.FromResult(response);
            });
            using var providers = new ForetellGuideProviders(directory.Path, handler);
            var bundle = await providers.Fetch(Duty, CancellationToken.None);
            if (mode == 2) Check(bundle.Sources.Length == 2, "Response at the exact byte boundary was rejected.");
            else Check(bundle.Sources.Length == 0 && State(bundle, ForetellGuideProviders.WikiProvider).Error.Contains("byte limits", StringComparison.Ordinal)
                && State(bundle, ForetellGuideProviders.WorkbookProvider).Error.Contains("byte limits", StringComparison.Ordinal), "Declared or streamed response exceeded the byte bounds.");
        }
    }

    private static async Task DiskIntegrityAndBounds()
    {
        using var directory = new TestDirectory();
        var offline = false;
        using var handler = new FakeHandler((request, _) => Task.FromResult(Wiki(request) && !offline ? WikiResponse() : Status(HttpStatusCode.ServiceUnavailable)));
        using var providers = new ForetellGuideProviders(directory.Path, handler);
        await providers.Fetch(Duty, CancellationToken.None);
        var cached = Directory.GetFiles(directory.Cache, "*.json").Single();
        var entry = JsonNode.Parse(File.ReadAllText(cached))!;
        entry["Bytes"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(WikiJson("<p>Corrupted but otherwise plausible strategy.</p>")));
        File.WriteAllText(cached, entry.ToJsonString());
        offline = true;
        Check((await providers.Fetch(Duty, CancellationToken.None)).Sources.Length == 0, "Unverified cached bytes were reused after hash corruption.");
        await using (var oversized = File.Create(cached)) oversized.SetLength(ForetellGuideProviders.MaximumDiskBytes);
        Check((await providers.Fetch(Duty, CancellationToken.None)).Sources.Length == 0, "Oversized cache entry was read as a valid fallback.");
        offline = false;
        Check((await providers.Fetch(Duty, CancellationToken.None)).Sources.Length == 1, "Oversized cache prevented fresh network recovery.");
        Check(new DirectoryInfo(directory.Cache).GetFiles().Sum(file => file.Length) <= ForetellGuideProviders.MaximumDiskBytes, "Disk cache byte budget was not enforced.");
        for (var index = 0; index <= ForetellGuideProviders.MaximumCacheEntries; ++index)
            File.WriteAllText(System.IO.Path.Combine(directory.Cache, index.ToString("X64") + ".json"), "{}");
        await providers.Fetch(Duty, CancellationToken.None);
        Check(Directory.GetFiles(directory.Cache, "*.json").Length <= ForetellGuideProviders.MaximumCacheEntries, "Disk cache entry budget was not enforced.");
    }

    private static HttpResponseMessage WorkbookResponse()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            var parts = new Dictionary<string, string>
            {
                ["xl/workbook.xml"] = "<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='" + SecurityElement.Escape(Duty.EnglishName) + "' sheetId='1' r:id='rId1'/></sheets></workbook>",
                ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet1.xml'/></Relationships>",
                ["xl/worksheets/sheet1.xml"] = "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetData><row r='1'><c r='A1' t='inlineStr'><is><t>" + SecurityElement.Escape(Duty.EnglishName) + "</t></is></c></row><row r='2'><c r='A2' t='inlineStr'><is><t>Move behind the boss when the front attack begins.</t></is></c></row></sheetData></worksheet>"
            };
            foreach (var part in parts)
            {
                using var writer = new StreamWriter(zip.CreateEntry(part.Key).Open(), Encoding.UTF8);
                writer.Write(part.Value);
            }
        }
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(output.ToArray()) };
        response.Content.Headers.ContentType = new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        return response;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) => respond(request, cancellation);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes, false)
    {
        public override bool CanSeek => false;
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foretell-guide-providers-" + Guid.NewGuid().ToString("N"));
        public string Cache => System.IO.Path.Combine(Path, "http-sources-v1");
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            var temporary = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            if (resolved.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
