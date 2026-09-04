using System.Diagnostics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision.Math;

namespace BossMod.Foretell;

internal static unsafe class ForetellCollisionMeshSource
{
    [DllImport("kernel32.dll", EntryPoint = "TryAcquireSRWLockShared")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool TryAcquireSceneLock(nint* sceneLock);

    [DllImport("kernel32.dll", EntryPoint = "ReleaseSRWLockShared")]
    private static extern void ReleaseSceneLock(nint* sceneLock);

    private const int MaximumSnapshotTriangles = 750_000;
    private const int MaximumScenePrimitives = 4_000_000;
    private const double MaximumCaptureMilliseconds = 20;
    private const double MaximumFingerprintMilliseconds = 2;

    public static bool TrySceneFingerprint(BGCollisionModule* module, Vector2 center, float radius,
        out ulong fingerprint, out int colliders)
    {
        fingerprint = 0;
        colliders = 0;
        if (module == null || module->ShuttingDown || module->SceneManager == null || module->LoadInProgressCounter > 0)
            return false;
        if (!TryAcquireSceneLock(&module->UpdateTaskLock))
            return false;
        var started = Stopwatch.GetTimestamp();
        var hash = 14695981039346656037UL;
        var inspected = 0;
        try
        {
            foreach (var sceneRef in module->SceneManager->Scenes)
            {
                var scene = sceneRef->Scene;
                if (scene == null)
                    continue;
                foreach (var collider in scene->Colliders)
                {
                    if (collider == null)
                        continue;
                    if ((inspected++ & 0x3F) == 0 && Stopwatch.GetElapsedTime(started).TotalMilliseconds > MaximumFingerprintMilliseconds)
                        return false;
                    var type = collider->GetColliderType();
                    switch (type)
                    {
                        case ColliderType.Mesh:
                        {
                            var bounds = ((ColliderMesh*)collider)->WorldBoundingBox;
                            if (!RectangleIntersectsCircle(bounds.Min.X, bounds.Min.Z, bounds.Max.X, bounds.Max.Z, center, radius))
                                continue;
                            Mix(BitConverter.SingleToUInt32Bits(bounds.Min.X));
                            Mix(BitConverter.SingleToUInt32Bits(bounds.Min.Y));
                            Mix(BitConverter.SingleToUInt32Bits(bounds.Min.Z));
                            Mix(BitConverter.SingleToUInt32Bits(bounds.Max.X));
                            Mix(BitConverter.SingleToUInt32Bits(bounds.Max.Y));
                            Mix(BitConverter.SingleToUInt32Bits(bounds.Max.Z));
                            break;
                        }
                        case ColliderType.Streamed:
                        {
                            var streamed = (ColliderStreamed*)collider;
                            if (!RectangleIntersectsCircle(streamed->StreamedMinX, streamed->StreamedMinZ,
                                    streamed->StreamedMaxX, streamed->StreamedMaxZ, center, radius))
                                continue;
                            Mix(BitConverter.SingleToUInt32Bits(streamed->StreamedMinX));
                            Mix(BitConverter.SingleToUInt32Bits(streamed->StreamedMinZ));
                            Mix(BitConverter.SingleToUInt32Bits(streamed->StreamedMaxX));
                            Mix(BitConverter.SingleToUInt32Bits(streamed->StreamedMaxZ));
                            break;
                        }
                    }
                    ++colliders;
                    Mix((ulong)(nuint)collider);
                    Mix((ulong)type);
                    Mix((ulong)(uint)collider->VisibilityFlags);
                    Mix(collider->LayerMask);
                    Mix(collider->ObjectMaterialValue);
                    Mix(collider->ObjectMaterialMask);
                    switch (type)
                    {
                        case ColliderType.Mesh:
                            Mix(((ColliderMesh*)collider)->Loaded ? 1UL : 0UL);
                            Mix((ulong)(nuint)((ColliderMesh*)collider)->Mesh);
                            MixTransform(((ColliderMesh*)collider)->World);
                            break;
                        case ColliderType.Box:
                            MixTransform(((ColliderBox*)collider)->World);
                            break;
                        case ColliderType.Cylinder:
                            MixTransform(((ColliderCylinder*)collider)->World);
                            break;
                        case ColliderType.Sphere:
                            MixTransform(((ColliderSphere*)collider)->World);
                            break;
                        case ColliderType.Plane:
                        case ColliderType.PlaneTwoSided:
                            MixTransform(((ColliderPlane*)collider)->World);
                            break;
                        case ColliderType.Streamed:
                            var streamed = (ColliderStreamed*)collider;
                            Mix((ulong)(uint)streamed->NumMeshesLoading);
                            Mix(streamed->Header == null ? 0UL : (ulong)(uint)streamed->Header->NumMeshes);
                            break;
                    }
                }
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseSceneLock(&module->UpdateTaskLock);
        }
        if (colliders == 0)
            return false;
        fingerprint = hash;
        return true;

        void Mix(ulong value)
        {
            hash ^= value;
            hash = unchecked(hash * 1099511628211UL);
        }

        void MixTransform(Matrix4x3 world)
        {
            MixVector(world.Row0); MixVector(world.Row1); MixVector(world.Row2); MixVector(world.Row3);
        }

        void MixVector(Vector3 value)
        {
            Mix(BitConverter.SingleToUInt32Bits(value.X));
            Mix(BitConverter.SingleToUInt32Bits(value.Y));
            Mix(BitConverter.SingleToUInt32Bits(value.Z));
        }
    }

    // The scene is native mutable memory and must only be visited on the framework thread. We copy a bounded local
    // triangle soup in one guarded pass; the expensive heightfield/raster work then runs entirely on managed data.
    public static bool TryCapture(BGCollisionModule* module, Vector3 player, Vector2 center, float radius, float resolution,
        double budgetMilliseconds, out ForetellCollisionSnapshot? snapshot, out string reason, out bool timedOut)
    {
        snapshot = null;
        reason = "collision scene unavailable";
        timedOut = false;
        if (module == null || module->ShuttingDown || module->SceneManager == null || module->LoadInProgressCounter > 0)
            return false;
        var maximumMilliseconds = Math.Clamp(budgetMilliseconds, 2, MaximumCaptureMilliseconds);

        var started = Stopwatch.GetTimestamp();
        var triangles = new List<ForetellCollisionTriangle>(16_384);
        var seenMeshes = new HashSet<nint>();
        var colliders = 0;
        var nativePrimitives = 0;
        var complete = true;
        ulong primitiveMaterial = 0;
        // Allocate managed scratch state before acquiring the native lock. Native collision updates run on a
        // separate task; framework-thread access alone does not protect these pointers. Never wait on the game.
        if (!TryAcquireSceneLock(&module->UpdateTaskLock))
        {
            reason = "collision scene update in progress";
            return false;
        }
        try
        {
            foreach (var sceneRef in module->SceneManager->Scenes)
            {
                var scene = sceneRef->Scene;
                if (scene == null)
                    continue;
                foreach (var collider in scene->Colliders)
                {
                    if (collider == null || !ForetellCollisionRules.Participates(collider->LayerMask, collider->VisibilityFlags))
                        continue;
                    primitiveMaterial = collider->ObjectMaterialValue;
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
                            for (var i = 0; i < count && complete; ++i)
                                complete &= CaptureMesh(streamed->Elements[i].Mesh);
                            break;
                        case ColliderType.Box:
                            complete &= CaptureBox((ColliderBox*)collider);
                            break;
                        case ColliderType.Cylinder:
                            complete &= CaptureCylinder((ColliderCylinder*)collider);
                            break;
                        case ColliderType.Sphere:
                            complete &= CaptureSphere((ColliderSphere*)collider);
                            break;
                        case ColliderType.Plane:
                        case ColliderType.PlaneTwoSided:
                            complete &= CapturePlane((ColliderPlane*)collider);
                            break;
                    }
                    if (!complete || Stopwatch.GetElapsedTime(started).TotalMilliseconds > maximumMilliseconds)
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
        finally
        {
            ReleaseSceneLock(&module->UpdateTaskLock);
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (!complete)
        {
            timedOut = elapsed > maximumMilliseconds;
            reason = timedOut
                ? $"native mesh snapshot exceeded {maximumMilliseconds:F0} ms attempt budget"
                : "native mesh snapshot exceeded structural safety bounds";
            return false;
        }
        if (triangles.Count == 0)
        {
            reason = "no loaded PCB collision triangles in local radius";
            return false;
        }
        snapshot = new(player, center, radius, resolution, triangles.ToArray(), colliders, nativePrimitives, elapsed);
        reason = $"copied {triangles.Count:N0} local PCB triangles";
        return true;

        bool CaptureMesh(ColliderMesh* collider)
        {
            if (collider == null || collider->MeshIsSimple || collider->Mesh == null || !collider->Loaded
                || !ForetellCollisionRules.Participates(collider->LayerMask, collider->VisibilityFlags)
                || !seenMeshes.Add((nint)collider))
                return true;
            var bounds = collider->WorldBoundingBox;
            if (!RectangleIntersectsCircle(bounds.Min.X, bounds.Min.Z, bounds.Max.X, bounds.Max.Z, center, radius))
                return true;
            ++colliders;
            var mesh = (MeshPCB*)collider->Mesh;
            var world = collider->World;
            return CaptureNode(mesh->RootNode, ref world, collider->ObjectMaterialValue, collider->ObjectMaterialMask);
        }

        bool CaptureNode(MeshPCB.FileNode* node, ref Matrix4x3 world, ulong materialValue, ulong materialMask, int depth = 0)
        {
            if (node == null)
                return true;
            if (depth > 64) return false;
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > maximumMilliseconds)
                return false;
            if (!NodeIntersectsCircle(node->LocalBounds, ref world, center, radius))
                return true;

            var vertices = node->NumVertsRaw + node->NumVertsCompressed;
            var primitives = node->NumPrims;
            nativePrimitives += primitives;
            if (nativePrimitives > MaximumScenePrimitives) return false;
            for (var i = 0; i < primitives; ++i)
            {
                if ((i & 0x7FF) == 0 && Stopwatch.GetElapsedTime(started).TotalMilliseconds > maximumMilliseconds)
                    return false;
                var primitive = node->Primitives[i];
                var material = ForetellCollisionRules.EffectiveMaterial(primitive.Material, materialValue, materialMask);
                if (!ForetellCollisionRules.BlocksMovement(material))
                    continue;
                if (primitive.V1 >= vertices || primitive.V2 >= vertices || primitive.V3 >= vertices)
                    continue;
                var a = world.TransformCoordinate(node->Vertex(primitive.V1));
                var b = world.TransformCoordinate(node->Vertex(primitive.V2));
                var c = world.TransformCoordinate(node->Vertex(primitive.V3));
                if (!TriangleIntersectsCircle(a, b, c, center, radius))
                    continue;
                if (triangles.Count >= MaximumSnapshotTriangles)
                    return false;
                triangles.Add(new(a, b, c, material));
            }
            return CaptureNode(node->Child1, ref world, materialValue, materialMask, depth + 1)
                && CaptureNode(node->Child2, ref world, materialValue, materialMask, depth + 1);
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
                if (!AddTriangle(bottomA, topB, topA) || !AddTriangle(bottomA, bottomB, topB)
                    || !AddTriangle(topCenter, topA, topB) || !AddTriangle(bottomCenter, bottomB, bottomA))
                    return false;
            }
            return true;
        }

        bool CaptureSphere(ColliderSphere* collider)
        {
            ++colliders;
            var world = collider->World;
            const int segments = 24;
            const int rings = 12;
            for (var ring = 0; ring < rings; ++ring)
                for (var segment = 0; segment < segments; ++segment)
                {
                    var a = Point(ring, segment); var b = Point(ring, segment + 1);
                    var c = Point(ring + 1, segment); var d = Point(ring + 1, segment + 1);
                    if (!AddTriangle(a, d, c) || !AddTriangle(a, b, d)) return false;
                }
            return true;

            Vector3 Point(int ring, int segment)
            {
                var latitude = -MathF.PI * .5f + MathF.PI * ring / rings;
                var longitude = MathF.Tau * segment / segments;
                return world.TransformCoordinate(new(MathF.Cos(latitude) * MathF.Sin(longitude),
                    MathF.Sin(latitude), MathF.Cos(latitude) * MathF.Cos(longitude)));
            }
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
            if (!ForetellCollisionRules.BlocksMovement(primitiveMaterial))
                return true;
            if (!TriangleIntersectsCircle(a, b, c, center, radius))
                return true;
            if (triangles.Count >= MaximumSnapshotTriangles)
                return false;
            triangles.Add(new(a, b, c, primitiveMaterial));
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
