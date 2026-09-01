using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Environment;
using System.Reflection;

namespace BossMod.Foretell;

// Stable, encounter-agnostic native client state that BMR's normalized Actor intentionally does not retain.
// Pointer values are never learner features: they are process-layout noise, not gameplay evidence.
public sealed partial class ForetellEngine
{
    private string _environmentFabricFingerprint = "";
    private string _cameraFabricFingerprint = "";
    private bool _pluginServicesAudited;

    private void ResetNativeDataFabric()
    {
        _environmentFabricFingerprint = "";
        _cameraFabricFingerprint = "";
    }

    private void StoreNative<T>(ForetellObservation observation, string key, T value)
        where T : notnull
        => TryStoreScalar(value, value.GetType(), key, observation);

    private unsafe void EnrichNativeCharacter(ForetellObservation observation, Actor actor)
    {
        if ((actor.InstanceID >> 32) != 0 || !HasNativeCharacterLayout(actor.Type))
            return;
        if (Service.ObjectTable.SearchById((uint)actor.InstanceID) is not ICharacter dalamudCharacter)
            return;

        var character = Utils.CharacterInternal(dalamudCharacter);
        if (character == null)
            return;

        const string p = "native.character";
        StoreNative(observation, $"{p}.schema", 1);
        StoreNative(observation, $"{p}.targetId", (ulong)character->GetTargetId());
        StoreNative(observation, $"{p}.softTargetId", (ulong)character->GetSoftTargetId());
        StoreNative(observation, $"{p}.castRotation", character->CastRotation);
        StoreNative(observation, $"{p}.nameId", character->NameId);
        StoreNative(observation, $"{p}.eventHandlerNameId", character->EventHandlerNameId);
        StoreNative(observation, $"{p}.transformationNameId", character->TransformationNameId);
        StoreNative(observation, $"{p}.transformationId", character->TransformationId);
        StoreNative(observation, $"{p}.statusLoopVfxId", character->StatusLoopVfxId);
        StoreNative(observation, $"{p}.modelScale", character->ModelScale);
        StoreNative(observation, $"{p}.mode", character->Mode);
        StoreNative(observation, $"{p}.modeParam", character->ModeParam);
        StoreNative(observation, $"{p}.objectType", character->ObjectType);
        StoreNative(observation, $"{p}.relationFlags", character->RelationFlags);
        StoreNative(observation, $"{p}.actorControlFlags", character->ActorControlFlags);
        StoreNative(observation, $"{p}.isPartyMember", character->IsPartyMember);
        StoreNative(observation, $"{p}.isAllianceMember", character->IsAllianceMember);
        StoreNative(observation, $"{p}.alpha", character->Alpha);

        ref var move = ref character->MoveController;
        StoreNative(observation, $"{p}.move.movementState", move.MovementState);
        StoreNative(observation, $"{p}.move.isSwimming", move.IsSwimming);
        RegisterCapability($"{p}.move.nativeMethods", typeof(FFXIVClientStructs.FFXIV.Client.Game.Control.MoveControl.MoveController),
            "IsFlying/IsDiving", false, true, "native function calls forbidden in the high-frequency sampler; movement fields remain ingested");

        ref var vfx = ref character->Vfx;
        StoreNative(observation, $"{p}.vfx.voiceId", vfx.VoiceId);
        var tethers = vfx.Tethers;
        StoreNative(observation, $"{p}.vfx.tetherCount", tethers.Length);
        for (var i = 0; i < tethers.Length; ++i)
        {
            ref var tether = ref tethers[i];
            var tp = $"{p}.vfx.tether[{i}]";
            StoreNative(observation, $"{tp}.id", tether.Id);
            StoreNative(observation, $"{tp}.targetId", (ulong)tether.TargetId);
            StoreNative(observation, $"{tp}.progress", tether.Progress);
        }

        ref var timeline = ref character->Timeline;
        StoreNative(observation, $"{p}.timeline.modelState", timeline.ModelState);
        StoreNative(observation, $"{p}.timeline.overallSpeed", timeline.OverallSpeed);
        StoreNative(observation, $"{p}.timeline.baseOverride", timeline.BaseOverride);
        StoreNative(observation, $"{p}.timeline.lipsOverride", timeline.LipsOverride);
        StoreNative(observation, $"{p}.timeline.isWeaponDrawn", timeline.IsWeaponDrawn);
        var animationState = timeline.AnimationState;
        for (var i = 0; i < animationState.Length; ++i)
            StoreNative(observation, $"{p}.timeline.animationState[{i}]", animationState[i]);

        ref var sequencer = ref timeline.TimelineSequencer;
        StoreTimelineArray(observation, $"{p}.timeline.id", sequencer.TimelineIds);
        StoreTimelineArray(observation, $"{p}.timeline.id2", sequencer.TimelineIds2);
        StoreTimelineArray(observation, $"{p}.timeline.id3", sequencer.TimelineIds3);
        StoreTimelineArray(observation, $"{p}.timeline.id4", sequencer.TimelineIds4);
        StoreTimelineArray(observation, $"{p}.timeline.speed", sequencer.TimelineSpeeds);

        ref var model = ref character->ModelContainer;
        StoreNative(observation, $"{p}.model.charaId", model.ModelCharaId);
        StoreNative(observation, $"{p}.model.skeletonId", model.ModelSkeletonId);
        StoreNative(observation, $"{p}.model.charaId2", model.ModelCharaId_2);
        StoreNative(observation, $"{p}.model.skeletonId2", model.ModelSkeletonId_2);
        StoreNative(observation, $"{p}.model.scaleId", model.ModelScaleId);
        StoreNative(observation, $"{p}.model.modeAttributeFlags", model.ModeAttributeFlags);
        StoreNative(observation, $"{p}.model.unscaledRadius", model.UnscaledRadius);

        ref var transformation = ref character->Transformation;
        StoreNative(observation, $"{p}.transformation.stanceChangeId", transformation.StanceChangeId);
        StoreNative(observation, $"{p}.transformation.stanceChangeState", transformation.StanceChangeState);
        StoreNative(observation, $"{p}.transformation.timer", transformation.Timer);
        StoreNative(observation, $"{p}.transformation.flags", transformation.Flags);
        StoreNative(observation, $"{p}.transformation.effectIndex", transformation.EffectIndex);
        StoreNative(observation, $"{p}.transformation.flags2", transformation.Flags2);
        StoreNative(observation, $"{p}.transformation.isEffectPending", transformation.IsEffectPending);
        StoreNative(observation, $"{p}.transformation.isCharacterNotReady", transformation.IsCharacterNotReady);
        StoreNative(observation, $"{p}.transformation.npcEquipId", transformation.NpcEquipId);
        StoreNative(observation, $"{p}.transformation.areWeaponsLoaded", transformation.AreWeaponLoaded);
    }

