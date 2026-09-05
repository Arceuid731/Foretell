using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading;

namespace BossMod.Foretell;

// Only this new automatic cache is rotated automatically. Existing raw/readable files and exported ZIPs
// retain their separate, user-controlled retention policy. No game services are used by this worker.
internal sealed class ForetellCapture : IDisposable
{
    internal const long SessionLimit = 64L * 1024 * 1024;
    internal const long CacheLimit = 256L * 1024 * 1024;
    internal const long ExpandedSessionLimit = 512L * 1024 * 1024;
    internal const int SegmentLimit = 4 * 1024 * 1024;
    private const long QueueLimit = 16L * 1024 * 1024;
    private const int ObservationLimit = 512 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };
    internal sealed class Session(string directory, uint territory, string id, string version)
    {
        public readonly string Directory = directory;
        public readonly uint Territory = territory;
        public readonly string ID = id;
        public readonly string Version = version;
        public long Rejected;
        public long Written;
        public long Bytes;
        public long ExpandedBytes;
        public int Capped;
        public string Error = "";
    }
    internal sealed class Snapshot(string directory, byte[] index, string[] parts, Action release) : IDisposable
    {
        public string Directory { get; } = directory;
        public byte[] Index { get; } = index;
        public string[] Parts { get; } = parts;
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
    private sealed record Event(Session Session, ForetellObservation Observation, long Bytes);
    private sealed record Seal(string Directory, TaskCompletionSource<Snapshot?> Completion);
    private readonly string _root;
    private readonly long _sessionLimit;
    private readonly long _cacheLimit;
    private readonly long _expandedLimit;
    private readonly int _segmentLimit;
    private int SegmentReserve => _segmentLimit + 64 * 1024;
    private readonly BlockingCollection<object> _queue = new(new ConcurrentQueue<object>(), 1024);
    private readonly Thread _thread;
    private readonly object _filesLock = new();
    private readonly Dictionary<string, int> _pins = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;
    private long _queuedBytes;
    private Session? _active;
    private FileStream? _file;
    private GZipStream? _gzip;
    private string _temporary = "";
    private int _segmentBytes;
    private int _part;
    private long _lastContext;
    private DateTime _segmentStarted;
    private DateTime _first, _last;
    private readonly List<string> _parts = [];
    private readonly Dictionary<string, string> _hashes = [];
    private readonly Dictionary<ObservationKind, long> _counts = [];
    public long PendingBytes => Interlocked.Read(ref _queuedBytes);

    public ForetellCapture(string root, long sessionLimit = SessionLimit, long cacheLimit = CacheLimit,
        int segmentLimit = SegmentLimit, long expandedLimit = ExpandedSessionLimit)
    {
        _root = Path.GetFullPath(root);
        _sessionLimit = sessionLimit; _cacheLimit = cacheLimit; _expandedLimit = expandedLimit;
        _segmentLimit = segmentLimit;
        _thread = new(Run) { IsBackground = true, Name = "Foretell compact capture" };
        _thread.Start();
    }

    public Session NewSession(uint territory, string id, string version)
        => new(Path.Combine(_root, $"foretell-T{territory}-{Guid.NewGuid():N}"), territory, id, version);

    public void Enqueue(Session session, ForetellObservation source)
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref session.Capped) != 0)
        { Interlocked.Increment(ref session.Rejected); return; }
        // Account for strings, dictionary entries, opaque bytes and context before allocating a queued copy.
        // Oversized inputs are reported as gaps; never silently trim a feature used by the live learner.
        long bytes = 1024 + (source.Context?.Actors.Length ?? 0) * 160L + (source.Detail?.Length ?? 0) * 2L;
        if (source.Numeric.Count + source.Text.Count + source.Binary.Count > 8192)
        { Interlocked.Increment(ref session.Rejected); return; }
        foreach (var pair in source.Numeric) bytes += 96 + pair.Key.Length * 2L;
        foreach (var pair in source.Text) bytes += 96 + (pair.Key.Length + (pair.Value?.Length ?? 0)) * 2L;
        foreach (var pair in source.Binary) bytes += 96 + pair.Key.Length * 2L + (pair.Value?.Length ?? 0);
        if (bytes > ObservationLimit || Interlocked.Read(ref _queuedBytes) + bytes > QueueLimit)
        { Interlocked.Increment(ref session.Rejected); return; }
        var copy = source.CopyForRecording();
        Interlocked.Add(ref _queuedBytes, bytes);
        try { if (_queue.TryAdd(new Event(session, copy, bytes))) return; }
        catch (InvalidOperationException) { }
        Interlocked.Add(ref _queuedBytes, -bytes);
        Interlocked.Increment(ref session.Rejected);
    }

    // FIFO barrier: close the segment before exposing immutable parts. The next event opens a new part,
    // so an Analysis ZIP can include the active session without racing the writer or blocking the game.
    public Task<Snapshot?> SnapshotAsync(string directory)
    {
        var completion = new TaskCompletionSource<Snapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (Volatile.Read(ref _disposed) == 0 && _queue.TryAdd(new Seal(directory, completion))) return completion.Task;
        }
        catch (InvalidOperationException) { }
        completion.SetException(new IOException("Compact capture snapshot unavailable: writer closed or queue full"));
        return completion.Task;
    }

    private void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            if (item is Seal seal)
            {
                try
                {
                    if (_active?.Directory == seal.Directory) { ClosePart(); SaveIndex(); }
                    seal.Completion.SetResult(PinSnapshot(seal.Directory));
                }
                catch (Exception e) { seal.Completion.SetException(e); }
                continue;
            }
            var entry = (Event)item;
            try { Write(entry); }
            catch (Exception e)
            {
                entry.Session.Error = $"{e.GetType().Name}: {e.Message}";
                Interlocked.Increment(ref entry.Session.Rejected);
                Volatile.Write(ref entry.Session.Capped, 1);
                try { ClosePart(); SaveIndex(); } catch { }
            }
            finally { Interlocked.Add(ref _queuedBytes, -entry.Bytes); }
        }
        try { ClosePart(); SaveIndex(); } catch { }
    }

    private void Write(Event item)
    {
        if (_active != item.Session)
        {
            ClosePart(); SaveIndex();
            _active = item.Session; _part = 0; _parts.Clear(); _hashes.Clear(); _counts.Clear(); _first = _last = default;
        }
        if (Volatile.Read(ref item.Session.Capped) != 0)
        { Interlocked.Increment(ref item.Session.Rejected); return; }
        if (_file != null && DateTime.UtcNow - _segmentStarted >= TimeSpan.FromMinutes(1)) { ClosePart(); SaveIndex(); }
        var observation = item.Observation;
        // Context is immutable after construction on the game thread, shared there once per frame.
        // Each compressed part starts with its own full context and is independently readable.
        var context = observation.Context;
        if (_lastContext == observation.ContextID && _file != null) observation.Context = null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(observation, Json);
        if (bytes.Length + 1 > Math.Min(1024 * 1024, _segmentLimit)) { Interlocked.Increment(ref item.Session.Rejected); return; }
        if (_file != null && _segmentBytes + bytes.Length + 1 > _segmentLimit)
        {
            ClosePart(); SaveIndex();
            observation.Context = context;
            bytes = JsonSerializer.SerializeToUtf8Bytes(observation, Json);
        }
        if (_file == null && !OpenPart()) { Interlocked.Increment(ref item.Session.Rejected); return; }
        _gzip!.Write(bytes); _gzip.WriteByte(10);
        _segmentBytes += bytes.Length + 1;
        _active.ExpandedBytes += bytes.Length + 1;
        _lastContext = observation.ContextID;
        Interlocked.Increment(ref _active.Written);
        _counts[observation.Kind] = _counts.GetValueOrDefault(observation.Kind) + 1;
        if (_first == default || observation.At < _first) _first = observation.At;
        if (observation.At > _last) _last = observation.At;
    }

    private bool OpenPart()
    {
        var session = _active!;
        if (session.Bytes + SegmentReserve > _sessionLimit || session.ExpandedBytes + _segmentLimit > _expandedLimit || _part >= 512)
        { session.Error = "Session capture limit reached; subsequent events are not recorded"; Volatile.Write(ref session.Capped, 1); return false; }
        lock (_filesLock)
        {
            Directory.CreateDirectory(session.Directory);
            PruneCache(session.Directory);
            var used = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
            if (used + SegmentReserve > _cacheLimit)
            { session.Error = "Automatic capture cache is full or protected by an export"; Volatile.Write(ref session.Capped, 1); return false; }
            _temporary = Path.Combine(session.Directory, $"{++_part:D6}.jsonl.gz.tmp");
            _file = new(_temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536);
            _gzip = new(_file, CompressionLevel.Fastest, leaveOpen: true);
            _segmentBytes = 0; _lastContext = 0; _segmentStarted = DateTime.UtcNow;
            return true;
        }
    }

    private void ClosePart()
    {
        if (_file == null) return;
        try
        {
            _gzip!.Dispose();
            _file.Dispose();
            var final = _temporary[..^4];
            File.Move(_temporary, final);
            _parts.Add(Path.GetFileName(final));
            using (var input = File.OpenRead(final)) _hashes[Path.GetFileName(final)] = Convert.ToHexString(SHA256.HashData(input));
            Interlocked.Add(ref _active!.Bytes, new FileInfo(final).Length);
        }
        finally { _gzip = null; _file?.Dispose(); _file = null; }
    }

    private byte[] Index() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = 1, sessionID = _active!.ID, territory = _active.Territory, pluginVersion = _active.Version,
        first = _first, last = _last, observations = _active.Written, rejected = Interlocked.Read(ref _active.Rejected),
        complete = _active.Rejected == 0 && _active.Error.Length == 0, error = _active.Error,
        compressedBytes = _active.Bytes, expandedBytes = _active.ExpandedBytes, parts = _parts, hashes = _hashes, counts = _counts,
        semantics = "Accepted normalized events in arrival order, including recorded client priors and world context. Cold-start semantic evaluation; no initial learned-memory checkpoint, rendered pixels or historical collision scene."
    }, Json);

    private void SaveIndex()
    {
        if (_active == null) return;
        Directory.CreateDirectory(_active.Directory);
        var path = Path.Combine(_active.Directory, "index.json");
        File.WriteAllBytes(path + ".tmp", Index());
        File.Move(path + ".tmp", path, true);
    }

    private Snapshot? PinSnapshot(string directory)
    {
        lock (_filesLock)
        {
            var full = Path.GetFullPath(directory);
            if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(Path.Combine(full, "index.json"))) return null;
            var index = File.ReadAllBytes(Path.Combine(full, "index.json"));
            using var document = JsonDocument.Parse(index);
            var parts = document.RootElement.GetProperty("parts").EnumerateArray().Select(e => e.GetString()!).ToArray();
            if (parts.Any(p => p != Path.GetFileName(p) || !p.EndsWith(".jsonl.gz", StringComparison.Ordinal)))
                throw new InvalidDataException("Invalid capture index part");
            _pins[full] = _pins.GetValueOrDefault(full) + 1;
            return new(full, index, parts, () => { lock (_filesLock) { if (--_pins[full] == 0) _pins.Remove(full); } });
        }
    }

    private void PruneCache(string active)
    {
        // Resolve and validate every target before deletion. Only known files in our dedicated session folders.
        var directories = Directory.GetDirectories(_root, "foretell-T*")
            .Select(path => new DirectoryInfo(path)).Where(d => d.FullName != active && !_pins.ContainsKey(d.FullName))
            .OrderBy(d => d.LastWriteTimeUtc).ToArray();
        var used = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
        foreach (var directory in directories)
        {
            if (used + SegmentReserve <= _cacheLimit && directory.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-14)) continue;
            if (!directory.FullName.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            foreach (var file in directory.GetFiles())
            {
                if (file.Name != "index.json" && file.Name != "index.json.tmp" && !file.Name.EndsWith(".jsonl.gz", StringComparison.Ordinal)
                    && !file.Name.EndsWith(".jsonl.gz.tmp", StringComparison.Ordinal)) continue;
                try { var length = file.Length; file.Delete(); used -= length; } catch (IOException) { }
            }
            if (!directory.EnumerateFileSystemInfos().Any()) directory.Delete();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.CompleteAdding();
        // Shutdown is the only synchronous wait. A background worker may finish draining after this deadline.
        _thread.Join(TimeSpan.FromSeconds(1));
    }
}
