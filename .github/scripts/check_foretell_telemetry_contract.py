#!/usr/bin/env python3
"""Fail CI when Foretell silently loses an encounter-agnostic telemetry surface."""

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[2]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


requirements = {
    "BossMod/Foretell/ForetellEngine.cs": [
        "RawServerIPCReceived.Subscribe(OnRawServerIPC)",
        "RawClientIPCSent.Subscribe(OnRawClientIPC)",
        "RawActorControlReceived.Subscribe(OnRawActorControl)",
        "_ws.Actors.EffectResult.Subscribe(OnEffectResult)",
        "private const bool NativeHookTelemetryEnabled = true",
        "private const bool NativeSnapshotTelemetryEnabled = true",
        "DrainNativeCaptures()",
        "_ws.Network.CaptureRawTransport = true",
        "ApplyPerformancePolicyMigration()",
        "InitializeDalamudSignals()",
        "private static DateTime NormalizeObservationTime",
        "At = ObservationNow()",
    ],
    "BossMod/Foretell/ForetellObserver.cs": [
        'affected.Binary[$"{prefix}.raw"]',
        'resolved.Numeric["action.globalSequence"]',
        'obs.Numeric["effectResult.sequence"]',
        'obs.Numeric["actorControl.p8"]',
        "RawTransportObservation(ObservationKind.ServerIPC",
        "ProcessObservation(obs, enriched: true)",
        "age < 250",
        "WorldOperationSubstitution(op)",
        "ActorState.OpMove =>",
        "ClientState.OpActiveCompanionChange",
        "_raw.EnqueueServer(_rawPath, _territory, packet)",
        "_raw.EnqueueClient(_rawPath, _territory, packet)",
        "_raw.EnqueueActorControl(_rawPath, _territory, captureAt, control)",
    ],
    "BossMod/Foretell/ForetellLearning.cs": [
        "observation.At = NormalizeObservationTime(observation.At)",
        "bool replaying = false, bool enriched = false",
        "else if (!enriched) EnrichObservation(observation)",
    ],
    "BossMod/Foretell/ForetellDataFabric.cs": [
        'FlattenRoot(_ws.Frame, "runtime.frame"',
        'FlattenRoot(_ws.Waymarks, "runtime.waymarks"',
        'FlattenRoot(_ws.Party, "runtime.party"',
        'FlattenRoot(_ws.Client, "runtime.client"',
        'FlattenRoot(_ws.DeepDungeon, "runtime.deepDungeon"',
        'FlattenRoot(_ws.Network, "runtime.network"',
        "foreach (var item in enumerable)",
        "AuditDalamudPluginServices()",
        "RefreshRuntimeContextSlice()",
        "SampleGenericActorSlice()",
        "SampleNativeActorSlice(now)",
        "private static readonly bool LiveReflectionTelemetryEnabled = false",
        "SampleCoreRuntimeSnapshot()",
        "ProcessObservation(obs, enriched: true)",
        "MaxFabricTraversalMilliseconds",
        "MaxNativeActorsPerSlice",
        "MaxNativeActorTraversalMilliseconds",
        "EnrichActorCore(observation, actor, \"actor\")",
        "EnrichActorCollections(obs, actor)",
        "StoreTypedWorldSnapshot(obs)",
        "--budget",
        "live getter rejected before invocation",
        "CanInvokeFabricGetter(type)",
        "CanTraverseFabricType(type)",
        "StoreConditionState(obs)",
        "StoreKeyState(obs)",
        "RejectNonBoxableMember(p.PropertyType",
        "RejectNonBoxableMember(f.FieldType",
        "memberType.IsFunctionPointer",
    ],
    "BossMod/Framework/Plugin.cs": [
        "OpenMainUi += () => _foretell.OpenInspector()",
        "OpenConfigUi += () => _foretell.OpenInspector()",
    ],
    "BossMod/Config/ConfigUI.cs": [
        "n is Foretell.ForetellConfig",
    ],
    "BossMod/Foretell/ForetellConfig.cs": [
        "public enum ForetellRadarShape",
        "RadarShape = ForetellRadarShape.Auto",
        "RadarUnlocked",
        "RadarPositionX",
        "RadarPositionY",
        "public bool RecordReplay;",
        "ReplayPerformancePolicyVersion",
    ],
    "BossMod/Foretell/ForetellRenderer.cs": [
        "ForetellRadarWindow",
        "drag to move",
        "RadarPositionX",
        "DrawRadarFrame",
        "ForetellRadarShape.Square",
        "RadarWorldRadius",
        "MaxRenderedMechanics",
    ],
    "BossMod/Foretell/ForetellInspector.cs": [
        'BeginTabItem("Knowledge explorer")',
        "DrawKnowledgeExplorer()",
        "DrawPurgeConfirmation()",
        "Delete learned data",
        "DATA COMPLETE — HEALTHY",
        "rawBacklogged",
        "nativeBacklogged",
    ],
    "BossMod/Foretell/ForetellKnowledge.cs": [
        "RefreshEncounterIdentity",
        "EncounterDisplayName",
        "SourceDisplayName",
        "MechanicDisplayName",
        "PurgeCategory",
        "PurgeEncounter",
        "PurgeSource",
        "PurgeMechanic",
        "RemoveOrphanGlobalKnowledge",
    ],
    "BossMod/Foretell/ForetellNativeState.cs": [
        'var tp = $"{p}.vfx.tether[{i}]"',
        '"{p}.timeline.overallSpeed"',
        '"{p}.model.unscaledRadius"',
        '"{p}.transformation.timer"',
        '"native.environment.activeWeather"',
        '"native.environment.transitionProgress"',
        '"native.camera.viewProjection"',
        "typeof(IDalamudService).Assembly.GetTypes()",
        "HasNativeCharacterLayout(actor.Type)",
    ],
    "BossMod/Foretell/ForetellNativeHooks.cs": [
        "ActorVFXCreateSignature",
        "ActorVFXDestroySignature",
        "StaticVFXDestroySignature",
        "ObservationKind.NativeVFXSpawn",
        "ObservationKind.NativeVFXDestroy",
        'StoreNative(obs, "native.vfx.path"',
        "ConcurrentQueue<NativeHookCapture>",
        "MaxNativeHookCapturesPerFrame",
        "MaxNativeHookDrainMilliseconds",
        "EnqueueNativeCapture",
        "DrainNativeCaptures",
    ],
    "BossMod/Foretell/ForetellDalamudSignals.cs": [
        "Service.DutyState.DutyWiped += OnDutyWiped",
        "Service.FlyTextGui.FlyTextCreated += OnFlyText",
        "ClassifyNonGameplayDalamudSignals()",
        'RegisterCapability("dalamud.logMessage"',
        'RegisterCapability("dalamud.toast.normal"',
        'obs.Binary["dalamud.flyText.text1.raw"]',
        'obs.Binary["dalamud.toast.message.raw"]',
    ],
    "BossMod/Foretell/ForetellReplayWriter.cs": [
        "BlockingCollection<Item>",
        "IsBackground = true",
        "JsonSerializer.Serialize(item.Observation",
        "GetConsumingEnumerable(_stop.Token)",
    ],
    "BossMod/Foretell/ForetellRawWriter.cs": [
        "BlockingCollection<Item>",
        "CompressionLevel.Fastest",
        "IsBackground = true",
        "GetConsumingEnumerable(_stop.Token)",
        "PendingItems",
        "RejectedItems",
        "packet.Payload",
        "ForetellRawFeatureWindow",
        "PendingFeatureWindows",
        "DurationTicks >= TimeSpan.TicksPerMillisecond * 250",
    ],
    "BossMod/Foretell/ForetellRawFeatures.cs": [
        "MaxRawFeatureWindowsPerFrame",
        "MaxRawFeatureDrainMilliseconds",
        "_raw.TryDequeueFeature(out var window)",
        'Detail = "raw:250ms-window"',
        'obs.Numeric["raw.window.payloadBytes"]',
        'obs.Numeric[$"raw.window.opcode[{opcode:X8}]"]',
        'obs.Numeric[$"raw.window.binaryBucket[{i}]"]',
        "ProcessObservation(obs, enriched: true)",
    ],
    "BossMod/Foretell/ForetellTypedSnapshots.cs": [
        "StoreTypedWorldSnapshot",
        "runtime.party.capacity",
        "runtime.client.cooldowns.capacity",
        "runtime.client.hate.primary",
        "runtime.deepDungeon.rooms",
        "foreach (var (itemId, quantity) in client.Inventory)",
    ],
    "BossMod/Data/NetworkState.cs": [
        "public volatile bool CaptureRawTransport",
    ],
    "BossMod/Framework/WorldStateGameSync.cs": [
        "if (_ws.Network.CaptureRawTransport)",
        "var needPayload = _ws.Network.CaptureRawTransport || _netConfig.Data.RecordServerPackets || _netConfig.Data.DumpServerPackets",
    ],
}

