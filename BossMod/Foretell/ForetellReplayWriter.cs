using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace BossMod.Foretell;

// Replay serialization and disk I/O must never run on the framework/render thread. This optional readable stream
// contains normalized events only; exact transport bytes live in the independently bounded raw journal.
internal sealed class ForetellReplayWriter : IDisposable
{
    private readonly record struct Item(string Path, ForetellObservation Observation);

    private const int MaxQueuedObservations = 16384;
    private const long MaxFileBytes = 512L * 1024 * 1024;
    private readonly BlockingCollection<Item> _queue = new(new ConcurrentQueue<Item>(), MaxQueuedObservations);
    private readonly CancellationTokenSource _stop = new();
    private readonly JsonSerializerOptions _json;
    private readonly Thread _thread;
    private int _disposed;
    private long _pending;
    private long _written;
    private long _rejected;
    private int _failed;
    private string _failure = "";

    public long Pending => Interlocked.Read(ref _pending);
    public long Written => Interlocked.Read(ref _written);
    public long Rejected => Interlocked.Read(ref _rejected);
    public bool Failed => Volatile.Read(ref _failed) != 0;
    public string Failure => _failure;

    public ForetellReplayWriter(JsonSerializerOptions json)
    {
        _json = json;
        _thread = new(Run)
        {
            IsBackground = true,
            Name = "Foretell replay writer"
        };
        _thread.Start();
    }

    public void Enqueue(string path, ForetellObservation observation)
    {
        if (Volatile.Read(ref _disposed) != 0 || Failed)
        {
            Interlocked.Increment(ref _rejected);
            return;
        }
        Interlocked.Increment(ref _pending);
        try
        {
            if (_queue.TryAdd(new(path, observation))) return;
        }
        catch (InvalidOperationException) { }
        Interlocked.Decrement(ref _pending);
        Interlocked.Increment(ref _rejected);
    }

    // Used only by the explicit Replay Lab action, never by the per-frame update path.
    public void Drain(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (Pending != 0 && DateTime.UtcNow < until)
            Thread.Sleep(5);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _queue.CompleteAdding();
        if (!_thread.Join(TimeSpan.FromSeconds(1)))
        {
            _stop.Cancel();
            _thread.Join(TimeSpan.FromSeconds(1));
        }
        if (!_thread.IsAlive)
        {
            _stop.Dispose();
            _queue.Dispose();
        }
    }

    private void Run()
    {
        StreamWriter? writer = null;
        var path = "";
        long context = 0;
        var cappedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable(_stop.Token))
            {
                if (cappedPaths.Contains(item.Path))
                {
                    Interlocked.Decrement(ref _pending);
                    Interlocked.Increment(ref _rejected);
                    continue;
                }
                if (writer == null || !string.Equals(path, item.Path, StringComparison.Ordinal))
                {
                    writer?.Dispose();
                    path = item.Path;
                    context = 0;
                    writer = new(path, append: true) { AutoFlush = true };
                }
                if (item.Observation.ContextID == context) item.Observation.Context = null;
                var line = JsonSerializer.Serialize(item.Observation, _json);
                var encodedBytes = System.Text.Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                if (writer.BaseStream.Position + encodedBytes > MaxFileBytes)
                {
                    cappedPaths.Add(path);
                    Interlocked.Decrement(ref _pending);
                    Interlocked.Increment(ref _rejected);
                    Service.Log($"[Foretell] Replay Lab segment {Path.GetFileName(path)} reached its 512 MiB hard limit; recording continues only in the compact raw journal.");
                    continue;
                }
                writer.WriteLine(line);
                context = item.Observation.ContextID;
                Interlocked.Decrement(ref _pending);
                Interlocked.Increment(ref _written);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _failure = $"{e.GetType().Name}: {e.Message}";
            Volatile.Write(ref _failed, 1);
            var uncommitted = Interlocked.Exchange(ref _pending, 0);
            while (_queue.TryTake(out _)) { }
            Interlocked.Add(ref _rejected, uncommitted);
            Service.Log($"[Foretell] Background replay writer stopped: {e.Message}; {uncommitted:N0} queued observations were released and counted as rejected.");
        }
        finally
        {
            writer?.Dispose();
        }
    }
}
