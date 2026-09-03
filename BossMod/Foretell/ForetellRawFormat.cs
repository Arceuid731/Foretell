using System.IO.Compression;
using System.IO;
using System.Threading;
using System.Text;

namespace BossMod.Foretell;

internal enum ForetellRawRecordKind : byte { ServerIPC = 1, ClientIPC = 2, ActorControl = 3 }

internal sealed record ForetellRawOpcodeFeature(int Count, long PayloadBytes, int MinLength, int MaxLength,
    ulong SequenceHash, double[] ByteMeans, double[] ByteVariances);

internal sealed record ForetellRawFeatureWindow(DateTime At, uint TerritoryID, int ServerPackets, int ClientPackets,
    int ActorControls, long PayloadBytes, Dictionary<uint, int> Opcodes, double[] BinaryBuckets,
    Dictionary<uint, ForetellRawOpcodeFeature> OpcodeFeatures, Dictionary<ulong, int> Transitions);

internal sealed record ForetellRawRecord(ForetellRawRecordKind Kind, long TimestampTicks, uint TerritoryID,
    uint[] Arguments, ulong SourceID, ulong TargetID, byte Flags, byte[] Payload);

internal sealed class ForetellRawReadReport
{
    public string Path { get; init; } = "";
    public int Schema { get; set; }
    public long Records { get; set; }
    public long ServerPackets { get; set; }
    public long ClientPackets { get; set; }
    public long ActorControls { get; set; }
    public long PayloadBytes { get; set; }
    public DateTime FirstAt { get; set; }
    public DateTime LastAt { get; set; }
    public Dictionary<uint, long> Opcodes { get; } = [];
    public List<string> Errors { get; } = [];
    public List<ForetellRawFeatureWindow> Windows { get; } = [];
    public bool Complete => Errors.Count == 0;
}

// One implementation is shared by live capture and offline replay. This is intentionally kept independent of
// Dalamud so the byte-for-byte feature contract can be covered by the standalone deterministic test harness.
internal sealed class ForetellRawWindowAccumulator
{
    private const int BucketCount = 64;
    private const int StructuralByteCount = 16;
    private sealed class OpcodeAccumulator
    {
        public int Count;
        public long Bytes;
        public int MinLength = int.MaxValue;
        public int MaxLength;
        public ulong SequenceHash = 14695981039346656037UL;
        public readonly double[] Sums = new double[StructuralByteCount];
        public readonly double[] Squares = new double[StructuralByteCount];
        public readonly int[] Samples = new int[StructuralByteCount];
    }
    private readonly Dictionary<uint, int> _opcodes = [];
    private readonly Dictionary<uint, OpcodeAccumulator> _opcodeFeatures = [];
    private readonly Dictionary<ulong, int> _transitions = [];
    private readonly double[] _buckets = new double[BucketCount];
    private long _firstTicks, _lastTicks;
    private uint _territory;
    private int _server, _client, _control;
    private long _bytes;
    private uint? _previousOpcode;
    public int Records => _server + _client + _control;
    public long DurationTicks => Math.Max(0, _lastTicks - _firstTicks);