    private static bool HasNativeCharacterLayout(ActorType type) => type is
        ActorType.Player or ActorType.Part or ActorType.Pet or ActorType.Chocobo or ActorType.Enemy or
        ActorType.Buddy or ActorType.Helper or ActorType.EventNpc or ActorType.MountType or
        ActorType.Companion or ActorType.Retainer or ActorType.Cutscene;

    private void StoreTimelineArray<T>(ForetellObservation observation, string prefix, Span<T> values)
        where T : unmanaged
    {
        StoreNative(observation, $"{prefix}.__count", values.Length);
        for (var i = 0; i < values.Length; ++i)
            StoreNative(observation, $"{prefix}[{i}]", values[i]);
    }

    private unsafe void SampleNativeEnvironment()
    {
        var env = EnvManager.Instance();
        if (env == null)
        {
            RegisterCapability("native.environment", typeof(EnvManager), "Instance", false, false, "native singleton unavailable");
            return;
        }

        var obs = Observation(ObservationKind.EnvironmentSnapshot, detail: "native EnvManager");
        StoreNative(obs, "native.environment.dayTimeSeconds", env->DayTimeSeconds);
        StoreNative(obs, "native.environment.activeTransitionTime", env->ActiveTransitionTime);
        StoreNative(obs, "native.environment.currentTransitionTime", env->CurrentTransitionTime);
        StoreNative(obs, "native.environment.transitionProgress", env->TransitionProgress);
        StoreNative(obs, "native.environment.activeWeather", env->ActiveWeather);
        StoreNative(obs, "native.environment.transitionTime", env->TransitionTime);
        StoreNative(obs, "native.environment.rain", env->EnvState.Rain);
        if (env->EnvScene != null)
        {
            StoreNative(obs, "native.environment.locationCount", env->EnvScene->LocationCount);
            var weatherIds = env->EnvScene->WeatherIds;
            StoreNative(obs, "native.environment.weatherId.__count", weatherIds.Length);
            for (var i = 0; i < weatherIds.Length; ++i)
                StoreNative(obs, $"native.environment.weatherId[{i}]", weatherIds[i]);
        }

        var fingerprint = Fingerprint(obs, "native.environment.");
        if (fingerprint == _environmentFabricFingerprint)
            return;
        _environmentFabricFingerprint = fingerprint;
        ProcessObservation(obs, enriched: true);
    }