errors: list[str] = []
for path, needles in requirements.items():
    text = read(path)
    for needle in needles:
        if needle not in text:
            errors.append(f"{path}: missing contract marker {needle!r}")

fabric = read("BossMod/Foretell/ForetellDataFabric.cs")
for guard, invocation in [
    ("RejectNonBoxableMember(p.PropertyType", "p.GetValue(value)"),
    ("CanInvokeFabricGetter(type)", "p.GetValue(value)"),
    ("RejectNonBoxableMember(f.FieldType", "f.GetValue(value)"),
]:
    guard_at = fabric.find(guard)
    invocation_at = fabric.find(invocation)
    if invocation_at < 0:
        errors.append(f"Foretell reflection contract invocation disappeared without review: {invocation}")
    elif guard_at >= 0 and guard_at > invocation_at:
        errors.append(f"Foretell invokes non-boxable reflection member before its crash guard: {invocation}")

for forbidden in ["FlattenEnumIndexers(", "move.IsFlying()", "move.IsDiving()"]:
    if forbidden in fabric or forbidden in read("BossMod/Foretell/ForetellNativeState.cs"):
        errors.append(f"Foretell runtime crash guard regressed: forbidden live invocation {forbidden!r}")

if "NativeActorSlices" in fabric:
    errors.append("Foretell native actor sampling regressed to a population-proportional all-actor slice")

