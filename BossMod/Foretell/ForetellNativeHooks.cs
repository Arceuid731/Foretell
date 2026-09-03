using Dalamud.Hooking;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using InteropGenerator.Runtime;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Text;
using FFXIVEventObject = FFXIVClientStructs.FFXIV.Client.Game.Object.EventObject;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private Hook<FFXIVEventObject.Delegates.PlayAnimation>? _foretellObjectEffectHook;
    private Hook<ActorVFXCreateDelegate>? _foretellActorVFXCreateHook;
    private Hook<ActorVFXDestroyDelegate>? _foretellActorVFXDestroyHook;
    private Hook<VfxObject.Delegates.Create>? _foretellStaticVFXCreateHook;
    private Hook<VfxObject.Delegates.CleanupRender>? _foretellStaticVFXDestroyHook;
    private readonly ConcurrentDictionary<nint, NativeVFXTrack> _nativeVFXTracks = [];
    private readonly ConcurrentQueue<(nint Address, long Token, DateTime Created)> _nativeVFXExpiry = [];
    private readonly ConcurrentQueue<NativeHookCapture> _nativeHookCaptures = [];
    private long _nativeVFXSequence;
    private long _nativeVFXTrackCount;
    private long _nativeHookCaptured;
    private long _nativeHookProcessed;
    private long _nativeHookPending;
    private long _nativeHookFailures;
    private double _lastNativeHookDrainMilliseconds;
    private double _peakNativeHookDrainMilliseconds;
    private const int MaxNativeHookCapturesPerFrame = 96;
    private const int MaxNativeHookBacklog = 8192;
    private const int MaxTrackedNativeVFX = 32768;
    private const int MaxNativeVFXExpiryEntries = 65536;
    private const double MaxNativeHookDrainMilliseconds = 0.75;
    private long _nativeVFXExpiryPending;

    // Signatures independently verified against the current client. The generic hook mechanism was cross-checked
    // with ECommons (MIT, NightmareXIV) rather than copying Splatoon's AGPL implementation.
    private const string ActorVFXCreateSignature = "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8";
    private const string ActorVFXDestroySignature = "48 89 5C 24 ?? 57 48 83 EC ?? 48 8D 05 ?? ?? ?? ?? 48 8B D9 48 89 01 8B FA 48 8D 05 ?? ?? ?? ?? 48 89 81 ?? ?? ?? ?? 48 8B 89 ?? ?? ?? ?? 48 85 C9 74 ?? 48 8B 01 48 8B D3";
    private const string StaticVFXDestroySignature = "40 53 48 83 EC 20 48 8B D9 48 8B 89 ?? ?? ?? ?? 48 85 C9 74 28 33 D2";

    private sealed record NativeVFXTrack(long Token, string Kind, string Path, string Pool, ulong CasterID, uint CasterOID,
        float CasterX, float CasterY, float CasterZ, float CasterRotation, float CasterRadius,
        ulong TargetID, uint TargetOID, float TargetX, float TargetY, float TargetZ, float TargetRotation, float TargetRadius,
        DateTime Created);
    private readonly record struct NativeActorRef(ulong ID, uint OID, float X, float Y, float Z, float Rotation, float Radius);
    private abstract record NativeHookCapture(DateTime At);
    private sealed record NativeObjectEffectCapture(DateTime CapturedAt, ulong InstanceID, uint OID, uint EntityID, uint ActionID, ulong Arg4,
        float X, float Y, float Z, float Rotation, float Radius) : NativeHookCapture(CapturedAt);
    private sealed record NativeVFXCapture(DateTime CapturedAt, ObservationKind Kind, NativeVFXTrack Track, float A4 = 0, byte A5 = 0, ushort A6 = 0, byte A7 = 0) : NativeHookCapture(CapturedAt);

    private delegate nint ActorVFXCreateDelegate(nint path, nint caster, nint target, float a4, byte a5, ushort a6, byte a7);
    private delegate void ActorVFXDestroyDelegate(nint vfx);

    private void ClassifyNativeTelemetryQuarantine()
    {
        const string reason = "quarantined: direct client hooks/readers require framework-thread queue isolation before live use";
        RegisterCapability("native.objectEffect", typeof(FFXIVEventObject), "PlayAnimation", false, true, reason);
        RegisterCapability("native.vfx.actor.lifecycle", typeof(NativeVFXTrack), "actor VFX", false, true, reason);
        RegisterCapability("native.vfx.static.lifecycle", typeof(VfxObject), "static VFX", false, true, reason);
        RegisterCapability("native.character", typeof(GameObject), "Character containers", false, true, reason);
        RegisterCapability("native.environment", typeof(FFXIVClientStructs.FFXIV.Client.Graphics.Environment.EnvManager), "environment state", false, true, reason);
        RegisterCapability("native.camera", typeof(Camera), "camera state", false, true, reason);
        Service.Log("[Foretell] Direct native telemetry is quarantined; typed semantic, raw network and managed Data Fabric sensors remain active.");
    }

    private unsafe void InitializeNativeHooks()
    {
        try
        {
            var address = (nint)FFXIVEventObject.Addresses.PlayAnimation.Value;
            if (address == 0)
            {
                RegisterCapability("native.objectEffect", typeof(FFXIVEventObject), "PlayAnimation", false, false, "FFXIVClientStructs address unavailable");
            }
            else
            {
                _foretellObjectEffectHook = Service.Hook.HookFromAddress<FFXIVEventObject.Delegates.PlayAnimation>(address, ForetellObjectEffectDetour);
                _foretellObjectEffectHook.Enable();
                RegisterCapability("native.objectEffect", typeof(FFXIVEventObject), "PlayAnimation", true, false, "direct passive client hook");
            }
        }
        catch (Exception e)
        {
            _foretellObjectEffectHook?.Dispose();
            _foretellObjectEffectHook = null;
            RegisterCapability("native.objectEffect", typeof(FFXIVEventObject), "PlayAnimation", false, false, $"hook unavailable: {e.GetType().Name}");
            Service.Log($"[Foretell] Native ObjectEffect hook unavailable: {e.Message}");
        }

        InitializeActorVFXHooks();
        InitializeStaticVFXHooks();
    }

    private void InitializeActorVFXHooks()
    {
        try
        {
            if (!Service.SigScanner.TryScanText(ActorVFXCreateSignature, out var create) || create == 0)
                throw new InvalidOperationException("actor VFX create signature unavailable");
            if (!Service.SigScanner.TryScanText(ActorVFXDestroySignature, out var destroy) || destroy == 0)
                throw new InvalidOperationException("actor VFX destroy signature unavailable");
            _foretellActorVFXCreateHook = Service.Hook.HookFromAddress<ActorVFXCreateDelegate>(create, ForetellActorVFXCreateDetour);
            _foretellActorVFXDestroyHook = Service.Hook.HookFromAddress<ActorVFXDestroyDelegate>(destroy, ForetellActorVFXDestroyDetour);
            _foretellActorVFXCreateHook.Enable();
            _foretellActorVFXDestroyHook.Enable();
            RegisterCapability("native.vfx.actor.lifecycle", typeof(NativeVFXTrack), "actor VFX path/caster/target/create/destroy", true, false, "direct passive client hooks");
        }
        catch (Exception e)
        {
            _foretellActorVFXDestroyHook?.Dispose();
            _foretellActorVFXDestroyHook = null;
            _foretellActorVFXCreateHook?.Dispose();
            _foretellActorVFXCreateHook = null;
            RegisterCapability("native.vfx.actor.lifecycle", typeof(NativeVFXTrack), "actor VFX", false, false, $"hook unavailable: {e.GetType().Name}");
            Service.Log($"[Foretell] Native actor VFX hooks unavailable: {e.Message}");
        }
    }

    private unsafe void InitializeStaticVFXHooks()
    {
        try
        {
            var create = (nint)VfxObject.Addresses.Create.Value;
            if (create == 0)
                throw new InvalidOperationException("static VFX create address unavailable");
            if (!Service.SigScanner.TryScanText(StaticVFXDestroySignature, out var destroy) || destroy == 0)
                throw new InvalidOperationException("static VFX destroy signature unavailable");
            _foretellStaticVFXCreateHook = Service.Hook.HookFromAddress<VfxObject.Delegates.Create>(create, ForetellStaticVFXCreateDetour);
            _foretellStaticVFXDestroyHook = Service.Hook.HookFromAddress<VfxObject.Delegates.CleanupRender>(destroy, ForetellStaticVFXDestroyDetour);
            _foretellStaticVFXCreateHook.Enable();
            _foretellStaticVFXDestroyHook.Enable();
            RegisterCapability("native.vfx.static.lifecycle", typeof(VfxObject), "static VFX path/pool/create/destroy", true, false, "direct passive client hooks");
        }
        catch (Exception e)
        {
            _foretellStaticVFXDestroyHook?.Dispose();
            _foretellStaticVFXDestroyHook = null;
            _foretellStaticVFXCreateHook?.Dispose();
            _foretellStaticVFXCreateHook = null;
            RegisterCapability("native.vfx.static.lifecycle", typeof(VfxObject), "static VFX", false, false, $"hook unavailable: {e.GetType().Name}");
            Service.Log($"[Foretell] Native static VFX hooks unavailable: {e.Message}");
        }
    }

    private void DisposeNativeHooks()
    {
        _foretellStaticVFXDestroyHook?.Disable();
        _foretellStaticVFXDestroyHook?.Dispose();
        _foretellStaticVFXDestroyHook = null;
        _foretellStaticVFXCreateHook?.Disable();
        _foretellStaticVFXCreateHook?.Dispose();
        _foretellStaticVFXCreateHook = null;
        _foretellActorVFXDestroyHook?.Disable();
        _foretellActorVFXDestroyHook?.Dispose();
        _foretellActorVFXDestroyHook = null;
        _foretellActorVFXCreateHook?.Disable();
        _foretellActorVFXCreateHook?.Dispose();
        _foretellActorVFXCreateHook = null;
        _foretellObjectEffectHook?.Disable();
        _foretellObjectEffectHook?.Dispose();
        _foretellObjectEffectHook = null;
        _nativeVFXTracks.Clear();
        Interlocked.Exchange(ref _nativeVFXTrackCount, 0);
        while (_nativeVFXExpiry.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _nativeVFXExpiryPending, 0);
    }

    private void EnqueueNativeCapture(NativeHookCapture capture)
    {
        if (Interlocked.Read(ref _nativeHookPending) >= MaxNativeHookBacklog)
        {
            Interlocked.Increment(ref _nativeHookFailures);
            return;
        }
        _nativeHookCaptures.Enqueue(capture);
        Interlocked.Increment(ref _nativeHookCaptured);
        Interlocked.Increment(ref _nativeHookPending);
    }

    private void DrainNativeCaptures()
    {
        var started = Stopwatch.GetTimestamp();
        var processed = 0;
        while (processed < MaxNativeHookCapturesPerFrame
            && Stopwatch.GetElapsedTime(started).TotalMilliseconds < MaxNativeHookDrainMilliseconds
            && _nativeHookCaptures.TryDequeue(out var capture))
        {
            Interlocked.Decrement(ref _nativeHookPending);
            ++processed;
            try
            {
                switch (capture)
                {
                    case NativeObjectEffectCapture effect:
                        ProcessNativeObjectEffect(effect);
                        break;
                    case NativeVFXCapture vfx:
                        EmitNativeVFX(vfx.Kind, vfx.Track, vfx.At, vfx.A4, vfx.A5, vfx.A6, vfx.A7);
                        break;
                }
                Interlocked.Increment(ref _nativeHookProcessed);
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _nativeHookFailures);
                Service.LogVerbose($"[Foretell] Deferred native capture rejected safely: {e.Message}");
            }
        }
        var now = DateTime.UtcNow;
        var cleaned = 0;
        while (cleaned++ < 128 && Stopwatch.GetElapsedTime(started).TotalMilliseconds < MaxNativeHookDrainMilliseconds
            && _nativeVFXExpiry.TryPeek(out var expiry)
            && ((now - expiry.Created).TotalMinutes >= 5 || Interlocked.Read(ref _nativeVFXExpiryPending) > MaxNativeVFXExpiryEntries)
            && _nativeVFXExpiry.TryDequeue(out expiry))
        {
            Interlocked.Decrement(ref _nativeVFXExpiryPending);
            if (_nativeVFXTracks.TryGetValue(expiry.Address, out var tracked) && tracked.Token == expiry.Token)
            {
                if (_nativeVFXTracks.TryRemove(expiry.Address, out _))
                    Interlocked.Decrement(ref _nativeVFXTrackCount);
            }
        }
        _lastNativeHookDrainMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _peakNativeHookDrainMilliseconds = Math.Max(_peakNativeHookDrainMilliseconds, _lastNativeHookDrainMilliseconds);
    }

    private void ProcessNativeObjectEffect(NativeObjectEffectCapture capture)
    {
        // Object effects are common combat traffic. They request a stable background refresh because some can
        // alter platforms, but unlike map/director transitions they do not collapse the current live component.
        InvalidateTopology(hard: false);
        var actor = capture.InstanceID != 0 ? _ws.Actors.Find(capture.InstanceID) : null;
        var obs = Observation(ObservationKind.ObjectEffect, actor, capture.EntityID, capture.ActionID);
        obs.At = NormalizeObservationTime(capture.At);
        obs.ActorID = capture.InstanceID;
        obs.ActorOID = capture.OID;
        obs.X = capture.X;
        obs.Z = capture.Z;
        obs.Rotation = capture.Rotation;
        if (actor == null)
            obs.SourceKind = SourceKind.EventObject;
        obs.Numeric["native.objectEffect.arg4"] = capture.Arg4;
        obs.Numeric["native.objectEffect.y"] = capture.Y;
        obs.Numeric["native.objectEffect.hitboxRadius"] = capture.Radius;
        ProcessObservation(obs);
    }

    private unsafe void ForetellObjectEffectDetour(FFXIVEventObject* self, uint entityId, uint actionId, ulong arg4)
    {
        NativeObjectEffectCapture? capture = null;
        try
        {
            if (self != null)
                capture = new(DateTime.UtcNow, (ulong)self->EntityId, self->BaseId, entityId, actionId, arg4,
                    self->Position.X, self->Position.Y, self->Position.Z, self->Rotation, self->HitboxRadius);
        }
        catch
        {
            Interlocked.Increment(ref _nativeHookFailures);
        }
        _foretellObjectEffectHook!.Original(self, entityId, actionId, arg4);
        if (capture != null)
            EnqueueNativeCapture(capture);
    }

    private unsafe nint ForetellActorVFXCreateDetour(nint pathAddress, nint casterAddress, nint targetAddress, float a4, byte a5, ushort a6, byte a7)
    {
        var result = _foretellActorVFXCreateHook!.Original(pathAddress, casterAddress, targetAddress, a4, a5, a6, a7);
        try
        {
            if (result == 0) return result;
            // Match the proven MIT ECommons ordering: never perform telemetry work before the game constructor.
            var path = ReadNativeString(pathAddress);
            var caster = ReadNativeActor(casterAddress);
            var target = ReadNativeActor(targetAddress);
            var track = new NativeVFXTrack(System.Threading.Interlocked.Increment(ref _nativeVFXSequence), "actor", path, "",
                caster.ID, caster.OID, caster.X, caster.Y, caster.Z, caster.Rotation, caster.Radius,
                target.ID, target.OID, target.X, target.Y, target.Z, target.Rotation, target.Radius, DateTime.UtcNow);
            if (Interlocked.Read(ref _nativeVFXTrackCount) < MaxTrackedNativeVFX)
            {
                if (_nativeVFXTracks.TryAdd(result, track))
                    Interlocked.Increment(ref _nativeVFXTrackCount);
                else
                    _nativeVFXTracks[result] = track;
                _nativeVFXExpiry.Enqueue((result, track.Token, track.Created));
                Interlocked.Increment(ref _nativeVFXExpiryPending);
            }
            else
                Interlocked.Increment(ref _nativeHookFailures);
            EnqueueNativeCapture(new NativeVFXCapture(track.Created, ObservationKind.NativeVFXSpawn, track, a4, a5, a6, a7));
        }
        catch (Exception e)
        {
            Service.LogVerbose($"[Foretell] Actor VFX spawn observation failed: {e.Message}");
        }
        return result;
    }

    private void ForetellActorVFXDestroyDetour(nint vfx)
    {
        try
        {
            if (_nativeVFXTracks.TryRemove(vfx, out var track))
            {
                Interlocked.Decrement(ref _nativeVFXTrackCount);
                EnqueueNativeCapture(new NativeVFXCapture(DateTime.UtcNow, ObservationKind.NativeVFXDestroy, track));
            }
        }
        catch (Exception e)
        {
            Service.LogVerbose($"[Foretell] Actor VFX destroy observation failed: {e.Message}");
        }
        finally
        {
            _foretellActorVFXDestroyHook!.Original(vfx);
        }
    }

    private unsafe VfxObject* ForetellStaticVFXCreateDetour(CStringPointer path, CStringPointer pool)
    {
        var result = _foretellStaticVFXCreateHook!.Original(path, pool);
        try
        {
            if (result == null) return result;
            var pathText = ReadNativeString((nint)path.Value);
            var poolText = ReadNativeString((nint)pool.Value);
            var position = result->Position;
            var track = new NativeVFXTrack(System.Threading.Interlocked.Increment(ref _nativeVFXSequence), "static", pathText, poolText,
                0, 0, position.X, position.Y, position.Z, 0, 0, 0, 0, 0, 0, 0, 0, 0, DateTime.UtcNow);
            if (Interlocked.Read(ref _nativeVFXTrackCount) < MaxTrackedNativeVFX)
            {
                if (_nativeVFXTracks.TryAdd((nint)result, track))
                    Interlocked.Increment(ref _nativeVFXTrackCount);
                else
                    _nativeVFXTracks[(nint)result] = track;
                _nativeVFXExpiry.Enqueue(((nint)result, track.Token, track.Created));
                Interlocked.Increment(ref _nativeVFXExpiryPending);
            }
            else
                Interlocked.Increment(ref _nativeHookFailures);
            EnqueueNativeCapture(new NativeVFXCapture(track.Created, ObservationKind.NativeVFXSpawn, track));
        }
        catch (Exception e)
        {
            Service.LogVerbose($"[Foretell] Static VFX spawn observation failed: {e.Message}");
        }
        return result;
    }

    private unsafe void ForetellStaticVFXDestroyDetour(VfxObject* vfx)
    {
        try
        {
            if (_nativeVFXTracks.TryRemove((nint)vfx, out var track))
            {
                Interlocked.Decrement(ref _nativeVFXTrackCount);
                EnqueueNativeCapture(new NativeVFXCapture(DateTime.UtcNow, ObservationKind.NativeVFXDestroy, track));
            }
        }
        catch (Exception e)
        {
            Service.LogVerbose($"[Foretell] Static VFX destroy observation failed: {e.Message}");
        }
        finally
        {
            _foretellStaticVFXDestroyHook!.Original(vfx);
        }
    }

    private void EmitNativeVFX(ObservationKind kind, NativeVFXTrack track, DateTime capturedAt, float a4 = 0, byte a5 = 0, ushort a6 = 0, byte a7 = 0)
    {
        var actor = track.CasterID != 0 ? _ws.Actors.Find(track.CasterID) : null;
        var obs = Observation(kind, actor, StableHash(track.Path), target: track.TargetID, detail: track.Path);
        obs.At = NormalizeObservationTime(capturedAt);
        obs.ActorID = track.CasterID;
        obs.ActorOID = track.CasterOID;
        obs.X = track.CasterX;
        obs.Z = track.CasterZ;
        obs.Rotation = track.CasterRotation;
        obs.TargetID = track.TargetID;
        obs.TargetX = track.TargetX;
        obs.TargetZ = track.TargetZ;
        if (actor == null)
            obs.SourceKind = SourceKind.Unknown;
        StoreNative(obs, "native.vfx.instance", track.Token);
        StoreNative(obs, "native.vfx.kind", track.Kind);
        StoreNative(obs, "native.vfx.path", track.Path);
        StoreNative(obs, "native.vfx.pool", track.Pool);
        StoreNative(obs, "native.vfx.casterId", track.CasterID);
        StoreNative(obs, "native.vfx.casterOid", track.CasterOID);
        StoreNative(obs, "native.vfx.caster.x", track.CasterX);
        StoreNative(obs, "native.vfx.caster.y", track.CasterY);
        StoreNative(obs, "native.vfx.caster.z", track.CasterZ);
        StoreNative(obs, "native.vfx.caster.rotation", track.CasterRotation);
        StoreNative(obs, "native.vfx.caster.hitboxRadius", track.CasterRadius);
        StoreNative(obs, "native.vfx.targetId", track.TargetID);
        StoreNative(obs, "native.vfx.targetOid", track.TargetOID);
        StoreNative(obs, "native.vfx.target.x", track.TargetX);
        StoreNative(obs, "native.vfx.target.y", track.TargetY);
        StoreNative(obs, "native.vfx.target.z", track.TargetZ);
        StoreNative(obs, "native.vfx.target.rotation", track.TargetRotation);
        StoreNative(obs, "native.vfx.target.hitboxRadius", track.TargetRadius);
        StoreNative(obs, "native.vfx.arg4", a4);
        StoreNative(obs, "native.vfx.arg5", a5);
        StoreNative(obs, "native.vfx.arg6", a6);
        StoreNative(obs, "native.vfx.arg7", a7);
        if (kind == ObservationKind.NativeVFXDestroy)
            StoreNative(obs, "native.vfx.lifetimeSeconds", Math.Max(0, (capturedAt - track.Created).TotalSeconds));
        ProcessObservation(obs);
    }

    private static unsafe string ReadNativeString(nint address)
    {
        if (address == 0) return "";
        try
        {
            // Dalamud's bounded helper centralises native-memory validation and matches the upstream MIT hook.
            return MemoryHelper.ReadString(address, Encoding.ASCII, 512).TrimEnd('\0');
        }
        catch { return ""; }
    }

    private static unsafe NativeActorRef ReadNativeActor(nint address)
    {
        if (address == 0) return default;
        try
        {
            var actor = (GameObject*)address;
            return new(actor->EntityId, actor->BaseId, actor->Position.X, actor->Position.Y, actor->Position.Z,
                actor->Rotation, actor->HitboxRadius);
        }
        catch
        {
            return default;
        }
    }
}
