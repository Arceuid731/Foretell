using System.IO;
using System.Numerics;

namespace BossMod.Foretell;

// Compact, versioned reproduction input; contains managed geometry only, never native pointers or actor IDs.
internal static class ForetellCollisionSnapshotIO
{
    private const uint Magic = 0x43525446; // FTRC
    private const int Version = 1;
    private const int MaximumTriangles = 750_000;

    public static void Write(Stream stream, ForetellCollisionSnapshot snapshot)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic); writer.Write(Version);
        WriteVector(writer, snapshot.Player);
        writer.Write(snapshot.Center.X); writer.Write(snapshot.Center.Y);
        writer.Write(snapshot.Radius); writer.Write(snapshot.Resolution);
        writer.Write(snapshot.Colliders); writer.Write(snapshot.NativePrimitives); writer.Write(snapshot.CaptureMilliseconds);
        writer.Write(snapshot.Triangles.Length);
        foreach (var triangle in snapshot.Triangles)
        {
            WriteVector(writer, triangle.A); WriteVector(writer, triangle.B); WriteVector(writer, triangle.C);
            writer.Write(triangle.Material);
        }
    }

    public static ForetellCollisionSnapshot Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version)
            throw new InvalidDataException("Unsupported collision snapshot header");
        var player = ReadVector(reader);
        var center = new Vector2(ReadFinite(reader), ReadFinite(reader));
        var radius = ReadFinite(reader); var resolution = ReadFinite(reader);
        if (radius is < 1 or > 192 || resolution is < .5f or > 4)
            throw new InvalidDataException("Invalid collision window dimensions");
        var colliders = reader.ReadInt32(); var primitives = reader.ReadInt32(); var milliseconds = reader.ReadDouble();
        var count = reader.ReadInt32();
        if (count is < 0 or > MaximumTriangles || colliders < 0 || primitives < 0 || !double.IsFinite(milliseconds))
            throw new InvalidDataException("Invalid collision snapshot bounds");
        var triangles = new ForetellCollisionTriangle[count];
        for (var i = 0; i < count; ++i)
            triangles[i] = new(ReadVector(reader), ReadVector(reader), ReadVector(reader), reader.ReadUInt64());
        if (stream.ReadByte() != -1) throw new InvalidDataException("Trailing collision snapshot data");
        return new(player, center, radius, resolution, triangles, colliders, primitives, milliseconds);
    }

    private static void WriteVector(BinaryWriter writer, Vector3 vector)
    {
        writer.Write(vector.X); writer.Write(vector.Y); writer.Write(vector.Z);
    }

    private static Vector3 ReadVector(BinaryReader reader) => new(ReadFinite(reader), ReadFinite(reader), ReadFinite(reader));
    private static float ReadFinite(BinaryReader reader)
    {
        var value = reader.ReadSingle();
        return float.IsFinite(value) ? value : throw new InvalidDataException("Non-finite collision coordinate");
    }
}
