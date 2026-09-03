using System.Numerics;

namespace BossMod.Foretell;

internal sealed record ArenaBoundaryAnalysis(
    string Fingerprint,
    Vector2 Origin,
    float ReferenceY,
    List<Vector2> Points,
    int Rays,
    int Hits,
    float Area,
    float Compactness,
    float AspectRatio,
    bool ArenaLike);

// Pure managed analysis shared by the live collision scanner and deterministic tests. It deliberately knows
// nothing about BMR modules or encounter identities: only the observed wall distances enter the result.
internal static class ForetellArenaBoundaryCore
{
    public static ArenaBoundaryAnalysis Analyze(Vector2 origin, float referenceY, IReadOnlyList<float> distances,
        IReadOnlyList<bool> hits, float maximumRadius)
    {
        if (distances.Count < 16 || hits.Count != distances.Count || !Finite(origin) || !float.IsFinite(referenceY)
            || !float.IsFinite(maximumRadius) || maximumRadius <= 0)
            return new("", origin, referenceY, [], distances.Count, 0, 0, 0, float.PositiveInfinity, false);

        var points = new List<Vector2>(distances.Count);
        var hitCount = 0;
        var minX = float.MaxValue;
        var maxX = float.MinValue;
        var minZ = float.MaxValue;
        var maxZ = float.MinValue;
        for (var i = 0; i < distances.Count; ++i)
        {
            var distance = float.IsFinite(distances[i]) ? Math.Clamp(distances[i], 1, maximumRadius) : maximumRadius;
            var angle = MathF.Tau * i / distances.Count;
            var point = origin + new Vector2(MathF.Sin(angle), MathF.Cos(angle)) * distance;
            points.Add(point);
            if (hits[i] && distance < maximumRadius - .1f) ++hitCount;
            minX = Math.Min(minX, point.X);
            maxX = Math.Max(maxX, point.X);
            minZ = Math.Min(minZ, point.Y);
            maxZ = Math.Max(maxZ, point.Y);
        }

        var signedTwiceArea = 0f;
        var perimeter = 0f;
        for (var i = 0; i < points.Count; ++i)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            signedTwiceArea += a.X * b.Y - b.X * a.Y;
            perimeter += Vector2.Distance(a, b);
        }
        var area = Math.Abs(signedTwiceArea) * .5f;
        var compactness = perimeter > .01f ? Math.Clamp(4 * MathF.PI * area / (perimeter * perimeter), 0, 1) : 0;
        var width = Math.Max(.01f, maxX - minX);
        var height = Math.Max(.01f, maxZ - minZ);
        var aspect = Math.Max(width, height) / Math.Min(width, height);
        var hitRatio = hitCount / (float)distances.Count;
        var arenaLike = hitRatio >= .70f && Math.Min(width, height) >= 12 && area >= 150
            && aspect <= 2.6f && compactness >= .18f;
        var fingerprint = Fingerprint(origin, distances, hits, maximumRadius);
        return new(fingerprint, origin, referenceY, points, distances.Count, hitCount, area, compactness, aspect, arenaLike);
    }

    public static bool Contains(IReadOnlyList<Vector2> polygon, Vector2 point)
    {
        if (polygon.Count < 3 || !Finite(point)) return false;
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if ((a.Y > point.Y) != (b.Y > point.Y)
                && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    // A room outline is not enough to call an encounter a boss. Require a leading enemy plus either a strong
    // health gap from the synced player or a clearly boss-sized hitbox backed by a smaller health gap.
    public static bool IsBossCandidate(uint enemyMaximumHP, uint leadingEnemyMaximumHP, uint playerMaximumHP, float hitboxRadius)
    {
        if (leadingEnemyMaximumHP == 0 || enemyMaximumHP < leadingEnemyMaximumHP * .85f)
            return false;
        var healthEvidence = playerMaximumHP != 0 && enemyMaximumHP >= playerMaximumHP * 2f;
        var sizeEvidence = float.IsFinite(hitboxRadius) && hitboxRadius >= 2.5f
            && (playerMaximumHP == 0 || enemyMaximumHP >= playerMaximumHP * 1.25f);
        return healthEvidence || sizeEvidence;
    }

    private static string Fingerprint(Vector2 origin, IReadOnlyList<float> distances, IReadOnlyList<bool> hits, float maximumRadius)
    {
        var hash = 14695981039346656037UL;
        Mix((uint)MathF.Round(origin.X / 2));
        Mix((uint)MathF.Round(origin.Y / 2));
        for (var i = 0; i < distances.Count; ++i)
        {
            var distance = float.IsFinite(distances[i]) ? Math.Clamp(distances[i], 0, maximumRadius) : maximumRadius;
            Mix((uint)MathF.Round(distance * 2) | (hits[i] ? 0x80000000u : 0));
        }
        return hash.ToString("X16");

        void Mix(uint value)
        {
            unchecked
            {
                hash ^= value;
                hash *= 1099511628211UL;
            }
        }
    }

    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
}