    public void Add(ForetellRawRecord item)
    {
        if (Records == 0) { _firstTicks = item.TimestampTicks; _territory = item.TerritoryID; }
        _lastTicks = item.TimestampTicks;
        if (item.Kind == ForetellRawRecordKind.ServerIPC) ++_server;
        else if (item.Kind == ForetellRawRecordKind.ClientIPC) ++_client;
        else ++_control;
        _bytes += item.Payload.LongLength;
        var opcode = item.Arguments.Length == 0 ? 0 : item.Arguments[0];
        var opcodeKey = ((uint)item.Kind << 24) | (opcode & 0x00FFFFFFu);
        _opcodes[opcodeKey] = _opcodes.GetValueOrDefault(opcodeKey) + 1;
        if (_previousOpcode is uint previous)
        {
            var transition = ((ulong)previous << 32) | opcodeKey;
            _transitions[transition] = _transitions.GetValueOrDefault(transition) + 1;
        }
        _previousOpcode = opcodeKey;
        if (!_opcodeFeatures.TryGetValue(opcodeKey, out var structural))
            _opcodeFeatures[opcodeKey] = structural = new();
        ++structural.Count;
        structural.Bytes += item.Payload.LongLength;
        structural.MinLength = Math.Min(structural.MinLength, item.Payload.Length);
        structural.MaxLength = Math.Max(structural.MaxLength, item.Payload.Length);
        unchecked
        {
            structural.SequenceHash ^= (uint)item.Payload.Length;
            structural.SequenceHash *= 1099511628211UL;
        }
        for (var i = 0; i < ForetellRawFormat.ArgumentCount; ++i)
            Mix(i < item.Arguments.Length ? item.Arguments[i] : 0);
        Mix(item.SourceID); Mix(item.TargetID); Mix(item.Flags);
        for (var i = 0; i < item.Payload.Length; ++i)
        {
            var bucket = (int)((opcode * 16777619u + (uint)i) % BucketCount);
            _buckets[bucket] += (item.Payload[i] - 127.5) / 127.5;
            unchecked
            {
                structural.SequenceHash ^= item.Payload[i];
                structural.SequenceHash *= 1099511628211UL;
            }
            if (i < StructuralByteCount)
            {
                var normalized = item.Payload[i] / 255d;
                structural.Sums[i] += normalized;
                structural.Squares[i] += normalized * normalized;
                ++structural.Samples[i];
            }
        }
    }

    public ForetellRawFeatureWindow Finish()
    {
        if (Records == 0)
            throw new InvalidOperationException("cannot finish an empty raw feature window");
        var opcodeFeatures = _opcodeFeatures.ToDictionary(pair => pair.Key, pair =>
        {
            var value = pair.Value;
            var means = new double[StructuralByteCount];
            var variances = new double[StructuralByteCount];
            for (var i = 0; i < StructuralByteCount; ++i)
            {
                var count = Math.Max(1, value.Samples[i]);
                means[i] = value.Sums[i] / count;
                variances[i] = Math.Max(0, value.Squares[i] / count - means[i] * means[i]);
            }
            return new ForetellRawOpcodeFeature(value.Count, value.Bytes,
                value.MinLength == int.MaxValue ? 0 : value.MinLength, value.MaxLength,
                value.SequenceHash, means, variances);
        });
        var result = new ForetellRawFeatureWindow(
            new DateTime(Math.Clamp(_lastTicks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc),
            _territory, _server, _client, _control, _bytes, new(_opcodes), _buckets.ToArray(),
            opcodeFeatures, new(_transitions));
        _opcodes.Clear();
        _opcodeFeatures.Clear();
        _transitions.Clear();
        Array.Clear(_buckets);
        _firstTicks = _lastTicks = 0;
        _territory = 0;
        _server = _client = _control = 0;
        _bytes = 0;
        _previousOpcode = null;
        return result;
    }

    private void Mix(ulong value)
    {
        unchecked
        {
            var hash = value * 11400714819323198485UL;
            _buckets[(int)(hash % BucketCount)] += (hash & 0x8000000000000000UL) == 0 ? 1 : -1;
        }
    }
}

internal static class ForetellRawFormat
{
    public static readonly long FeatureWindowTicks = TimeSpan.TicksPerSecond;
    public const int FeatureWindowMaxRecords = 1024;
    public const ulong Magic = 0x315741524C5446UL;
    public const int CurrentSchema = 2;
    public const int ArgumentCount = 10;
    public const int MaxPayloadBytes = 64 * 1024 * 1024;
    public const int MaxInMemoryWindows = 250_000;
    public const int MaxOpcodeFamilies = 1_000_000;
    public const long MaxIndexedRecords = 50_000_000;

    public static void WriteHeader(BinaryWriter writer)
    {
        writer.Write(Magic);
        writer.Write(CurrentSchema);
    }

    public static void Write(BinaryWriter writer, ForetellRawRecord item)
    {
        writer.Write((byte)item.Kind);
        writer.Write(item.TimestampTicks);
        writer.Write(item.TerritoryID);
        for (var i = 0; i < ArgumentCount; ++i)
            writer.Write(i < item.Arguments.Length ? item.Arguments[i] : 0);
        writer.Write(item.SourceID);
        writer.Write(item.TargetID);
        writer.Write(item.Flags);
        writer.Write(item.Payload.Length);
        writer.Write(item.Payload);
    }

