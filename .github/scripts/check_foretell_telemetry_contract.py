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
        "InitializeNativeHooks()",
        "InitializeDalamudSignals()",
        "private static DateTime NormalizeObservationTime",
        "At = ObservationNow()",
    ],
    "BossMod/Foretell/ForetellObserver.cs": [
        'affected.Binary[$"{prefix}.raw"]',
        'resolved.Numeric["action.globalSequence"]',
        'obs.Numeric["effectResult.sequence"]',
        'obs.Numeric["actorControl.p8"]',
        "WorldOperationSubstitution(op)",
        "ActorState.OpMove =>",
        "ClientState.OpActiveCompanionChange",
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
        "ProcessObservation(obs, enriched: true)",
        "MaxFabricTraversalMilliseconds",
        "MaxNativeActorsPerSlice",
        "MaxNativeActorTraversalMilliseconds",
        "EnrichActorCore(observation, actor, \"actor\")",
        "EnrichActorCollections(obs, actor)",
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
        "RadarUnlocked",
        "RadarPositionX",
        "RadarPositionY",
    ],
    "BossMod/Foretell/ForetellRenderer.cs": [
        "ForetellRadarWindow",
        "drag to move",
        "RadarPositionX",
        "MaxRenderedMechanics",
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
    ],
    "BossMod/Foretell/ForetellDalamudSignals.cs": [
        "Service.DutyState.DutyWiped += OnDutyWiped",
        "Service.FlyTextGui.FlyTextCreated += OnFlyText",
        "Service.ChatGui.LogMessage += OnDalamudLogMessage",
        "Service.ToastGui.Toast += OnNormalToast",
        'obs.Binary["dalamud.flyText.text1.raw"]',
        'obs.Binary["dalamud.toast.message.raw"]',
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
