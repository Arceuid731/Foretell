using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace BossMod.Foretell;

// Lossless-while-healthy transport journal. Network callbacks only copy/enqueue immutable primitives; compression
// and disk I/O are isolated on this background thread. Both item count and retained payload bytes are bounded so a
// failed/slow disk can never turn telemetry into an out-of-memory crash. Any rejected record is surfaced as an
// explicit degraded sensor state instead of being misreported as complete capture.
internal sealed class ForetellRawWriter : IDisposable
{
    private readonly record struct Item(string Path, ForetellRawRecord Record);

    private const int MaxQueuedRecords = 65536;
    private const long MaxQueuedPayloadBytes = 256L * 1024 * 1024;
    private const int MaxQueuedFeatureWindows = 4096;
    private readonly BlockingCollection<Item> _queue = new(new ConcurrentQueue<Item>(), MaxQueuedRecords);
    private readonly ConcurrentQueue<ForetellRawFeatureWindow> _features = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private long _pendingItems;
    private long _pendingBytes;
    private long _writtenItems;
    private long _writtenBytes;
    private long _rejectedItems;
    private long _pendingFeatureWindows;
    private long _rejectedFeatureWindows;
    private int _disposed;
    private int _failed;
    private string _failure = "";

    public long PendingItems => Interlocked.Read(ref _pendingItems);
    public long PendingBytes => Interlocked.Read(ref _pendingBytes);
    public long WrittenItems => Interlocked.Read(ref _writtenItems);
    public long WrittenBytes => Interlocked.Read(ref _writtenBytes);
    public long RejectedItems => Interlocked.Read(ref _rejectedItems);
    public long PendingFeatureWindows => Interlocked.Read(ref _pendingFeatureWindows);
    public long RejectedFeatureWindows => Interlocked.Read(ref _rejectedFeatureWindows);
    public bool Failed => Volatile.Read(ref _failed) != 0;
    public string Failure => _failure;

    public ForetellRawWriter()
    {
        _thread = new(Run) { IsBackground = true, Name = "Foretell lossless raw journal" };
        _thread.Start();
    }

    public void EnqueueServer(string path, uint territoryID, NetworkState.RawServerIPC packet)
        => Enqueue(new(path, new(ForetellRawRecordKind.ServerIPC, SafeTicks(packet.SendTimestamp), territoryID,
            [(uint)packet.ID, packet.Opcode, packet.Epoch], packet.SourceServerActor, packet.TargetServerActor, 0, packet.Payload)));

    public void EnqueueClient(string path, uint territoryID, NetworkState.RawClientIPC packet)
        => Enqueue(new(path, new(ForetellRawRecordKind.ClientIPC, SafeTicks(packet.SendTimestamp), territoryID,
            [packet.Opcode], 0, 0, 0, packet.Payload)));

    public void EnqueueActorControl(string path, uint territoryID, DateTime at, NetworkState.RawActorControl control)
        => Enqueue(new(path, new(ForetellRawRecordKind.ActorControl, SafeTicks(at), territoryID,
            [control.Command, control.P1, control.P2, control.P3, control.P4, control.P5, control.P6, control.P7, control.P8],
            control.SourceID, control.TargetID, control.Replaying, [])));

    // Preserve the source clock value without DateTime arithmetic: malformed/default client timestamps must never
    // reproduce the startup overflow that older Foretell builds hit while converting or subtracting DateTime.MinValue.
    private static long SafeTicks(DateTime value) => value == default ? DateTime.UtcNow.Ticks : value.Ticks;

    public bool TryDequeueFeature(out ForetellRawFeatureWindow window)
    {
        if (_features.TryDequeue(out var found))
        {
            window = found;
            Interlocked.Decrement(ref _pendingFeatureWindows);
            return true;
        }
        window = null!;
        return false;
    }

