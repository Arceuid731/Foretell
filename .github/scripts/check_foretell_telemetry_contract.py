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
    ],
    "BossMod/Foretell/ForetellObserver.cs": [
        'affected.Binary[$"{prefix}.raw"]',
        'resolved.Numeric["action.globalSequence"]',
        'obs.Numeric["effectResult.sequence"]',
        'obs.Numeric["actorControl.p8"]',
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

foretell_sources = "\n".join(
    path.read_text(encoding="utf-8-sig")
    for path in sorted((ROOT / "BossMod/Foretell").glob("*.cs"))
)

for forbidden, reason in {
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
