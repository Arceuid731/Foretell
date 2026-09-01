using Dalamud.Hooking;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using InteropGenerator.Runtime;
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
    private long _nativeVFXSequence;

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

    private delegate nint ActorVFXCreateDelegate(nint path, nint caster, nint target, float a4, byte a5, ushort a6, byte a7);
    private delegate void ActorVFXDestroyDelegate(nint vfx);

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
    }

    private unsafe void ForetellObjectEffectDetour(FFXIVEventObject* self, uint entityId, uint actionId, ulong arg4)
    {
        _foretellObjectEffectHook!.Original(self, entityId, actionId, arg4);
        try
        {
            if (self == null) return;
            var instanceID = (ulong)self->EntityId;
            var actor = _ws.Actors.Find(instanceID);
            var obs = Observation(ObservationKind.ObjectEffect, actor, entityId, actionId);
            if (actor == null)
            {
                obs.ActorID = instanceID;
                obs.ActorOID = self->BaseId;
                obs.SourceKind = SourceKind.EventObject;
            }
            obs.Numeric["native.objectEffect.arg4"] = arg4;
            ProcessObservation(obs);
        }
        catch (Exception e)
        {
            Service.LogVerbose($"[Foretell] ObjectEffect observation failed: {e.Message}");
        }
    }

    private unsafe nint ForetellActorVFXCreateDetour(nint pathAddress, nint casterAddress, nint targetAddress, float a4, byte a5, ushort a6, byte a7)
    {
        var path = ReadNativeString(pathAddress);
        var caster = ReadNativeActor(casterAddress);
        var target = ReadNativeActor(targetAddress);
        var result = _foretellActorVFXCreateHook!.Original(pathAddress, casterAddress, targetAddress, a4, a5, a6, a7);
        try
        {
            if (result == 0) return result;
            var track = new NativeVFXTrack(System.Threading.Interlocked.Increment(ref _nativeVFXSequence), "actor", path, "",
                caster.ID, caster.OID, caster.X, caster.Y, caster.Z, caster.Rotation, caster.Radius,
                target.ID, target.OID, target.X, target.Y, target.Z, target.Rotation, target.Radius, _ws.CurrentTime);
            _nativeVFXTracks[result] = track;
            EmitNativeVFX(ObservationKind.NativeVFXSpawn, track, a4, a5, a6, a7);
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
                EmitNativeVFX(ObservationKind.NativeVFXDestroy, track);
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
        var pathText = path.ToString();
        var poolText = pool.ToString();
        var result = _foretellStaticVFXCreateHook!.Original(path, pool);
        try
        {
            if (result == null) return result;
            var position = result->Position;
            var track = new NativeVFXTrack(System.Threading.Interlocked.Increment(ref _nativeVFXSequence), "static", pathText, poolText,
                0, 0, position.X, position.Y, position.Z, 0, 0, 0, 0, 0, 0, 0, 0, 0, _ws.CurrentTime);
            _nativeVFXTracks[(nint)result] = track;
            EmitNativeVFX(ObservationKind.NativeVFXSpawn, track);
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
                EmitNativeVFX(ObservationKind.NativeVFXDestroy, track);
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

    private void EmitNativeVFX(ObservationKind kind, NativeVFXTrack track, float a4 = 0, byte a5 = 0, ushort a6 = 0, byte a7 = 0)
    {
        var actor = track.CasterID != 0 ? _ws.Actors.Find(track.CasterID) : null;
        var obs = Observation(kind, actor, StableHash(track.Path), target: track.TargetID, detail: track.Path);
        if (actor == null && track.CasterID != 0)
        {
            obs.ActorID = track.CasterID;
            obs.ActorOID = track.CasterOID;
            obs.SourceKind = SourceKind.Unknown;
        }
        if (actor == null)
        {
            obs.X = track.CasterX;
            obs.Z = track.CasterZ;
        }
        if (track.TargetID != 0 && _ws.Actors.Find(track.TargetID) == null)
        {
            obs.TargetX = track.TargetX;
            obs.TargetZ = track.TargetZ;
        }
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
            StoreNative(obs, "native.vfx.lifetimeSeconds", Math.Max(0, (_ws.CurrentTime - track.Created).TotalSeconds));
        ProcessObservation(obs);
    }

    private static string ReadNativeString(nint address)
    {
        if (address == 0) return "";
        try { return MemoryHelper.ReadStringNullTerminated(address); }
        catch { return ""; }
    }

    private static unsafe NativeActorRef ReadNativeActor(nint address)
    {
        if (address == 0) return default;
        try
        {
            var actor = (GameObject*)address;
            return new((ulong)actor->GetGameObjectId(), actor->BaseId, actor->Position.X, actor->Position.Y, actor->Position.Z,
                actor->Rotation, actor->HitboxRadius);
        }
        catch
        {
            return default;
        }
    }
}