    private void SampleNativeCamera()
    {
        if (Camera.Instance is not { } camera)
        {
            RegisterCapability("native.camera", typeof(Camera), "Instance", false, false, "active render camera unavailable");
            return;
        }

        var obs = Observation(ObservationKind.CameraSnapshot, detail: "active render camera");
        StoreVector(obs, "native.camera.origin", camera.Origin);
        StoreMatrix(obs, "native.camera.view", camera.View);
        StoreMatrix(obs, "native.camera.projection", camera.Proj);
        StoreMatrix(obs, "native.camera.viewProjection", camera.ViewProj);
        StoreVector(obs, "native.camera.nearPlane", camera.NearPlane);
        StoreNative(obs, "native.camera.azimuth", camera.CameraAzimuth);
        StoreNative(obs, "native.camera.altitude", camera.CameraAltitude);
        StoreNative(obs, "native.camera.viewport.width", camera.ViewportSize.X);
        StoreNative(obs, "native.camera.viewport.height", camera.ViewportSize.Y);
        StoreNative(obs, "native.camera.fov", camera.FieldOfView);
        StoreNative(obs, "native.camera.aspectRatio", camera.AspectRatio);
        StoreNative(obs, "native.camera.near", camera.NativeNearPlane);
        StoreNative(obs, "native.camera.far", camera.FarPlane);
        StoreNative(obs, "native.camera.orthoHeight", camera.OrthoHeight);
        StoreNative(obs, "native.camera.isOrtho", camera.IsOrtho);
        StoreNative(obs, "native.camera.standardZ", camera.StandardZ);
        StoreNative(obs, "native.camera.finiteFarPlane", camera.FiniteFarPlane);

        var fingerprint = Fingerprint(obs, "native.camera.");
        if (fingerprint == _cameraFabricFingerprint)
            return;
        _cameraFabricFingerprint = fingerprint;
        ProcessObservation(obs, enriched: true);
    }

    private void StoreVector(ForetellObservation observation, string prefix, Vector3 value)
    {
        StoreNative(observation, $"{prefix}.x", value.X);
        StoreNative(observation, $"{prefix}.y", value.Y);
        StoreNative(observation, $"{prefix}.z", value.Z);
    }

    private void StoreVector(ForetellObservation observation, string prefix, Vector4 value)
    {
        StoreNative(observation, $"{prefix}.x", value.X);
        StoreNative(observation, $"{prefix}.y", value.Y);
        StoreNative(observation, $"{prefix}.z", value.Z);
        StoreNative(observation, $"{prefix}.w", value.W);
    }

    private void StoreMatrix(ForetellObservation observation, string prefix, Matrix4x4 value)
    {
        StoreNative(observation, $"{prefix}.m11", value.M11); StoreNative(observation, $"{prefix}.m12", value.M12);
        StoreNative(observation, $"{prefix}.m13", value.M13); StoreNative(observation, $"{prefix}.m14", value.M14);
        StoreNative(observation, $"{prefix}.m21", value.M21); StoreNative(observation, $"{prefix}.m22", value.M22);
        StoreNative(observation, $"{prefix}.m23", value.M23); StoreNative(observation, $"{prefix}.m24", value.M24);
        StoreNative(observation, $"{prefix}.m31", value.M31); StoreNative(observation, $"{prefix}.m32", value.M32);
        StoreNative(observation, $"{prefix}.m33", value.M33); StoreNative(observation, $"{prefix}.m34", value.M34);
        StoreNative(observation, $"{prefix}.m41", value.M41); StoreNative(observation, $"{prefix}.m42", value.M42);
        StoreNative(observation, $"{prefix}.m43", value.M43); StoreNative(observation, $"{prefix}.m44", value.M44);
    }

