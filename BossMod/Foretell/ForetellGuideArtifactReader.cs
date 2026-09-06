using System.IO;
using System.Security.Cryptography;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    internal static byte[] ReadGuideArtifact(string directory, GuideCaptureFile guide)
    {
        if (!ForetellCapture.ValidGuideFilename(guide.File)) throw new InvalidDataException("Unsafe guide artifact path");
        var file = new FileInfo(Path.Combine(directory, guide.File));
        if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length != guide.Bytes || file.Length > ForetellCapture.GuideExpandedLimit + 65536)
            throw new InvalidDataException("Invalid guide artifact size/path");
        var bytes = File.ReadAllBytes(file.FullName);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), guide.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Guide artifact integrity check failed");
        return bytes;
    }
}