if "if (LiveReflectionTelemetryEnabled)\n            {\n                RefreshRuntimeContextSlice();\n                SampleGenericActorSlice();" not in fabric:
    errors.append("Foretell generic reflection scanner escaped the disabled live telemetry gate")

if fabric.find("CanTraverseFabricType(type)") > fabric.find("value is IEnumerable enumerable"):
    errors.append("Foretell can enumerate an external live implementation before applying its assembly allowlist")

learning = read("BossMod/Foretell/ForetellLearning.cs")
if learning.find("observation.At = NormalizeObservationTime(observation.At)") > learning.find("observation.At.AddSeconds(-8)"):
    errors.append("Foretell performs DateTime arithmetic before normalizing an uninitialized WorldState timestamp")

episode_trigger = learning[learning.find("private static bool IsEpisodeTrigger"):learning.find("private static string SignalKey")]
for noisy_trigger in ["DalamudLogMessage", "NormalToast", "QuestToast", "ErrorToast"]:
    if noisy_trigger in episode_trigger:
        errors.append(f"Foretell creates mechanic episodes from a diagnostic/UI stream: {noisy_trigger}")
if "observation.ActorID == 0 && observation.TargetID == 0" not in episode_trigger:
    errors.append("Foretell can create mechanic episodes from unbound ambient native VFX")

engine = read("BossMod/Foretell/ForetellEngine.cs")
if engine.find("SampleDataFabric(force: true)") > engine.find("InitializeNativeHooks()"):
    errors.append("Foretell installs native hooks before fallible initial Data Fabric sampling")
if "if (NativeHookTelemetryEnabled)\n                InitializeNativeHooks();" not in engine:
    errors.append("Foretell native hooks escaped their explicit data-complete gate")
if "if (!NativeSnapshotTelemetryEnabled)\n            return;" not in fabric:
    errors.append("Foretell native snapshots escaped their explicit data-complete gate")
if "_replay.Enqueue(_replayPath, observation)" not in engine or "_replay.WriteLine" in engine:
    errors.append("Foretell replay serialization can run synchronously on the framework thread")