    private void AuditDalamudPluginServices()
    {
        if (_pluginServicesAudited)
            return;

        var ingested = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(IClientState)] = "direct runtime root",
            [nameof(IChatGui)] = "structured native LogMessage event stream; private player chat is deliberately not recorded",
            [nameof(ICondition)] = "complete enum indexer",
            [nameof(IDataManager)] = "Lumina rows and game data",
            [nameof(IDutyState)] = "direct runtime root",
            [nameof(IFateTable)] = "direct runtime root",
            [nameof(IFlyTextGui)] = "raw FlyText event stream with binary SeString payloads",
            [nameof(IFramework)] = "WorldState frame root",
            [nameof(IGameConfig)] = "direct runtime root",
            [nameof(IGameGui)] = "direct runtime root",
            [nameof(IGamepadState)] = "direct runtime root",
            [nameof(IJobGauges)] = "lossless WorldState client gauge payload",
            [nameof(IKeyState)] = "complete enum indexer",
            [nameof(IObjectTable)] = "dedicated normalized and native actor roots",
            [nameof(IPartyList)] = "direct runtime root",
            [nameof(IBuddyList)] = "direct runtime root",
            [nameof(IPlayerState)] = "direct runtime root",
            [nameof(ITargetManager)] = "direct runtime root",
            [nameof(IToastGui)] = "normal, quest and error toast event streams with binary SeString payloads"
        };

        var nonGameplay = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(IAddonEventManager)] = "non-gameplay UI event plumbing",
            [nameof(IAddonLifecycle)] = "non-gameplay UI lifecycle",
            [nameof(IAetheryteList)] = "non-encounter travel/account state",
            [nameof(IAgentLifecycle)] = "non-gameplay UI agent lifecycle",
            [nameof(ICommandManager)] = "non-gameplay command plumbing",
            ["IConsole"] = "non-gameplay developer console",
            [nameof(IContextMenu)] = "non-gameplay UI plumbing",
            [nameof(IDalamudService)] = "service marker, not a data source",
            [nameof(IDtrBar)] = "non-gameplay UI presentation",
            [nameof(IGameInventory)] = "non-encounter inventory/economy state",
            [nameof(IGameInteropProvider)] = "sensor plumbing, not gameplay evidence",
            [nameof(IGameLifecycle)] = "process lifecycle, not encounter state",
            [nameof(IMarketBoard)] = "non-encounter economy state",
            [nameof(INamePlateGui)] = "derived UI presentation; actor state is ingested directly",
            [nameof(INotificationManager)] = "non-gameplay UI notifications",
            [nameof(IPartyFinderGui)] = "non-encounter matchmaking UI",
            [nameof(IPluginLog)] = "non-gameplay diagnostics",
            [nameof(IReliableFileStorage)] = "non-gameplay persistence plumbing",
            [nameof(ISeStringEvaluator)] = "non-gameplay text formatting",
            [nameof(ISelfTestRegistry)] = "non-gameplay diagnostics",
            [nameof(ISigScanner)] = "sensor plumbing, not gameplay evidence",
            [nameof(ITextureProvider)] = "non-gameplay rendering resource",
            [nameof(ITextureReadbackProvider)] = "non-gameplay rendering resource",
            [nameof(ITextureSubstitutionProvider)] = "non-gameplay rendering resource",
            [nameof(ITitleScreenMenu)] = "non-gameplay UI presentation",
            [nameof(IUnlockState)] = "non-encounter account progression"
        };

        IEnumerable<Type> services;
        try
        {
            services = typeof(IDalamudService).Assembly.GetTypes().Where(t => t.IsInterface && t.Namespace == "Dalamud.Plugin.Services" && typeof(IDalamudService).IsAssignableFrom(t));
        }
        catch (ReflectionTypeLoadException e)
        {
            services = e.Types.OfType<Type>().Where(t => t.IsInterface && t.Namespace == "Dalamud.Plugin.Services");
        }

        foreach (var service in services.OrderBy(t => t.Name))
        {
            var key = $"pluginService.{service.Name}";
            if (ingested.TryGetValue(service.Name, out var source))
                RegisterCapability(key, service, service.Name, true, false, source);
            else if (nonGameplay.TryGetValue(service.Name, out var reason))
                RegisterCapability(key, service, service.Name, false, true, reason);
            else
                RegisterCapability(key, service, service.Name, false, false, "new Dalamud service requires explicit gameplay classification");
        }
        _pluginServicesAudited = true;
    }
}
