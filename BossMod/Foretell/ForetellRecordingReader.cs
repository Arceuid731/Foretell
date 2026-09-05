using System.IO;
using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BossMod.Foretell;

// Two bounded sequential passes: inspect completeness/time bounds, then evaluate. Neither retains the corpus.
// The same API accepts an Analysis ZIP, an automatic capture index/directory, or a legacy JSONL recording.
public sealed class ForetellRecordingReader
{
    private const long MaxExpandedBytes = 1024L * 1024 * 1024;
    private readonly string _path;
    private byte[]? _sealedIndex;
    private static readonly JsonSerializerOptions Json = new()
    { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, Converters = { new JsonStringEnumConverter() } };
    public long Parsed { get; private set; }
    public long Rejected { get; private set; }
    public bool Complete { get; private set; } = true;
    public DateTime First { get; private set; }
    public DateTime Last { get; private set; }

    public ForetellRecordingReader(string path) => _path = Path.GetFullPath(path);
    internal ForetellRecordingReader(string path, byte[] sealedIndex) : this(path) => _sealedIndex = sealedIndex;

    public void Inspect(System.Threading.CancellationToken cancellationToken = default)
    {
        foreach (var _ in Read()) cancellationToken.ThrowIfCancellationRequested();
        if (Parsed == 0) throw new InvalidDataException("Recording contains no usable observations");
    }

    public IEnumerable<ForetellObservation> Read()
    {
        Parsed = Rejected = 0; Complete = true; First = Last = default;
        long expanded = 0;
        long expected = -1;
        long parseRejected = 0;
        Dictionary<string, string> hashes = [];
        IEnumerable<ForetellObservation> Consume(Stream source, bool gzip)
        {
            using var decompressor = gzip ? new GZipStream(source, CompressionMode.Decompress, leaveOpen: true) : null;
            using var reader = new StreamReader(decompressor ?? source, Encoding.UTF8, true, 8192, leaveOpen: true);
            foreach (var line in Lines(reader))
            {
                expanded += line.Length * 2L;
                if (expanded > MaxExpandedBytes) throw new InvalidDataException("Expanded recording exceeds its streaming work limit");
                if (string.IsNullOrWhiteSpace(line)) continue;
                ForetellObservation? item = null;
                try { item = JsonSerializer.Deserialize<ForetellObservation>(line, Json); }
                catch (JsonException) { }
                if (item == null || item.At == default || item.Kind == ObservationKind.Unknown || !Enum.IsDefined(item.Kind))
                { ++Rejected; ++parseRejected; Complete = false; continue; }
                ++Parsed;
                if (First == default || item.At < First) First = item.At;
                if (item.At > Last) Last = item.At;
                yield return item;
            }
        }
        string[] Parts(Stream input)
        {
            using var limited = new MemoryStream();
            var buffer = new byte[8192]; int count;
            while ((count = input.Read(buffer)) > 0)
            {
                if (limited.Length + count > 128 * 1024) throw new InvalidDataException("Capture index exceeds its size limit");
                limited.Write(buffer, 0, count);
            }
            using var index = JsonDocument.Parse(_sealedIndex ??= limited.ToArray());
            var root = index.RootElement;
            if (root.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("Unsupported capture schema");
            Complete = root.TryGetProperty("complete", out var complete) && complete.GetBoolean();
            Rejected += root.TryGetProperty("rejected", out var rejected) ? rejected.GetInt64() : 0;
            expected = root.TryGetProperty("observations", out var observations) ? observations.GetInt64() : 0;
            hashes = root.GetProperty("hashes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            var parts = root.GetProperty("parts").EnumerateArray().Select(p => p.GetString()!).ToArray();
            if (parts.Length > 1024 || parts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != parts.Length
                || parts.Any(p => string.IsNullOrWhiteSpace(p) || p != Path.GetFileName(p) || !p.EndsWith(".jsonl.gz", StringComparison.Ordinal)))
                throw new InvalidDataException("Invalid capture parts");
            if (parts.Any(p => !hashes.ContainsKey(p))) throw new InvalidDataException("Capture part has no integrity hash");
            return parts;
        }
        IEnumerable<ForetellObservation> Verified(Stream source, string part)
        {
            // One small compressed part, bounded independently of the full recording size. Verify it before
            // feeding any of its events into evaluation, including when gzip accepts a truncated trailer.
            using var compressed = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = source.Read(buffer)) > 0)
            {
                if (compressed.Length + count > 4 * 1024 * 1024 + 65536) throw new InvalidDataException("Capture part exceeds its size bound");
                compressed.Write(buffer, 0, count);
            }
            compressed.Position = 0;
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(compressed)), hashes[part], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Capture part integrity check failed: " + part);
            compressed.Position = 0;
            foreach (var item in Consume(compressed, true)) yield return item;
        }
        if (_path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(_path);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);
            var entry = archive.GetEntry("capture/index.json") ?? throw new InvalidDataException("ZIP has no automatic decision capture");
            using var indexStream = entry.Open();
            foreach (var part in Parts(indexStream))
            {
                var partEntry = archive.GetEntry("capture/" + part) ?? throw new InvalidDataException("Missing capture part: " + part);
                using var stream = partEntry.Open();
                foreach (var item in Verified(stream, part)) yield return item;
            }
        }
        else if (Directory.Exists(_path) || Path.GetFileName(_path) == "index.json")
        {
            var directory = Directory.Exists(_path) ? _path : Path.GetDirectoryName(_path)!;
            using Stream indexStream = _sealedIndex == null ? File.OpenRead(Path.Combine(directory, "index.json")) : new MemoryStream(_sealedIndex, writable: false);
            foreach (var part in Parts(indexStream))
            {
                using var stream = File.OpenRead(Path.Combine(directory, part));
                foreach (var item in Verified(stream, part)) yield return item;
            }
        }
        else
        {
            if (new FileInfo(_path).Length > 512L * 1024 * 1024) throw new InvalidDataException("Legacy recording exceeds 512 MiB");
            using var stream = File.OpenRead(_path);
            foreach (var item in Consume(stream, _path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))) yield return item;
        }
        if (expected >= 0 && Parsed + parseRejected != expected)
            throw new InvalidDataException("Capture contains fewer observations than its index");
    }

    private static IEnumerable<string> Lines(TextReader reader)
    {
        var buffer = new char[8192]; var line = new StringBuilder(); int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < count; ++i)
            {
                if (buffer[i] == '\n') { yield return line.ToString(); line.Clear(); }
                else
                {
                    if (line.Length >= 1024 * 1024) throw new InvalidDataException("Recording line exceeds 1 MiB characters");
                    line.Append(buffer[i]);
                }
            }
        }
        if (line.Length > 0) yield return line.ToString();
    }
}
