using System.IO;
using System.Text.Json;
using System.Threading;

namespace BossMod.Foretell;

internal sealed record GuideBindingRecord(string Key, string Scope, string Mechanic, string Kind, uint ID, string Name,
    uint SourceOID, uint SourceNameID, int Observations, DateTime LastSeen);

internal sealed class GuideBindingMemory : IDisposable
{
    internal const int EntryLimit = 1024;
    internal const int FileSizeLimit = 2 * 1024 * 1024;
    internal const int ObservationLimit = 1_000_000;
    private const int Schema = 1;
    private readonly object _gate = new();
    private readonly Dictionary<string, GuideBindingRecord> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly string? _path;
    private readonly Task _worker;
    private string? _destination;
    private TaskCompletionSource? _flush;
    private bool _ready;
    private bool _disposed;
    private bool _dirty;
    private string _error = "";

    public GuideBindingMemory(string? path = null)
    {
        _path = path;
        _ready = path == null;
        _worker = path == null ? Task.CompletedTask : Task.Run(WorkAsync);
    }

    public GuideBindingRecord[] Entries { get { lock (_gate) return Snapshot(); } }
    public string Error { get { lock (_gate) return _error; } }
    public bool Ready { get { lock (_gate) return _ready; } }

    public bool Remember(GuideBindingRecord value)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (!Valid(value))
            {
                SetError("Invalid guide event-ID binding.");
                return false;
            }
            value = Clamp(value);
            if (_entries.TryGetValue(value.Key, out var previous))
            {
                value = Merge(previous, value);
                if (value == previous) return false;
            }
            _entries[value.Key] = value;
            Prune(_entries);
            if (!_entries.ContainsKey(value.Key)) return false;
            _dirty = true;
            if (_path != null) Signal();
            return true;
        }
    }

    public Task FlushAsync()
    {
        lock (_gate)
        {
            if (_disposed) return _worker;
            if (_path == null) return Task.CompletedTask;
            _flush ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            Signal();
            return _flush.Task;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_path != null) Signal();
                else _signal.Dispose();
            }
        }
        _worker.GetAwaiter().GetResult();
    }

    private void Signal()
    {
        if (_signal.CurrentCount == 0) _signal.Release();
    }

    private async Task WorkAsync()
    {
        try
        {
            Load();
            while (true)
            {
                await _signal.WaitAsync().ConfigureAwait(false);
                bool debounce;
                lock (_gate) debounce = !_disposed && _flush == null;
                if (debounce) await Task.Delay(250).ConfigureAwait(false);

                GuideBindingRecord[]? snapshot;
                TaskCompletionSource? flush;
                bool stopping;
                lock (_gate)
                {
                    snapshot = _dirty ? Snapshot() : null;
                    _dirty = false;
                    flush = _flush;
                    _flush = null;
                    stopping = _disposed;
                }
                if (snapshot != null && !Write(snapshot))
                {
                    lock (_gate) _dirty = true;
                }
                flush?.TrySetResult();
                if (stopping) return;
            }
        }
        finally
        {
            lock (_gate)
            {
                _ready = true;
                _flush?.TrySetResult();
                _flush = null;
                _signal.Dispose();
            }
        }
    }

    private void Load()
    {
        var loaded = new Dictionary<string, GuideBindingRecord>(StringComparer.Ordinal);
        try
        {
            _destination = Path.GetFullPath(_path!);
            using var file = new FileStream(_destination, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > FileSizeLimit) throw new InvalidDataException("Guide binding file exceeds 2 MiB.");
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = file.Read(chunk, 0, Math.Min(chunk.Length, FileSizeLimit + 1 - (int)buffer.Length))) != 0)
            {
                buffer.Write(chunk, 0, count);
                if (buffer.Length > FileSizeLimit) throw new InvalidDataException("Guide binding file exceeds 2 MiB.");
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Schema", out var schema)
                || !schema.TryGetInt32(out var version) || version != Schema)
                throw new InvalidDataException("Unsupported guide binding schema.");
            if (!root.TryGetProperty("Entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Missing guide binding entries.");
            foreach (var element in entries.EnumerateArray())
            {
                var value = element.Deserialize<GuideBindingRecord>();
                if (!Valid(value)) throw new InvalidDataException("Invalid persisted guide event-ID binding.");
                value = Clamp(value!);
                if (loaded.ContainsKey(value.Key)) throw new InvalidDataException("Duplicate persisted guide binding key.");
                loaded[value.Key] = value;
                Prune(loaded);
            }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception error) when (StorageError(error))
        {
            loaded.Clear();
            SetError("Guide binding load: " + error.Message);
        }
        lock (_gate)
        {
            foreach (var value in _entries.Values)
                loaded[value.Key] = loaded.TryGetValue(value.Key, out var previous) ? Merge(previous, value) : value;
            Prune(loaded);
            _entries.Clear();
            foreach (var value in loaded.Values) _entries.Add(value.Key, value);
            _ready = true;
        }
    }

    private bool Write(GuideBindingRecord[] entries)
    {
        string? temporary = null;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Schema, Entries = entries });
            if (bytes.Length > FileSizeLimit) throw new InvalidDataException("Guide binding snapshot exceeds 2 MiB.");
            var destination = _destination ?? throw new InvalidDataException("Invalid guide binding path.");
            var directory = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(true);
            }
            File.Move(temporary, destination, true);
            return true;
        }
        catch (Exception error) when (StorageError(error))
        {
            SetError("Guide binding write: " + error.Message);
            return false;
        }
        finally
        {
            if (temporary != null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (StorageError(error))
                {
                    SetError("Guide binding temporary file cleanup: " + error.Message);
                }
            }
        }
    }

    private GuideBindingRecord[] Snapshot() => _entries.Values.OrderByDescending(value => value.LastSeen)
        .ThenBy(value => value.Key, StringComparer.Ordinal).ToArray();

    private void SetError(string error)
    {
        lock (_gate) _error = error.Length <= 512 ? error : error[..512];
    }

    private static void Prune(Dictionary<string, GuideBindingRecord> entries)
    {
        if (entries.Count <= EntryLimit) return;
        foreach (var key in entries.Values.OrderByDescending(value => value.LastSeen).ThenBy(value => value.Key, StringComparer.Ordinal)
            .Skip(EntryLimit).Select(value => value.Key).ToArray())
            entries.Remove(key);
    }

    private static GuideBindingRecord Clamp(GuideBindingRecord value) => value with { Observations = Math.Clamp(value.Observations, 1, ObservationLimit) };

    private static GuideBindingRecord Merge(GuideBindingRecord previous, GuideBindingRecord value) =>
        previous.Scope == value.Scope && previous.Mechanic == value.Mechanic && previous.Kind == value.Kind && previous.ID == value.ID
        && previous.Name == value.Name && previous.SourceOID == value.SourceOID && previous.SourceNameID == value.SourceNameID
            ? value with { Observations = (int)Math.Min(ObservationLimit, (long)previous.Observations + value.Observations), LastSeen = previous.LastSeen > value.LastSeen ? previous.LastSeen : value.LastSeen }
            : value;

    private static bool Valid(GuideBindingRecord? value) => value != null && Hash(value.Key) && Hash(value.Scope)
        && Text(value.Mechanic) && Text(value.Name) && value.Kind is "cast" or "status"
        && value.ID != 0 && value.SourceOID != 0 && value.SourceNameID != 0
        && value.LastSeen > DateTime.MinValue && value.LastSeen < DateTime.MaxValue;

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 120 && !value.Any(char.IsControl);
    private static bool StorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
        or ArgumentException or NotSupportedException or InvalidOperationException or System.Security.SecurityException;
}
