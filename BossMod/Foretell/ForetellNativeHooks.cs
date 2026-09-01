using Dalamud.Hooking;
using FFXIVEventObject = FFXIVClientStructs.FFXIV.Client.Game.Object.EventObject;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private Hook<FFXIVEventObject.Delegates.PlayAnimation>? _foretellObjectEffectHook;

    private unsafe void InitializeNativeHooks()
    {
        try
        {
            var address = (nint)FFXIVEventObject.Addresses.PlayAnimation.Value;
            if (address == 0)
            {
                RegisterCapability("native.objectEffect", typeof(FFXIVEventObject), "PlayAnimation", false, true, "FFXIVClientStructs address unavailable");
                return;
            }
            _foretellObjectEffectHook = Service.Hook.HookFromAddress<FFXIVEventObject.Delegates.PlayAnimation>(address, ForetellObjectEffectDetour);
            _foretellObjectEffectHook.Enable();
            RegisterCapability("native.objectEffect", typeof(FFXIVEventObject), "PlayAnimation", true, false, "direct passive client hook");
        }
        catch (Exception e)
        {
            RegisterCapability("native.objectEffect", typeof(FFXIVEventObject), "PlayAnimation", false, true, $"hook unavailable: {e.GetType().Name}");
            Service.Log($"[Foretell] Native ObjectEffect hook unavailable: {e.Message}");
        }
    }

    private void DisposeNativeHooks()
    {
        _foretellObjectEffectHook?.Disable();
        _foretellObjectEffectHook?.Dispose();
        _foretellObjectEffectHook = null;
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
}
