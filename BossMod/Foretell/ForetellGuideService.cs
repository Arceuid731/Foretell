using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;

namespace BossMod.Foretell;

internal enum GuideState { Idle, ReadingCache, Downloading, Preparing, Ready, Offline, Failed }
internal sealed record GuideSnapshot(GuideDuty? Duty, GuideState State, GuideDocument? Document = null, string Error = "")
{
    public DateTime StartedAt { get; init; }
    public double ElapsedSeconds { get; init; }
    public double? EstimatedSeconds { get; init; }
    public long ReceivedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public bool FromCache { get; init; }
}

internal sealed class ForetellWikiSource : IDisposable
{
    private readonly HttpClient _http;
    public ForetellWikiSource(HttpMessageHandler? handler = null)
    {
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All });
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Foretell/0.11 (+https://github.com/Arceuid731/Foretell)");
    }

    public Task<string> Fetch(GuideDuty duty, CancellationToken cancellation) => Fetch(duty, cancellation, null);

    public async Task<string> Fetch(GuideDuty duty, CancellationToken cancellation, Action<long, long?>? progress)
    {
        if (!duty.Valid) throw new InvalidDataException("Instance identity is missing.");
        var title = char.ToUpperInvariant(duty.EnglishName[0]) + duty.EnglishName[1..];
        var url = "https://ffxiv.consolegameswiki.com/mediawiki/api.php?action=parse&prop=text%7Crevid&format=json&formatversion=2&redirects=1&page=" + Uri.EscapeDataString(title);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != "application/json") throw new InvalidDataException("Wiki did not return JSON.");
        if (response.Content.Headers.ContentLength > ForetellGuideParser.MaxResponseBytes) throw new InvalidDataException("Wiki response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellation).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > ForetellGuideParser.MaxResponseBytes) throw new InvalidDataException("Wiki response is too large.");
            buffer.Write(chunk, 0, read);
            progress?.Invoke(buffer.Length, response.Content.Headers.ContentLength);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public void Dispose() => _http.Dispose();
}

internal sealed class ForetellGuideCache(string directory)
{
    private sealed record Envelope(string Payload, string Hash);
    private const int MaxCacheBytes = 24 * 1024 * 1024;
    private string FilePath(GuideDuty duty) => Path.Combine(directory, duty.Key + ".json");

