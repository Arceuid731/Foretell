using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace BossMod.Foretell;

internal sealed class ForetellGuideProviders : IDisposable
{
    internal const string WikiProvider = "Console Games Wiki";
    internal const string GamerProvider = "Gamer Escape";
    internal const string RavenProvider = "Raven's Reminders";
    internal const string WorkbookProvider = "Community Workbook";
    internal const string RavenIndex = "https://ravensreminders.com/tldr-guides/";
    internal const string WorkbookUrl = "https://docs.google.com/spreadsheets/d/1MX0RjPS4gtT6YI5Szxlsin9hcaohnEQQC7zNdrDHBrQ/export?format=xlsx";
    internal const int MaximumPageBytes = ForetellGuideParser.MaxResponseBytes;
    internal const int MaximumWorkbookBytes = ForetellWorkbookGuide.MaximumArchiveBytes;
    internal const long MaximumDiskBytes = 64 * 1024 * 1024;
    internal const int MaximumCacheEntries = 256;
    private const int MaximumMetadataBytes = 16384;
    private static readonly TimeSpan SharedLifetime = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim DiskGate = new(1, 1);
    private static readonly SemaphoreSlim IndexGate = new(1, 1);
    private static readonly SemaphoreSlim WorkbookGate = new(1, 1);
    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(250);
    private const string AttributesPattern = "(?:[^>\"']|\"[^\"]*\"|'[^']*')*";
    private static readonly Regex Tags = new("<(?<tag>[a-z][a-z0-9]*)\\b(?<attributes>" + AttributesPattern + ")>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex Attributes = new("(?<name>[a-z_:][a-z0-9_:.-]*)\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s>]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex Links = new("<a\\b(?<attributes>" + AttributesPattern + ")>(?<body>[\\s\\S]*?)</a\\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex IgnoredHtml = new(@"<!--[\s\S]*?-->|<(script|style|iframe|sup)\b[^>]*>[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex Article = new(@"^(?<name>.+?),\s*(?<article>the|an|a)(?<suffix>\s*[:(].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex GuidePath = new(@"^/tldrguide/[a-z0-9-]+/$", RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex CacheName = new(@"^[A-F0-9]{64}\.json$", RegexOptions.CultureInvariant, RegexBudget);
    private readonly string _directory;
    private readonly HttpClient _http;

    private sealed record CacheEntry(string Url, string FinalUrl, string MediaType, byte[] Bytes, string Hash, string ETag,
        DateTime? ModifiedAt, DateTime RetrievedAt, DateTime CheckedAt);
    private sealed record Download<T>(T Value, CacheEntry Entry, bool FromCache, string Error = "");
    private sealed record ProviderResult(GuideSourcePage? Page, GuideProviderState State);
    private sealed class MissingSourceException(string message) : Exception(message);

    public ForetellGuideProviders(string directory, HttpMessageHandler? handler = null)
    {
        _directory = Path.GetFullPath(Path.Combine(directory, "http-sources-v1"));
        _http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, UseCookies = false });
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Foretell/0.11 (+https://github.com/Arceuid731/Foretell)");
    }

    public async Task<GuideSourceBundle> Fetch(GuideDuty duty, CancellationToken cancellation, bool refresh = false)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!duty.Valid || string.IsNullOrWhiteSpace(duty.EnglishName)) throw new InvalidDataException("Instance identity is missing.");
        var title = char.ToUpperInvariant(duty.EnglishName[0]) + duty.EnglishName[1..];
        var wikiUrl = WikiUrl("https://ffxiv.consolegameswiki.com/mediawiki/api.php", title);
        var gamerUrl = WikiUrl("https://ffxiv.gamerescape.com/w/api.php", title);
        var results = await Task.WhenAll(
            Isolated(WikiProvider, wikiUrl, () => Wiki(WikiProvider, wikiUrl, duty, cancellation), cancellation),
            Isolated(GamerProvider, gamerUrl, () => Wiki(GamerProvider, gamerUrl, duty, cancellation), cancellation),
            Isolated(RavenProvider, RavenIndex, () => Raven(duty, cancellation, refresh), cancellation),
            Isolated(WorkbookProvider, WorkbookUrl, () => Workbook(duty, cancellation, refresh), cancellation)).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return new(results.Where(result => result.Page != null).Select(result => result.Page!).ToArray(), results.Select(result => result.State).ToArray());
    }

    private static string WikiUrl(string endpoint, string title)
        => endpoint + "?action=parse&prop=text%7Crevid&format=json&formatversion=2&redirects=1&page=" + Uri.EscapeDataString(title);

