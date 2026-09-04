using System.Diagnostics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;

namespace BossMod.Foretell;

internal static unsafe class ForetellCollisionMeshSource
{
    private const int MaximumSnapshotTriangles = 750_000;
    private const int MaximumNodePrimitives = 1_000_000;
    private const double MaximumCaptureMilliseconds = 8;

    // The scene is native mutable memory and must only be visited on the framework thread. We copy a bounded local
    // triangle soup in one guarded pass; the expensive heightfield/raster work then runs entirely on managed data.
    public static bool TryCapture(BGCollisionModule* module, Vector3 player, float radius, float resolution,
        out ForetellCollisionSnapshot? snapshot, out string reason)
    {
        snapshot = null;
        reason = "collision scene unavailable";
        if (module == null || module->ShuttingDown || module->SceneManager == null || module->LoadInProgressCounter > 0)
            return false;

        var started = Stopwatch.GetTimestamp();
        var triangles = new List<ForetellCollisionTriangle>(16_384);
        var seenMeshes = new HashSet<nint>();
        var colliders = 0;
        var nativePrimitives = 0;
        var complete = true;
        var center = new Vector2(player.X, player.Z);
        try
        {
            foreach (var sceneRef in module->SceneManager->Scenes)
            {
                var scene = sceneRef->Scene;
                if (scene == null)
                    continue;
                foreach (var collider in scene->Colliders)
                {
                    if (collider == null || (collider->VisibilityFlags & 1) == 0)
                        continue;
                    switch (collider->GetColliderType())
                    {
                        case ColliderType.Mesh:
                            complete &= CaptureMesh((ColliderMesh*)collider);
                            break;
                        case ColliderType.Streamed:
                            var streamed = (ColliderStreamed*)collider;
                            if (streamed->Header == null || streamed->Elements == null || streamed->NumMeshesLoading > 0)
                                continue;
                            var count = streamed->Header->NumMeshes;
                            if (count is < 0 or > 1_000_000)
                            {
                                complete = false;
                                continue;
                            }
                            for (var i = 0; i < count && complete; ++i)
                                complete &= CaptureMesh(streamed->Elements[i].Mesh);
                            break;
                        case ColliderType.Box:
                            complete &= CaptureBox((ColliderBox*)collider);
                            break;
                        case ColliderType.Cylinder:
                            complete &= CaptureCylinder((ColliderCylinder*)collider);
                            break;
                        case ColliderType.Plane:
                        case ColliderType.PlaneTwoSided:
                            complete &= CapturePlane((ColliderPlane*)collider);
                            break;
                    }
                    if (!complete || Stopwatch.GetElapsedTime(started).TotalMilliseconds > MaximumCaptureMilliseconds)
                    {
                        complete = false;
                        break;
                    }
                }
                if (!complete)
                    break;
            }
        }
        catch (Exception e)
        {
            reason = $"native mesh snapshot rejected safely: {e.GetType().Name}";
            return false;
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (!complete)
        {
            reason = elapsed > MaximumCaptureMilliseconds
                ? $"native mesh snapshot exceeded {MaximumCaptureMilliseconds:F0} ms ceiling"
                : "native mesh snapshot exceeded structural safety bounds";
            return false;
        }
        if (triangles.Count == 0)
        {
            reason = "no loaded PCB collision triangles in local radius";
            return false;
        }
        snapshot = new(player, radius, resolution, triangles.ToArray(), colliders, nativePrimitives, elapsed);
        reason = $"copied {triangles.Count:N0} local PCB triangles";
        return true;

        bool CaptureMesh(ColliderMesh* collider)
        {
            if (collider == null || collider->MeshIsSimple || collider->Mesh == null || !collider->Loaded
                || !seenMeshes.Add((nint)collider))
                return true;
            var bounds = collider->WorldBoundingBox;
            if (!RectangleIntersectsCircle(bounds.Min.X, bounds.Min.Z, bounds.Max.X, bounds.Max.Z, center, radius))
                return true;
            ++colliders;
            var mesh = (MeshPCB*)collider->Mesh;
            var world = collider->World;
            return CaptureNode(mesh->RootNode, ref world);
        }

        bool CaptureNode(MeshPCB.FileNode* node, ref Matrix4x3 world)
        {
            if (node == null)
                return true;
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > MaximumCaptureMilliseconds)
                return false;
            if (!NodeIntersectsCircle(node->LocalBounds, ref world, center, radius))
                return true;

            var vertices = node->NumVertsRaw + node->NumVertsCompressed;
            var primitives = node->NumPrims;
            if (vertices < 0 || primitives < 0 || primitives > MaximumNodePrimitives)
                return false;
            nativePrimitives += primitives;
            for (var i = 0; i < primitives; ++i)
            {
                if ((i & 0x7FF) == 0 && Stopwatch.GetElapsedTime(started).TotalMilliseconds > MaximumCaptureMilliseconds)
                    return false;
                var primitive = node->Primitives[i];
                if (primitive.V1 >= vertices || primitive.V2 >= vertices || primitive.V3 >= vertices)
                    continue;
                var a = world.TransformCoordinate(node->Vertex(primitive.V1));
                var b = world.TransformCoordinate(node->Vertex(primitive.V2));
                var c = world.TransformCoordinate(node->Vertex(primitive.V3));
                if (!TriangleIntersectsCircle(a, b, c, center, radius))
                    continue;
                if (triangles.Count >= MaximumSnapshotTriangles)
                    return false;
                triangles.Add(new(a, b, c));
            }
            return CaptureNode(node->Child1, ref world) && CaptureNode(node->Child2, ref world);
        }

        bool CaptureBox(ColliderBox* collider)
        {
            if (collider == null)
                return true;
            ++colliders;
            var world = collider->World;
            Span<Vector3> corners =
            [
                new(-1, -1, -1), new(-1, -1, 1), new(-1, 1, -1), new(-1, 1, 1),
                new(1, -1, -1), new(1, -1, 1), new(1, 1, -1), new(1, 1, 1)
            ];
            for (var i = 0; i < corners.Length; ++i)
                corners[i] = world.TransformCoordinate(corners[i]);
            ReadOnlySpan<(int A, int B, int C)> faces =
            [
                (0, 1, 3), (0, 3, 2), (4, 6, 7), (4, 7, 5),
                (0, 4, 5), (0, 5, 1), (2, 3, 7), (2, 7, 6),
                (0, 2, 6), (0, 6, 4), (1, 5, 7), (1, 7, 3)
            ];
            foreach (var face in faces)
                if (!AddTriangle(corners[face.A], corners[face.B], corners[face.C]))
                    return false;
            return true;
        }

        bool CaptureCylinder(ColliderCylinder* collider)
        {
            if (collider == null)
                return true;
            ++colliders;
            var world = collider->World;
            const int segments = 24;
            var topCenter = world.TransformCoordinate(new(0, 1, 0));
            var bottomCenter = world.TransformCoordinate(new(0, -1, 0));
            for (var i = 0; i < segments; ++i)
            {
                var a = MathF.Tau * i / segments;
                var b = MathF.Tau * (i + 1) / segments;
                var bottomA = world.TransformCoordinate(new(MathF.Sin(a), -1, MathF.Cos(a)));
                var bottomB = world.TransformCoordinate(new(MathF.Sin(b), -1, MathF.Cos(b)));
                var topA = world.TransformCoordinate(new(MathF.Sin(a), 1, MathF.Cos(a)));
                var topB = world.TransformCoordinate(new(MathF.Sin(b), 1, MathF.Cos(b)));
                if (!AddTriangle(bottomA, topA, topB) || !AddTriangle(bottomA, topB, bottomB)
                    || !AddTriangle(topCenter, topA, topB) || !AddTriangle(bottomCenter, bottomB, bottomA))
                    return false;
            }
            return true;
        }

        bool CapturePlane(ColliderPlane* collider)
        {
            if (collider == null)
                return true;
            ++colliders;
            var world = collider->World;
            var a = world.TransformCoordinate(new(-1, 1, 0));
            var b = world.TransformCoordinate(new(-1, -1, 0));
            var c = world.TransformCoordinate(new(1, -1, 0));
            var d = world.TransformCoordinate(new(1, 1, 0));
            return AddTriangle(a, b, c) && AddTriangle(a, c, d);
        }

        bool AddTriangle(Vector3 a, Vector3 b, Vector3 c)
        {
            ++nativePrimitives;
            if (!TriangleIntersectsCircle(a, b, c, center, radius))
                return true;
            if (triangles.Count >= MaximumSnapshotTriangles)
                return false;
            triangles.Add(new(a, b, c));
            return true;
        }
    }