    private void Enqueue(Item item)
    {
        if (Volatile.Read(ref _disposed) != 0 || Failed)
        {
            Interlocked.Increment(ref _rejectedItems);
            return;
        }
        var payloadBytes = item.Record.Payload.LongLength;
        var reservedBytes = Interlocked.Add(ref _pendingBytes, payloadBytes);
        if (reservedBytes > MaxQueuedPayloadBytes)
        {
            Interlocked.Add(ref _pendingBytes, -payloadBytes);
            Interlocked.Increment(ref _rejectedItems);
            return;
        }
        Interlocked.Increment(ref _pendingItems);
        try
        {
            if (_queue.TryAdd(item)) return;
        }
        catch (InvalidOperationException) { }
        Interlocked.Decrement(ref _pendingItems);
        Interlocked.Add(ref _pendingBytes, -payloadBytes);
        Interlocked.Increment(ref _rejectedItems);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _queue.CompleteAdding();
        if (!_thread.Join(TimeSpan.FromSeconds(3)))
        {
            // Never block plugin unload indefinitely. Any residual count remains visible in the shutdown log.
            Service.Log($"[Foretell] Raw journal shutdown timed out with {PendingItems:N0} records / {PendingBytes:N0} bytes pending; no records were silently discarded during live capture.");
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
        FileStream? file = null;
        GZipStream? gzip = null;
        BinaryWriter? writer = null;
        var path = "";
        var lastFlush = DateTime.UtcNow;
        var aggregate = new ForetellRawWindowAccumulator();
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable(_stop.Token))
            {
                if (writer == null || !string.Equals(path, item.Path, StringComparison.Ordinal))
                {
                    FlushFeatures(aggregate);
                    writer?.Dispose();
                    gzip?.Dispose();
                    file?.Dispose();
                    path = item.Path;
                    file = new(path, FileMode.Append, FileAccess.Write, FileShare.Read, 65536, FileOptions.SequentialScan);
                    gzip = new(file, CompressionLevel.Fastest, leaveOpen: true);
                    writer = new(gzip, System.Text.Encoding.UTF8, leaveOpen: true);
                    ForetellRawFormat.WriteHeader(writer);
                }

                ForetellRawFormat.Write(writer, item.Record);
                aggregate.Add(item.Record);
                if (aggregate.DurationTicks >= TimeSpan.TicksPerMillisecond * 250 || aggregate.Records >= 256)
                    FlushFeatures(aggregate);
                Interlocked.Decrement(ref _pendingItems);
                Interlocked.Add(ref _pendingBytes, -item.Record.Payload.LongLength);
                Interlocked.Increment(ref _writtenItems);
                Interlocked.Add(ref _writtenBytes, item.Record.Payload.Length);

                if ((DateTime.UtcNow - lastFlush).TotalSeconds >= 1)
                {
                    writer.Flush();
                    lastFlush = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _failure = $"{e.GetType().Name}: {e.Message}";
            Volatile.Write(ref _failed, 1);
            var uncommitted = Interlocked.Exchange(ref _pendingItems, 0);
            Interlocked.Exchange(ref _pendingBytes, 0);
            while (_queue.TryTake(out _)) { }
            Interlocked.Add(ref _rejectedItems, uncommitted);
            Service.Log($"[Foretell] Lossless raw journal stopped: {_failure}; {uncommitted:N0} uncommitted records were released and counted as rejected.");
        }
        finally
        {
            FlushFeatures(aggregate);
            writer?.Dispose();
            gzip?.Dispose();
            file?.Dispose();
        }
    }

    private void FlushFeatures(ForetellRawWindowAccumulator aggregate)
    {
        if (aggregate.Records == 0)
            return;
        var window = aggregate.Finish();
        if (Interlocked.Read(ref _pendingFeatureWindows) >= MaxQueuedFeatureWindows)
        {
            // Exact records are already committed to the raw journal. Only this low-latency derivative is
            // rejected, visibly, and can be rebuilt later by Replay Lab.
            Interlocked.Increment(ref _rejectedFeatureWindows);
            return;
        }
        _features.Enqueue(window);
        Interlocked.Increment(ref _pendingFeatureWindows);
    }

}