    private static async Task<ProviderResult> Isolated(string provider, string url, Func<Task<ProviderResult>> fetch, CancellationToken cancellation)
    {
        try { return await fetch().ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var missing = error is MissingSourceException || error is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone };
            return new(null, new(provider, missing ? "Missing" : "Unavailable", url, Diagnostic(error)));
        }
    }

    private async Task<ProviderResult> Wiki(string provider, string url, GuideDuty duty, CancellationToken cancellation)
    {
        var download = await Read(url, MaximumPageBytes, TimeSpan.Zero, "json", bytes =>
        {
            var response = Encoding.UTF8.GetString(bytes);
            using var json = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 32 });
            if (json.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var code)
                && code.GetString() is "missingtitle" or "invalidtitle") throw new MissingSourceException("Wiki page is missing.");
            if (json.RootElement.TryGetProperty("parse", out var parsed) && parsed.TryGetProperty("text", out var content)
                && content.ValueKind == JsonValueKind.String && HtmlText(content.GetString() ?? "").Length < 20)
                throw new MissingSourceException("Wiki page contains no usable text.");
            var document = ForetellGuideParser.ReadPage(response, duty, DateTime.UtcNow);
            var page = document.Page ?? throw new MissingSourceException("Wiki page contains no usable text.");
            var sourceUrl = provider == WikiProvider ? page.Url
                : $"https://ffxiv.gamerescape.com/w/index.php?title={Uri.EscapeDataString(document.Title)}&oldid={document.Revision}";
            return (GuideSourcePage?)new GuideSourcePage(provider, sourceUrl, HtmlText(page.Html), page.Html, "html");
        }, cancellation).ConfigureAwait(false);
        return Result(provider, download);
    }

    private async Task<ProviderResult> Raven(GuideDuty duty, CancellationToken cancellation, bool refresh)
    {
        var index = await Read(RavenIndex, MaximumPageBytes, SharedLifetime, "html", bytes =>
        {
            var html = Encoding.UTF8.GetString(bytes);
            if (!Catalog(html).Any()) throw new InvalidDataException("Raven guide catalog contains no guide links.");
            return html;
        }, cancellation, refresh).ConfigureAwait(false);
        var url = MatchRavenGuide(index.Value, duty);
        if (url == null) return new(null, new(RavenProvider, "Missing", RavenIndex, "No unique catalog entry matches this duty and variant."));
        return await Isolated(RavenProvider, url, async () =>
        {
            var download = await Read(url, MaximumPageBytes, TimeSpan.Zero, "html", bytes =>
            {
                var html = Encoding.UTF8.GetString(bytes);
                var text = HtmlText(html);
                if (text.Length < 20) throw new MissingSourceException("Raven guide contains no usable text.");
                return (GuideSourcePage?)new GuideSourcePage(RavenProvider, url, text, html, "html");
            }, cancellation).ConfigureAwait(false);
            var result = Result(RavenProvider, download);
            if (index.Error.Length != 0)
                result = new(result.Page! with { FromCache = true }, result.State with { Status = "Cached", Error = index.Error + (download.Error.Length == 0 ? "" : "; " + download.Error) });
            return result;
        }, cancellation).ConfigureAwait(false);
    }

    private async Task<ProviderResult> Workbook(GuideDuty duty, CancellationToken cancellation, bool refresh)
    {
        var download = await Read(WorkbookUrl, MaximumWorkbookBytes, SharedLifetime, "xlsx",
            bytes => ForetellWorkbookGuide.Read(bytes, duty), cancellation, refresh).ConfigureAwait(false);
        return Result(WorkbookProvider, download);
    }

    private static ProviderResult Result(string provider, Download<GuideSourcePage?> download)
    {
        if (download.Value == null) return new(null, new(provider, "Missing", download.Entry.Url, "No matching guide text is available."));
        var page = download.Value with { Provider = provider, RetrievedAt = download.Entry.RetrievedAt, ModifiedAt = download.Entry.ModifiedAt, FromCache = download.FromCache };
        return new(page, new(provider, download.FromCache ? "Cached" : "Ready", page.Url, download.Error));
    }

    internal static string? MatchRavenGuide(string html, GuideDuty duty)
    {
        var name = CatalogName(duty.EnglishName);
        var matches = Catalog(html).Where(link => CatalogName(link.Title) == name).Select(link => link.Url).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static IEnumerable<(string Title, string Url)> Catalog(string html)
    {
        foreach (Match link in Links.Matches(IgnoredHtml.Replace(html, "")))
        {
            var href = Attribute(link.Groups["attributes"].Value, "href");
            if (!Uri.TryCreate(new Uri(RavenIndex), href, out var uri) || uri.Host != "ravensreminders.com" || !Allowed(uri)
                || uri.Query.Length != 0 || !GuidePath.IsMatch(uri.AbsolutePath)) continue;
            yield return (ForetellGuideParser.PlainText(link.Groups["body"].Value), uri.GetLeftPart(UriPartial.Path));
        }
    }

    private static string CatalogName(string title)
    {
        var name = GuideNames.Normalize(title);
        var article = Article.Match(name);
        if (article.Success) name = article.Groups["article"].Value + " " + article.Groups["name"].Value + article.Groups["suffix"].Value;
        return GuideNames.Normalize(name).Replace(" :", ":", StringComparison.Ordinal);
    }

    internal static string HtmlText(string html)
    {
        if (Encoding.UTF8.GetByteCount(html) > MaximumPageBytes) throw new InvalidDataException("Guide HTML exceeds size limits.");
        var decorated = Tags.Replace(html, tag =>
        {
            var attributes = tag.Groups["attributes"].Value;
            var labels = new List<string>();
            foreach (var attribute in new[] { "alt", "title", "aria-label" })
            {
                var label = Attribute(attributes, attribute).Trim();
                if (label.Length != 0 && !labels.Contains(label, StringComparer.Ordinal)) labels.Add(label);
            }
            foreach (var icon in Attribute(attributes, "class").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (icon is "tank" or "healer" or "dps" or "melee" or "ranged" || icon.StartsWith("role-", StringComparison.Ordinal)) labels.Add(icon);
                else if (icon.StartsWith("fa-", StringComparison.Ordinal) && icon is not ("fa-fw" or "fa-lg" or "fa-xs" or "fa-sm" or "fa-spin" or "fa-solid"))
                    labels.Add("icon: " + icon[3..]);
            }
            return tag.Value + (labels.Count == 0 ? "" : " [" + WebUtility.HtmlEncode(string.Join("; ", labels)) + "] ");
        });
        if (Encoding.UTF8.GetByteCount(decorated) > MaximumPageBytes * 2) throw new InvalidDataException("Guide HTML labels exceed size limits.");
        return ForetellGuideParser.PlainText(decorated);
    }

    private static string Attribute(string attributes, string name)
        => Attributes.Matches(attributes).Cast<Match>().Where(attribute => attribute.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(attribute => WebUtility.HtmlDecode(attribute.Groups["value"].Value)).FirstOrDefault() ?? "";

    private async Task<Download<T>> Read<T>(string url, int limit, TimeSpan lifetime, string format, Func<byte[], T> parse, CancellationToken cancellation, bool refresh = false)
    {
        var key = GuideNames.Hash(url);
        var gate = lifetime > TimeSpan.Zero ? url == RavenIndex ? IndexGate : WorkbookGate : null;
        if (gate != null) await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var cached = await ReadCache(key, url, limit, format, cancellation).ConfigureAwait(false);
            T? cachedValue = default;
            if (cached != null)
            {
                try { cachedValue = parse(cached.Bytes); }
                catch (Exception error) when (error is not OperationCanceledException) { cached = null; }
            }
            if (cached != null && !refresh && lifetime > TimeSpan.Zero && DateTime.UtcNow - cached.CheckedAt < lifetime)
                return new(cachedValue!, cached, true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var uri = new Uri(url);
                for (var redirects = 0; ; ++redirects)
                {
                    if (!Allowed(uri) || !SameProvider(new Uri(url), uri)) throw new InvalidDataException("Guide redirect is outside the HTTPS provider allowlist.");
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    if (cached != null)
                    {
                        if (EntityTagHeaderValue.TryParse(cached.ETag, out var etag)) request.Headers.IfNoneMatch.Add(etag);
                        if (cached.ModifiedAt is { } modified) request.Headers.IfModifiedSince = new DateTimeOffset(modified);
                    }
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                    {
                        if (redirects >= 5 || response.Headers.Location == null) throw new InvalidDataException("Guide redirect chain is invalid or too long.");
                        var next = new Uri(uri, response.Headers.Location);
                        if (new Uri(url).Host == "ravensreminders.com" && next.AbsolutePath.TrimEnd('/') != new Uri(url).AbsolutePath.TrimEnd('/'))
                            throw new InvalidDataException("Raven redirect changes guide identity.");
                        uri = next;
                        continue;
                    }
                    if (response.StatusCode == HttpStatusCode.NotModified)
                    {
                        if (cached == null) throw new InvalidDataException("Provider returned 304 without a verified cached response.");
                        cached = cached with { CheckedAt = DateTime.UtcNow, ETag = response.Headers.ETag?.ToString() ?? cached.ETag,
                            ModifiedAt = response.Content.Headers.LastModified?.UtcDateTime ?? cached.ModifiedAt };
                        await WriteCache(key, cached, cancellation).ConfigureAwait(false);
                        return new(cachedValue!, cached, true);
                    }
                    response.EnsureSuccessStatusCode();
                    var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
                    if (!MediaTypeMatches(mediaType, format)) throw new InvalidDataException("Guide response has an unexpected content type.");
                    if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Guide response exceeds byte limits.");
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    var bytes = await ReadBounded(stream, limit, timeout.Token).ConfigureAwait(false);
                    var value = parse(bytes);
                    timeout.Token.ThrowIfCancellationRequested();
                    var now = DateTime.UtcNow;
                    var entry = new CacheEntry(url, uri.AbsoluteUri, mediaType, bytes, Hash(bytes), response.Headers.ETag?.ToString() ?? "",
                        response.Content.Headers.LastModified?.UtcDateTime, now, now);
                    await WriteCache(key, entry, cancellation).ConfigureAwait(false);
                    return new(value, entry, false);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception error) when (cached != null)
            {
                return new(cachedValue!, cached, true, Diagnostic(error));
            }
        }
        finally { gate?.Release(); }
    }

    private async Task<CacheEntry?> ReadCache(string key, string url, int limit, string format, CancellationToken cancellation)
    {
        try
        {
            await using var stream = File.OpenRead(Path.Combine(_directory, key + ".json"));
            var bytes = await ReadBounded(stream, limit * 4 / 3 + MaximumMetadataBytes, cancellation).ConfigureAwait(false);
            var cached = JsonSerializer.Deserialize<CacheEntry>(bytes);
            if (cached == null || cached.Url != url || cached.Bytes == null || cached.Bytes.Length == 0 || cached.Bytes.Length > limit
                || cached.Hash != Hash(cached.Bytes) || !Uri.TryCreate(cached.FinalUrl, UriKind.Absolute, out var finalUri)
                || !Allowed(finalUri) || !SameProvider(new Uri(url), finalUri) || !MediaTypeMatches(cached.MediaType, format)
                || cached.CheckedAt < cached.RetrievedAt || cached.CheckedAt > DateTime.UtcNow || cached.RetrievedAt == default
                || cached.ETag is not { Length: <= 1024 }) return null;
            return cached;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    private async Task WriteCache(string key, CacheEntry entry, CancellationToken cancellation)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(entry);
        if (payload.Length > MaximumWorkbookBytes * 4 / 3 + MaximumMetadataBytes) return;
        await DiskGate.WaitAsync(cancellation).ConfigureAwait(false);
        var path = Path.Combine(_directory, key + ".json");
        var temporary = Path.Combine(_directory, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(_directory);
            var files = new DirectoryInfo(_directory).EnumerateFiles("*.json").Where(file => CacheName.IsMatch(file.Name)).OrderBy(file => file.LastWriteTimeUtc).ToList();
            var total = files.Sum(file => file.Length);
            var count = files.Count;
            foreach (var file in files)
            {
                if (total + payload.Length <= MaximumDiskBytes && count < MaximumCacheEntries) break;
                var length = file.Length;
                file.Delete();
                total -= length;
                --count;
            }
            await File.WriteAllBytesAsync(temporary, payload, cancellation).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            DiskGate.Release();
        }
    }

    private static async Task<byte[]> ReadBounded(Stream stream, int limit, CancellationToken cancellation)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit - (int)output.Length + 1)), cancellation).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > limit) throw new InvalidDataException("Guide response exceeds byte limits.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static bool MediaTypeMatches(string mediaType, string format) => format switch
    {
        "json" => mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase),
        "html" => mediaType is "text/html" or "application/xhtml+xml",
        "xlsx" => mediaType is "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" or "application/octet-stream" or "application/zip" or "application/binary",
        _ => false
    };

    private static bool GoogleHost(string host)
        => host is "docs.google.com" or "sheets.google.com" or "sheets.googleusercontent.com" || host.EndsWith("-sheets.googleusercontent.com", StringComparison.Ordinal);

    private static bool Allowed(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
            && (uri.Host is "ffxiv.consolegameswiki.com" or "ffxiv.gamerescape.com" or "ravensreminders.com" || GoogleHost(uri.Host));

    private static bool SameProvider(Uri origin, Uri destination)
        => (origin.Host == destination.Host || GoogleHost(origin.Host) && GoogleHost(destination.Host))
            && (origin.Host != "ravensreminders.com" || origin.AbsolutePath.TrimEnd('/') == destination.AbsolutePath.TrimEnd('/'));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Diagnostic(Exception error) => error is OperationCanceledException ? "Guide request timed out after 20 seconds." : error.Message[..Math.Min(error.Message.Length, 512)];
    public void Dispose() => _http.Dispose();
}