    public static ForetellRawReadReport Read(string path, uint legacyTerritory = 0, CancellationToken cancellationToken = default)
    {
        var report = new ForetellRawReadReport { Path = path };
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip, Encoding.UTF8);
            if (reader.ReadUInt64() != Magic)
                throw new InvalidDataException("not a Foretell raw journal");
            report.Schema = reader.ReadInt32();
            if (report.Schema is < 1 or > CurrentSchema)
                throw new InvalidDataException($"unsupported raw schema {report.Schema}");

            var aggregate = new ForetellRawWindowAccumulator();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte kind;
                try { kind = reader.ReadByte(); }
                catch (EndOfStreamException) { break; }
                var record = ReadRecord(reader, kind, report.Schema, legacyTerritory);
                Accumulate(report, aggregate, record);
                if (report.Records > MaxIndexedRecords)
                    throw new InvalidDataException($"journal exceeds the {MaxIndexedRecords:N0}-record in-memory analysis safety limit");
                if (report.Opcodes.Count > MaxOpcodeFamilies)
                    throw new InvalidDataException($"journal exceeds the {MaxOpcodeFamilies:N0}-opcode-family safety limit");
                if (aggregate.DurationTicks >= FeatureWindowTicks || aggregate.Records >= FeatureWindowMaxRecords)
                {
                    if (report.Windows.Count >= MaxInMemoryWindows)
                        throw new InvalidDataException($"journal exceeds the {MaxInMemoryWindows:N0}-window in-memory analysis safety limit");
                    report.Windows.Add(aggregate.Finish());
                }
            }
            if (aggregate.Records > 0 && report.Windows.Count >= MaxInMemoryWindows)
                throw new InvalidDataException($"journal exceeds the {MaxInMemoryWindows:N0}-window in-memory analysis safety limit");
            if (aggregate.Records > 0)
                report.Windows.Add(aggregate.Finish());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { report.Errors.Add($"{e.GetType().Name}: {e.Message}"); }
        return report;
    }

    private static ForetellRawRecord ReadRecord(BinaryReader reader, byte kindByte, int schema, uint legacyTerritory)
    {
        if (kindByte is < 1 or > 3)
            throw new InvalidDataException($"invalid record kind {kindByte}");
        var ticks = reader.ReadInt64();
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            throw new InvalidDataException($"invalid timestamp {ticks}");
        var territory = schema >= 2 ? reader.ReadUInt32() : legacyTerritory;
        var args = new uint[ArgumentCount];
        for (var i = 0; i < args.Length; ++i) args[i] = reader.ReadUInt32();
        var source = reader.ReadUInt64();
        var target = reader.ReadUInt64();
        var flags = reader.ReadByte();
        var length = reader.ReadInt32();
        if (length is < 0 or > MaxPayloadBytes)
            throw new InvalidDataException($"invalid payload length {length}");
        var payload = reader.ReadBytes(length);
        if (payload.Length != length)
            throw new EndOfStreamException($"truncated payload: wanted {length}, got {payload.Length}");
        return new((ForetellRawRecordKind)kindByte, ticks, territory, args, source, target, flags, payload);
    }

    private static void Accumulate(ForetellRawReadReport report, ForetellRawWindowAccumulator aggregate, ForetellRawRecord record)
    {
        ++report.Records;
        report.PayloadBytes += record.Payload.LongLength;
        if (record.Kind == ForetellRawRecordKind.ServerIPC) ++report.ServerPackets;
        else if (record.Kind == ForetellRawRecordKind.ClientIPC) ++report.ClientPackets;
        else ++report.ActorControls;
        var at = new DateTime(record.TimestampTicks, DateTimeKind.Utc);
        if (report.FirstAt == default || at < report.FirstAt) report.FirstAt = at;
        if (at > report.LastAt) report.LastAt = at;
        var opcodeKey = ((uint)record.Kind << 24) | (record.Arguments[0] & 0x00FFFFFFu);
        report.Opcodes[opcodeKey] = report.Opcodes.GetValueOrDefault(opcodeKey) + 1;
        aggregate.Add(record);
    }

}
