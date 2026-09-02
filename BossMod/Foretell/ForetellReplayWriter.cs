using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace BossMod.Foretell;

// Replay serialization and disk I/O must never run on the framework/render thread. The queue is intentionally
// unbounded: recording is explicit opt-in and raw transport payloads must not be silently truncated or dropped.
internal sealed class ForetellReplayWriter : IDisposable
{
    private readonly record struct Item(string Path, ForetellObservation Observation);

    private readonly BlockingCollection<Item> _queue = new(new ConcurrentQueue<Item>());
    private readonly CancellationTokenSource _stop = new();
    private readonly JsonSerializerOptions _json;
    private readonly Thread _thread;
    private int _disposed;

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
        if (Volatile.Read(ref _disposed) != 0)
            return;
        try { _queue.Add(new(path, observation)); }
        catch (InvalidOperationException) { }
    }

    // Used only by the explicit Replay Lab action, never by the per-frame update path.
    public void Drain(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (_queue.Count != 0 && DateTime.UtcNow < until)
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
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable(_stop.Token))
            {
                if (writer == null || !string.Equals(path, item.Path, StringComparison.Ordinal))
                {
                    writer?.Dispose();
                    path = item.Path;
                    writer = new(path, append: true) { AutoFlush = true };
                }
                writer.WriteLine(JsonSerializer.Serialize(item.Observation, _json));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Service.Log($"[Foretell] Background replay writer stopped: {e.Message}");
        }
        finally
        {
            writer?.Dispose();
        }
    }
}
