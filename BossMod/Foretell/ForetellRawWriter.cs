using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace BossMod.Foretell;

// Lossless transport journal. Network callbacks only copy/enqueue immutable primitives; compression and disk I/O
// are isolated on this background thread. The queue is deliberately unbounded: saturation is reported, never
// hidden by dropping packets. Files are local, per territory session, and are never uploaded automatically.
internal sealed class ForetellRawWriter : IDisposable
{
    private enum RecordKind : byte { ServerIPC = 1, ClientIPC = 2, ActorControl = 3 }

    private readonly record struct Item(string Path, RecordKind Kind, long TimestampTicks,
        uint A0, uint A1, uint A2, uint A3, uint A4, uint A5, uint A6, uint A7, uint A8, uint A9,
        ulong U0, ulong U1, byte B0, byte[] Payload);

    private readonly BlockingCollection<Item> _queue = new(new ConcurrentQueue<Item>());
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private long _pendingItems;
    private long _pendingBytes;
    private long _writtenItems;
    private long _writtenBytes;
    private long _rejectedItems;
    private int _disposed;
    private int _failed;
    private string _failure = "";

    public long PendingItems => Interlocked.Read(ref _pendingItems);
    public long PendingBytes => Interlocked.Read(ref _pendingBytes);
    public long WrittenItems => Interlocked.Read(ref _writtenItems);
    public long WrittenBytes => Interlocked.Read(ref _writtenBytes);
    public long RejectedItems => Interlocked.Read(ref _rejectedItems);
    public bool Failed => Volatile.Read(ref _failed) != 0;
    public string Failure => _failure;

    public ForetellRawWriter()
    {
        _thread = new(Run) { IsBackground = true, Name = "Foretell lossless raw journal" };
        _thread.Start();
    }

    public void EnqueueServer(string path, NetworkState.RawServerIPC packet)
        => Enqueue(new(path, RecordKind.ServerIPC, SafeTicks(packet.SendTimestamp),
            (uint)packet.ID, packet.Opcode, packet.Epoch, 0, 0, 0, 0, 0, 0, 0,
            packet.SourceServerActor, packet.TargetServerActor, 0, packet.Payload));

    public void EnqueueClient(string path, NetworkState.RawClientIPC packet)
        => Enqueue(new(path, RecordKind.ClientIPC, SafeTicks(packet.SendTimestamp),
            packet.Opcode, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, packet.Payload));

    public void EnqueueActorControl(string path, DateTime at, NetworkState.RawActorControl control)
        => Enqueue(new(path, RecordKind.ActorControl, SafeTicks(at),
            control.Command, control.P1, control.P2, control.P3, control.P4, control.P5, control.P6, control.P7, control.P8, 0,
            control.SourceID, control.TargetID, control.Replaying, Array.Empty<byte>()));

    // Preserve the source clock value without DateTime arithmetic: malformed/default client timestamps must never
    // reproduce the startup overflow that older Foretell builds hit while converting or subtracting DateTime.MinValue.
    private static long SafeTicks(DateTime value) => value == default ? DateTime.UtcNow.Ticks : value.Ticks;

    private void Enqueue(Item item)
    {
        if (Volatile.Read(ref _disposed) != 0 || Failed)
        {
            Interlocked.Increment(ref _rejectedItems);
            return;
        }
        Interlocked.Increment(ref _pendingItems);
        Interlocked.Add(ref _pendingBytes, item.Payload.Length);
        try { _queue.Add(item); }
        catch (InvalidOperationException)
        {
            Interlocked.Decrement(ref _pendingItems);
            Interlocked.Add(ref _pendingBytes, -item.Payload.Length);
        }
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
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable(_stop.Token))
            {
                if (writer == null || !string.Equals(path, item.Path, StringComparison.Ordinal))
                {
                    writer?.Dispose();
                    gzip?.Dispose();
                    file?.Dispose();
                    path = item.Path;
                    file = new(path, FileMode.Append, FileAccess.Write, FileShare.Read, 65536, FileOptions.SequentialScan);
                    gzip = new(file, CompressionLevel.Fastest, leaveOpen: true);
                    writer = new(gzip, System.Text.Encoding.UTF8, leaveOpen: true);
                    writer.Write(0x315741524C5446UL); // FTLRAW1, little endian
                    writer.Write(1); // schema
                }

                writer.Write((byte)item.Kind);
                writer.Write(item.TimestampTicks);
                writer.Write(item.A0); writer.Write(item.A1); writer.Write(item.A2); writer.Write(item.A3); writer.Write(item.A4);
                writer.Write(item.A5); writer.Write(item.A6); writer.Write(item.A7); writer.Write(item.A8); writer.Write(item.A9);
                writer.Write(item.U0); writer.Write(item.U1); writer.Write(item.B0);
                writer.Write(item.Payload.Length);
                writer.Write(item.Payload);
                Interlocked.Decrement(ref _pendingItems);
                Interlocked.Add(ref _pendingBytes, -item.Payload.Length);
                Interlocked.Increment(ref _writtenItems);
                Interlocked.Add(ref _writtenBytes, item.Payload.Length);

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
            Service.Log($"[Foretell] Lossless raw journal stopped: {_failure}; {PendingItems:N0} queued records remain explicitly uncommitted.");
        }
        finally
        {
            writer?.Dispose();
            gzip?.Dispose();
            file?.Dispose();
        }
    }
}