    private static bool NodeIntersectsCircle(AABB local, ref Matrix4x3 world, Vector2 center, float radius)
    {
        var minX = float.MaxValue;
        var minZ = float.MaxValue;
        var maxX = float.MinValue;
        var maxZ = float.MinValue;
        for (var corner = 0; corner < 8; ++corner)
        {
            var localPoint = new Vector3(
                (corner & 1) == 0 ? local.Min.X : local.Max.X,
                (corner & 2) == 0 ? local.Min.Y : local.Max.Y,
                (corner & 4) == 0 ? local.Min.Z : local.Max.Z);
            var point = world.TransformCoordinate(localPoint);
            minX = Math.Min(minX, point.X);
            minZ = Math.Min(minZ, point.Z);
            maxX = Math.Max(maxX, point.X);
            maxZ = Math.Max(maxZ, point.Z);
        }
        return RectangleIntersectsCircle(minX, minZ, maxX, maxZ, center, radius);
    }

    private static bool RectangleIntersectsCircle(float minX, float minZ, float maxX, float maxZ, Vector2 center, float radius)
    {
        if (!float.IsFinite(minX) || !float.IsFinite(minZ) || !float.IsFinite(maxX) || !float.IsFinite(maxZ))
            return false;
        var closestX = Math.Clamp(center.X, Math.Min(minX, maxX), Math.Max(minX, maxX));
        var closestZ = Math.Clamp(center.Y, Math.Min(minZ, maxZ), Math.Max(minZ, maxZ));
        var dx = closestX - center.X;
        var dz = closestZ - center.Y;
        return dx * dx + dz * dz <= radius * radius;
    }

    private static bool TriangleIntersectsCircle(Vector3 a, Vector3 b, Vector3 c, Vector2 center, float radius)
    {
        var minX = Math.Min(a.X, Math.Min(b.X, c.X));
        var minZ = Math.Min(a.Z, Math.Min(b.Z, c.Z));
        var maxX = Math.Max(a.X, Math.Max(b.X, c.X));
        var maxZ = Math.Max(a.Z, Math.Max(b.Z, c.Z));
        return RectangleIntersectsCircle(minX, minZ, maxX, maxZ, center, radius);
    }
}