    public GuideDocument? Read(GuideDuty duty)
    {
        try
        {
            var path = FilePath(duty);
            if (!File.Exists(path) || new FileInfo(path).Length > MaxCacheBytes) return null;
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path));
            if (envelope == null || envelope.Payload == null || GuideNames.Hash(envelope.Payload) != envelope.Hash) return null;
            var document = JsonSerializer.Deserialize<GuideDocument>(envelope.Payload);
            if (document == null || document.Schema != GuideDocument.CurrentSchema || document.Duty != duty || document.Revision <= 0
                || GuideNames.Normalize(document.Title) != GuideNames.Normalize(duty.EnglishName)
                || document.SourceHash is not { Length: 64 }
                || document.Bosses is not { Length: <= 32 } || document.Bosses.Length == 0 && document.Page == null || document.MechanicCount > 512
                || document.RetrievedAt > DateTime.UtcNow.AddMinutes(5)) return null;
            if (document.Page is { } page && (page.Text.Length < 20 || page.Text.Length > GuideSourceAssembly.MaximumBytes
                || page.Html.Length > GuideSourceAssembly.MaximumBytes || GuideNames.Hash(page.Html) != document.SourceHash
                || !Uri.TryCreate(page.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)) return null;
            if (document.Sources == null || document.Providers == null) return null;
            if (document.Sources.Length > 0)
            {
                var rebuilt = GuideSourceAssembly.Combine(duty, new(document.Sources, document.Providers), document.RetrievedAt);
                if (rebuilt.SourceHash != document.SourceHash || rebuilt.Page != document.Page) return null;
            }
            foreach (var boss in document.Bosses)
            {
                if (boss.Name.Length is 0 or > 200 || boss.Phases.Length is 0 or > 64) return null;
                foreach (var phase in boss.Phases)
                    if (phase.Context.Length > 100000 || string.IsNullOrWhiteSpace(phase.Context) && phase.Mechanics.Length == 0
                        || phase.Mechanics.Any(mechanic => mechanic.Name.Length is 0 or > 120 || mechanic.Text.Length > 24000)) return null;
            }
            return document;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException)
        {
            return null;
        }
    }

    public void Write(GuideDocument document)
    {
        var payload = JsonSerializer.Serialize(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(payload, GuideNames.Hash(payload)));
        if (bytes.Length > MaxCacheBytes) throw new InvalidDataException("Prepared guide exceeds cache limits.");
        Directory.CreateDirectory(directory);
        var path = FilePath(document.Duty);
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        var files = new DirectoryInfo(directory).GetFiles("C*-T*.json")
            .Where(file => System.Text.RegularExpressions.Regex.IsMatch(file.Name, @"^C\d+-T\d+\.json$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            .OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
        long retained = 0;
        for (var index = 0; index < files.Length; ++index)
        {
            retained += files[index].Length;
            if ((index >= 128 || retained > 64L * 1024 * 1024) && files[index].FullName != path) files[index].Delete();
        }
    }
}

internal sealed class ForetellGuideService : IDisposable
{
    private sealed record Request(long Serial, GuideDuty Duty, bool Refresh, CancellationTokenSource Cancellation)
    {
        public DateTime StartedAt { get; } = DateTime.UtcNow;
        public System.Diagnostics.Stopwatch Watch { get; } = System.Diagnostics.Stopwatch.StartNew();
        public bool FromCache;
    }
    private readonly object _gate = new();
    private readonly Channel<Request> _queue = Channel.CreateBounded<Request>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ForetellGuideCache _cache;
    private readonly Func<GuideDuty, CancellationToken, Task<string>> _fetch;
    private readonly ForetellGuideProviders? _sources;
    private readonly Task _worker;
    private Request? _latest;
    private volatile GuideSnapshot _snapshot = new(null, GuideState.Idle);
    private long _serial;
    private bool _disposed;
    private readonly GuideTimingHistory _timings;
    public GuideSnapshot Snapshot => _snapshot;
    internal Task Completion => _worker;

    public ForetellGuideService(string directory, Func<GuideDuty, CancellationToken, Task<string>>? fetch = null)
    {
        _cache = new(directory);
        _timings = new(directory);
        if (fetch == null) _sources = new(Path.Combine(directory, "providers"));
        _fetch = fetch ?? ((_, _) => throw new InvalidOperationException("Provider service is not initialized."));
        _worker = Task.Run(Run);
    }

    public void RequestGuide(GuideDuty duty, bool refresh = false)
    {
        if (!duty.Valid) return;
        lock (_gate)
        {
            if (_disposed) return;
            _latest?.Cancellation.Cancel();
            if (_queue.Reader.TryRead(out var dropped)) dropped.Cancellation.Dispose();
            var request = new Request(++_serial, duty, refresh, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
            _latest = request;
            _snapshot = new(duty, GuideState.ReadingCache) { StartedAt = request.StartedAt };
            _queue.Writer.TryWrite(request);
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_disposed) return;
            ++_serial;
            _latest?.Cancellation.Cancel();
            _snapshot = new(null, GuideState.Idle);
        }
    }

    private void Publish(Request request, GuideState state, GuideDocument? document = null, string error = "")
    {
        lock (_gate)
            if (!_disposed && request.Serial == _serial)
                _snapshot = new(request.Duty, state, document, error)
                {
                    StartedAt = request.StartedAt, ElapsedSeconds = request.Watch.Elapsed.TotalSeconds,
                    EstimatedSeconds = request.FromCache ? null : _timings.Estimate,
                    FromCache = request.FromCache
                };
    }

    private async Task Run()
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                GuideDocument? document = null;
                try
                {
                    request.Cancellation.Token.ThrowIfCancellationRequested();
                    document = _cache.Read(request.Duty);
                    if (document?.Page == null) document = null;
                    request.FromCache = document != null;
                    if (document != null) Publish(request, GuideState.Ready, document);
                    if (document == null || request.Refresh || _sources != null || DateTime.UtcNow - document.RetrievedAt > TimeSpan.FromDays(7))
                    {
                        request.FromCache = false;
                        Publish(request, GuideState.Downloading, document);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(request.Cancellation.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(25));
                        var fetched = _sources != null
                            ? GuideSourceAssembly.Combine(request.Duty, await _sources.Fetch(request.Duty, timeout.Token).ConfigureAwait(false), DateTime.UtcNow)
                            : ForetellGuideParser.ReadPage(await _fetch(request.Duty, timeout.Token).ConfigureAwait(false), request.Duty, DateTime.UtcNow);
                        timeout.Token.ThrowIfCancellationRequested();
                        Publish(request, GuideState.Preparing, document);
                        document = fetched;
                        _timings.Record(request.Watch.Elapsed.TotalSeconds);
                        timeout.Token.ThrowIfCancellationRequested();
                        try { _cache.Write(document); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                        {
                            Publish(request, GuideState.Ready, document, "CacheWrite: " + error.GetType().Name); continue;
                        }
                    }
                    Publish(request, GuideState.Ready, document);
                }
                catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested) { }
                catch (Exception error)
                {
                    var detail = error is InvalidDataException ? error.GetType().Name + ": " + error.Message : error.GetType().Name;
                    Publish(request, document == null ? GuideState.Failed : GuideState.Offline, document, detail[..Math.Min(detail.Length, 500)]);
                }
                finally
                {
                    lock (_gate)
                    {
                        if (_latest == request) _latest = null;
                        request.Cancellation.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { _sources?.Dispose(); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _lifetime.Cancel(); _queue.Writer.TryComplete();
            if (_queue.Reader.TryRead(out var dropped)) { if (_latest == dropped) _latest = null; dropped.Cancellation.Dispose(); }
        }
    }
}