if "_ws.Network.CaptureRawTransport = true" not in engine:
    errors.append("Foretell data-complete raw transport capture is not always armed")

observer = read("BossMod/Foretell/ForetellObserver.cs")
server_handler = observer[observer.find("private void OnRawServerIPC"):observer.find("private void OnRawClientIPC")]
client_handler = observer[observer.find("private void OnRawClientIPC"):observer.find("private void OnRawActorControl")]
for name, handler in [("server", server_handler), ("client", client_handler)]:
    if "ProcessObservation(" in handler or "ProcessRichObservation(" in handler:
        errors.append(f"Foretell raw {name} transport re-entered the semantic learner")
    if "_raw.Enqueue" not in handler:
        errors.append(f"Foretell raw {name} transport is not retained by the lossless journal")
if "ProcessObservation(obs, enriched: true)" not in observer[observer.find("private void SamplePartyPositions"):observer.find("private static uint ReadActionID")]:
    errors.append("Foretell position sampling re-entered full actor/static enrichment")

config = read("BossMod/Foretell/ForetellConfig.cs")
if "public bool RecordReplay = true" in config:
    errors.append("Foretell high-volume replay recording became default-on again")

native_hooks = read("BossMod/Foretell/ForetellNativeHooks.cs")
for start, end, name in [
    ("private unsafe void ForetellObjectEffectDetour", "private unsafe nint ForetellActorVFXCreateDetour", "ObjectEffect"),
    ("private unsafe nint ForetellActorVFXCreateDetour", "private void ForetellActorVFXDestroyDetour", "actor VFX create"),
    ("private void ForetellActorVFXDestroyDetour", "private unsafe VfxObject* ForetellStaticVFXCreateDetour", "actor VFX destroy"),
    ("private unsafe VfxObject* ForetellStaticVFXCreateDetour", "private unsafe void ForetellStaticVFXDestroyDetour", "static VFX create"),
    ("private unsafe void ForetellStaticVFXDestroyDetour", "private void EmitNativeVFX", "static VFX destroy"),
]:
    body = native_hooks[native_hooks.find(start):native_hooks.find(end)]
    for forbidden_call in ["ProcessObservation(", "ProcessRichObservation(", "EmitNativeVFX("]:
        if forbidden_call in body:
            errors.append(f"Foretell {name} detour performs deferred work directly: {forbidden_call}")
    if "EnqueueNativeCapture" not in body:
        errors.append(f"Foretell {name} detour does not enqueue its primitive capture")

foretell_sources = "\n".join(
    path.read_text(encoding="utf-8-sig")
    for path in sorted((ROOT / "BossMod/Foretell").glob("*.cs"))
)

for forbidden, reason in {
    "_runtimeNumeric": "duplicating the full runtime cache into every observation",
    "RefreshRuntimeContext()": "synchronous all-root runtime reflection sweep",
    'FlattenRoot(actor, "actor"': "unbounded reflective actor traversal on the framework thread",
    "if (budget <= 0 || f.IsStatic) break": "static field prematurely terminating a generic field scan",
    ".Take(32)": "fixed 32-entry telemetry sampling cap",
    "n >= 32": "fixed 32-entry telemetry sampling cap",
    "MaxFabricEntriesPerObject": "shared monolithic object budget",
    "FeatureSums.Count >=": "learner silently discarding new generic features",
    "s[..160]": "semantic text truncation",
    "ArenaBounds": "hand-authored encounter arena topology",
}.items():
    if forbidden in foretell_sources:
        errors.append(f"Foretell sources contain {reason}: {forbidden!r}")

authored_import = re.compile(r"^\s*using\s+BossMod\.(?:Modules|Components|BossModule)(?:\.|;)", re.MULTILINE)
if authored_import.search(foretell_sources):
    errors.append("Foretell imports hand-authored encounter knowledge")

if errors:
    print("Foretell telemetry contract FAILED:", file=sys.stderr)
    for error in errors:
        print(f" - {error}", file=sys.stderr)
    raise SystemExit(1)

print(f"Foretell telemetry contract OK ({sum(map(len, requirements.values()))} required markers).")
