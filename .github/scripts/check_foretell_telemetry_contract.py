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
        "RawActorControlReceived.Subscribe(OnRawActorControl)",
        "AttachRawCapture();",
        "RawServerIPCCapture = OnRawServerIPC",
        "RawClientIPCCapture = OnRawClientIPC",
        "RawActorControlCapture = OnRawActorControlCapture",
        "DetachRawCapture();",
        "_ws.Actors.EffectResult.Subscribe(OnEffectResult)",
        "private static readonly bool NativeHookTelemetryEnabled = true",
        "private static readonly bool NativeSnapshotTelemetryEnabled = true",
        "DrainNativeCaptures()",
        "_ws.Network.CaptureRawTransport = true",
        "ApplyPerformancePolicyMigration()",
        "InitializeDalamudSignals()",
        "private static DateTime NormalizeObservationTime",
        "At = ObservationNow()",
        "try { UpdateCore(); }",
        "PerformanceThrottled",
        "MaxSemanticObservationsPerFrame",
        "MaxSemanticMillisecondsPerFrame",
        "TryEnterSemanticBudget(bool priority = false)",
        "MaxPrioritySemanticObservationsPerFrame",
        "MaxPrioritySemanticMillisecondsPerFrame",
        "NormalizeEnum(ref _cfg.Mode",
        "NormalizeFinite(ref _cfg.RadarSize",
        "(DateTime.UtcNow - _rawOpenedAt).TotalHours >= 1",
        "NormalizeContextualMechanic",
        "Everything after background-resource construction is one transactional startup region",
        "ExpireHazardContext(now)",
    ],
    "BossMod/Foretell/ForetellObserver.cs": [
        'affected.Binary[$"{prefix}.raw"]',
        'resolved.Numeric["action.globalSequence"]',
        'obs.Numeric["effectResult.sequence"]',
        'obs.Numeric["actorControl.p8"]',
        "ProcessObservation(obs, enriched: true)",
        "age < 250",
        "WorldOperationSubstitution(op)",
        "ActorState.OpMove =>",
        "ClientState.OpActiveCompanionChange",
        "Combat chocobo",
        "SemanticBudgetAvailable(ForetellInferenceCore.IsPrioritySemanticObservation(ObservationKind.AffectedTarget",
        "_raw.EnqueueServer(context.Path, context.TerritoryID, packet)",
        "_raw.EnqueueClient(context.Path, context.TerritoryID, packet)",
        "_raw.EnqueueActorControl(context.Path, context.TerritoryID, DateTime.UtcNow, control)",
    ],
    "BossMod/Foretell/ForetellLearning.cs": [
        "observation.At = NormalizeObservationTime(observation.At)",
        "observation.Numeric ??= []",
        "bool replaying = false, bool enriched = false",
        "else if (!enriched) EnrichObservation(observation)",
        "LearnPhaseBoundary(observation)",
        "LearnCompositeMechanics(encounter, episode)",
        "LooksLikeGaze",
        "LooksLikeProximity",
        "CaptureResolutionPose",
        "ForetellInferenceCore.CanStartMechanicEpisode",
        "FiniteOrZero(observation.X)",
        "IssueMechanicPrediction",
        "ScheduleTimelineForecast",
        "ScheduleCompositeForecasts",
        "ResolveTimelineForecasts",
        "RecordCausalEdge",
        "UpdateRawProtocolMemory",
        "mechanic.AnchorStdDev > 3",
        "finalized >= 2",
        "TotalMilliseconds >= .65",
        "TouchOutOfCombatHazardContext",
        "ProcessObservationCore",
        "ChargeSemanticBudget(started)",
        "const int maxLiveEpisodes = 64",
        ".Take(12)",
        "IsSignalExcluded",
        "StorePrediction",
        "AddDecisionAudit",
        "DecisionAuditStage.Classified",
        "ObserveSignalTriggerContext",
        "UpdateTriggerContextForecasts",
        "PredictiveTriggerBasis.PhaseClock",
        "PredictiveTriggerBasis.BossHealth",
        "CurrentArenaBoundary is not { ArenaLike: true } boundary",
    ],
    "BossMod/Foretell/ForetellModel.cs": [
        "DecisionAuditStage",
        "DecisionAuditEntry",
        "public int Schema { get; set; } = 24",
        "public string PluginVersion { get; set; }",
        "List<DecisionAuditEntry> DecisionAudit",
        "SignalTriggerMemory",
        "Dictionary<string, SignalTriggerMemory> TriggerContexts",
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
        "bool includeNative = true",
        "if (!includeNative || !NativeSnapshotTelemetryEnabled || PerformanceThrottled)",
        "private static readonly bool LiveReflectionTelemetryEnabled = false",
        "SampleCoreRuntimeSnapshot()",
        "ProcessObservation(obs, enriched: true)",
        "MaxFabricTraversalMilliseconds",
        "if (!TryEnterSemanticBudget(ForetellInferenceCore.IsPrioritySemanticObservation(observation.Kind, observation.SourceKind)))",
        "MaxNativeActorsPerSlice",
        "MaxNativeActorTraversalMilliseconds",
        "NativeActorInterestRadius",
        "forceRelevant: true",
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
        "non-finite sentinels are retained as text",
    ],
    "BossMod/Framework/Plugin.cs": [
        "_openMainUiHandler = () => _foretell.OpenInspector()",
        "_openConfigUiHandler = () => _foretell.OpenInspector()",
        "_pluginSubscriptions.Add(Service.Config.Modified.Subscribe",
        "DisposeComponents();",
        "Interlocked.Exchange(ref _disposed, 1)",
    ],
    "BossMod/Config/ConfigUI.cs": [
        "n is Foretell.ForetellConfig",
    ],
    "BossMod/Foretell/ForetellConfig.cs": [
        "Hybrid = 2",
        "Foretell = 4",
        'string.Equals(mode.GetString(), "Compare"',
        "public enum ForetellRadarShape",
        "RadarShape = ForetellRadarShape.Auto",
        "RadarUnlocked",
        "RadarPositionX",
        "RadarPositionY",
        "TextHintsUnlocked",
        "TextPositionX",
        "TextPositionY",
        "public bool RecordReplay;",
        "ReplayPerformancePolicyVersion",
        "AutomaticStorageMaintenance",
        "RecordingRetentionDays",
        "MaximumRecordingStorageGiB",
    ],
    "BossMod/Foretell/ForetellRenderer.cs": [
        "ForetellRadarWindow",
        "ForetellTextHintsWindow",
        "GuidanceInstruction",
        "UserFacingPredictionLabel",
        "drag to move",
        "RadarPositionX",
        "DrawRadarFrame",
        "ForetellRadarShape.Square",
        "RadarWorldRadius",
        "MaxRenderedMechanics",
        "FiniteViewport(viewport)",
        "CameraRelativeRadarOffset",
        "DrawRadarActors",
        "DrawArenaBoundaryRadarFrame",
        "DrawDynamicTerrainRadar",
        "DrawDynamicTerrainWorld",
        "EffectiveRadarWorldRadius",
        "DrawWorldLineClipped",
        "DrawRadarLineClipped",
        "Walkability cannot establish attack occlusion",
        "CanTraverseSegment",
        "ProjectWorldAlertToTopology",
        "topology.Contours",
        "TryClipSegmentToCircle",
        "↑ camera",
    ],
    "BossMod/Foretell/ForetellInspector.cs": [
        'DrawInspectorTab("Knowledge", DrawKnowledgeExplorer)',
        'DrawInspectorTab("Recordings", DrawInspectorReplay)',
        "finally { ImGui.EndTabBar(); }",
        "finally { ImGui.EndTabItem(); }",
        "DrawKnowledgeExplorer()",
        "DrawPurgeConfirmation()",
        "Delete local data",
        "FULL SENSOR CONTRACT — HEALTHY",
        "rawBacklogged",
        "nativeBacklogged",
        "DrawStorageManager()",
        "Pause view",
        "PurgePhaseSignal",
        "Export signal filters",
        "Ignore signal",
        "Learned causal graph",
        "Raw protocol families",
        "Analysis ZIP",
        "Phase-clock / boss-HP triggers",
    ],
    "BossMod/Foretell/ForetellKnowledge.cs": [
        "ExportEncounterKnowledge",
        "RefreshEncounterIdentity",
        "EncounterDisplayName",
        "SourceDisplayName",
        "MechanicDisplayName",
        "PurgeCategory",
        "PurgeEncounter",
        "PurgeSource",
        "PurgeMechanic",
        "PurgeTopology",
        "PurgeArenaBoundary",
        "PurgeTimelineEdge",
        "PurgeComposite",
        "PurgeTriggerContext",
        "PurgePhase",
        "PurgeSession",
        "PurgeCausalEdge",
        "PurgeRawOpcode",
        "DeleteStorageFile",
        "RemoveOrphanGlobalKnowledge",
        "IgnoreSignal",
        "ImportSignalFilters",
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
        "MaxNativeHookBacklog",
        "MaxTrackedNativeVFX",
        "EnqueueNativeCapture",
        "DrainNativeCaptures",
        "MemoryHelper.ReadString(address, Encoding.ASCII, 512)",
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
        "MaxQueuedObservations",
        "TryAdd",
        "Rejected",
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
        "MaxQueuedPayloadBytes",
        "RejectedItems",
        "packet.Payload",
        "ForetellRawFeatureWindow",
        "PendingFeatureWindows",
        "RejectedFeatureWindows",
        "MaxQueuedFeatureWindows",
        "DurationTicks >= ForetellRawFormat.FeatureWindowTicks",
        "Records >= ForetellRawFormat.FeatureWindowMaxRecords",
        "public string ActivePath",
        "Volatile.Write(ref _activePath, path)",
    ],
    "BossMod/Foretell/ForetellAnalysisBundle.cs": [
        "StartAnalysisBundleExport",
        "Task.Run(() => CreateAnalysisBundle",
        "AnalysisRawJournals",
        "_raw.ActivePath",
        "DecisionAudit",
        "decisionAudit = decisions",
        '"foretell-analysis.json"',
        '"manifest.json"',
        "CompressionLevel.NoCompression",
        "displayEligibleMeaning",
        "sessionPluginVersion",
        "exporterPluginVersion",
    ],
    "BossMod/Foretell/ForetellRawFeatures.cs": [
        "MaxRawFeatureWindowsPerFrame",
        "MaxRawFeatureDrainMilliseconds",
        "_raw.TryDequeueFeature(out var window)",
        "RawWindowObservation(window, includeStructuralDetails: false)",
        "RegisterRecordedFeatures(obs)",
        "ProcessObservation(obs, enriched: true)",
    ],
    "BossMod/Foretell/ForetellRawFormat.cs": [
        "CurrentSchema = 2",
        "MaxPayloadBytes",
        "MaxInMemoryWindows",
        "ForetellRawWindowAccumulator",
        "FeatureWindowTicks = TimeSpan.TicksPerSecond",
        "FeatureWindowMaxRecords = 1024",
        "truncated payload",
        "ForetellRawOpcodeFeature",
        "SequenceHash",
        "ByteVariances",
        "Transitions",
    ],
    "BossMod/Foretell/ForetellReplay.cs": [
        "TryParseJournalTime",
        "OrderByDescending(File.GetLastWriteTimeUtc)",
        "DateTimeStyles.AdjustToUniversal",
        "ForetellRawFormat.Read",
        "MaxReadableReplayBytes",
        "EvaluateRecordedStream",
        "reader.Inspect",
        "SnapshotAsync",
        'Detail = "raw:feature-window"',
        "if (includeStructuralDetails)",
        'obs.Numeric["raw.window.payloadBytes"]',
        'obs.Numeric[$"raw.window.opcode[{opcode:X8}]"]',
        'obs.Numeric[$"raw.window.binaryBucket[{i}]"]',
    ],
    "BossMod/Foretell/ForetellCapture.cs": [
        "QueueLimit = 16L * 1024 * 1024", "SessionLimit = 64L * 1024 * 1024",
        "CacheLimit = 256L * 1024 * 1024", "CopyForRecording", "SnapshotAsync",
        "PruneCache", "SHA256.HashData", "Interlocked.Increment(ref session.Rejected)",
    ],
    "BossMod/Foretell/ForetellRecordingReader.cs": [
        "IEnumerable<ForetellObservation>", "MaxExpandedBytes", "capture/index.json", "integrity check failed",
    ],
    "BossMod/Foretell/ForetellTopology.cs": [
        "PollCompletedCollisionRaster",
        "PollCompletedTopologyAnalysis",
        "Task.Run",
        "RaycastMaterialFilter",
        "MaxTopologyRaysPerFrame",
        "TopologyBurstMillisecondsPerFrame",
        "TopologySteadyMillisecondsPerFrame",
        "RequestTopologyAnalysis",
        "SuspendTopology",
        "++_topologyInvalidations",
        "non-finite player position rejected before native call",
        "SampleNativeArenaBoundary",
        "ForetellTopologyFrontier",
        "ProbeTopologyEdge",
        "ForetellTopologyWindow.Plan",
        "_topologyAtomicSwaps",
        "_topologyRetainedRebuilds",
        "SampleTopologySceneFingerprint",
    ],
    "BossMod/Foretell/ForetellTopologyWindow.cs": [
        "ForetellTopologyWindowPlan",
        "MinimumPrefetchMargin",
        "AlignmentCells",
        "NeedsReplacement",
        "CoversVisible",
        "TryClipSegmentToCircle",
    ],
    "BossMod/Foretell/ForetellCollisionMeshSource.cs": [
        "ForetellCollisionMeshSource",
        "MaximumCaptureMilliseconds",
        "ForetellCollisionRules.Participates",
        "ForetellCollisionRules.EffectiveMaterial",
        "TryAcquireSceneLock",
        "ReleaseSceneLock",
        "ColliderType.Streamed",
        "ColliderType.Mesh",
        "ColliderType.Box",
        "ColliderType.Cylinder",
        "TryCapture",
        "TrySceneFingerprint",
        "MaximumFingerprintMilliseconds",
        "triangles.ToArray",
    ],
    "BossMod/Foretell/ForetellCollisionRasterizer.cs": [
        "ForetellCollisionRasterizer",
        "ForetellCollisionSnapshot",
        "MinimumFloorNormalY",
        "MaximumLayersPerCell",
        "ReachableLayers",
        "IsWallBlocked",
        "ForetellCollisionRasterResult Build",
    ],
    "BossMod/Foretell/ForetellArenaBoundary.cs": [
        "ArenaBoundaryRayCount = 96",
        "MaxArenaBoundaryRaysPerFrame = 4",
        "MaxArenaBoundaryMillisecondsPerFrame = .12",
        "BGCollisionModule.RaycastMaterialFilter",
        "ConditionFlag.InCombat",
        "ArenaEnemySummary",
        "HasBossCandidate",
        "SuspendTopology",
    ],
    "BossMod/Foretell/ForetellArenaBoundaryCore.cs": [
        "ForetellArenaBoundaryCore",
        "public static ArenaBoundaryAnalysis Analyze",
        "public static bool Contains",
        "public static bool IsBossCandidate",
        "playerMaximumHP * 2f",
        "arenaLike",
        "nothing about BMR modules or encounter identities",
    ],
    "BossMod/Foretell/ForetellDynamicTerrainCore.cs": [
        "BuildRadialSector",
        "peerAngles.Length < 3",
        "No territory, action, object ID or animation-state table enters the decision",
    ],
    "BossMod/Foretell/ForetellDynamicTerrain.cs": [
        "ObserveDynamicTerrainAnimation",
        "ActorType.EventObj",
        "now.AddSeconds(7)",
        "PromoteDynamicTerrainWarningsForPull",
        "warning.Expires <= now",
    ],
    "BossMod/Foretell/ForetellTopologyGrid.cs": [
        "Flood(seed, connected, maxStepHeight, requireKnownEdges)",
        "KnownEdges",
        "BlockedEdges",
        "BuildContours",
        "Fingerprint",
        "IsConnectedPassable",
        "TryConnectedHeight",
    ],
    "BossMod/Foretell/ForetellTypedSnapshots.cs": [
        "StoreTypedWorldSnapshot",
        "StoreColdTypedWorldSnapshot",
        "runtime.party.capacity",
        "runtime.client.cooldowns.capacity",
        "runtime.client.hate.primary",
        "runtime.deepDungeon.rooms",
        "Full player cooldown, inventory and progression collections are not encounter evidence",
    ],
    "BossMod/Data/NetworkState.cs": [
        "public volatile bool CaptureRawTransport",
        "RawServerIPCCapture",
        "RawClientIPCCapture",
        "RawActorControlCapture",
        "RejectedActorControlSemantic",
    ],
    "BossMod/Framework/WorldStateGameSync.cs": [
        "if (_ws.Network.CaptureRawTransport)",
        "var needPayload = _ws.Network.CaptureRawTransport || _netConfig.Data.RecordServerPackets || _netConfig.Data.DumpServerPackets",
        "RawServerIPCCapture?.Invoke",
        "RawClientIPCCapture?.Invoke",
        "RawActorControlCapture?.Invoke",
        "MaxForetellActorControlSemanticBacklog",
        "MaxForetellActorControlSemanticPerFrame",
    ],
    "BossMod/Foretell/OnlineClassifier.cs": [
        "if (!double.IsFinite(normalizedWeights[c][i]))",
        "if (!double.IsFinite(x[i])) continue",
        "Math.Clamp(w[i] + learningRate * error * x[i], -20, 20)",
    ],
    "BossMod/Foretell/ForetellInferenceCore.cs": [
        "CanStartMechanicEpisode",
        "CameraRelativeRadarOffset",
        "WilsonLowerBound",
        "GuidanceConfidence",
        "CausalConfidence",
        "TimelineProbability",
        "GeometryMatches",
        "IsGazeActionVFX",
        "IsAmbiguousLargeCircleAction",
        "IsReliableSpatialActionPrior",
    ],
    "BossMod/Foretell/ForetellStorageCore.cs": [
        "protectedFullPaths",
        "SearchOption.TopDirectoryOnly",
        "Math.Clamp(retentionDays",
        "maximumBytes",
    ],
    "BossMod/Foretell/ForetellStorage.cs": [
        "Task.Run",
        "StartStorageMaintenance",
        "PollStorageMaintenance",
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
normalization_pos = learning.find("observation.At = NormalizeObservationTime(observation.At)")
unsafe_arithmetic_pos = learning.find("observation.At.AddSeconds(-8)")
if unsafe_arithmetic_pos >= 0 and (normalization_pos < 0 or normalization_pos > unsafe_arithmetic_pos):
    errors.append("Foretell performs DateTime arithmetic before normalizing an uninitialized WorldState timestamp")

episode_trigger = learning[learning.find("private static bool IsEpisodeTrigger"):learning.find("private static string SignalKey")]
for noisy_trigger in ["DalamudLogMessage", "NormalToast", "QuestToast", "ErrorToast"]:
    if noisy_trigger in episode_trigger:
        errors.append(f"Foretell creates mechanic episodes from a diagnostic/UI stream: {noisy_trigger}")
inference = read("BossMod/Foretell/ForetellInferenceCore.cs")
mechanic_source_gate = inference[inference.find("public static bool CanStartMechanicEpisode"):inference.find("public static bool IsMechanicOutcomeEvidence")]
if "actorID != 0 && actorOID != 0" not in mechanic_source_gate:
    errors.append("Foretell can create mechanic episodes from unbound ambient native VFX")
if "SourceKind.Player or SourceKind.Pet" not in mechanic_source_gate:
    errors.append("Foretell can create learned mechanic episodes from player or pet actions")
if "if (mayStartEpisode && observation.Kind == ObservationKind.CastStart)" not in learning:
    errors.append("Foretell action metadata can bypass the mechanic-source admission gate")
if "episode ??= correlated ?? BestEpisode(observation)" in fabric:
    errors.append("Foretell ambient feature snapshots can attach to an unrelated episode by timestamp alone")

engine = read("BossMod/Foretell/ForetellEngine.cs")
if "SampleDataFabric(force: true, includeNative: false)" not in engine:
    errors.append("Foretell startup can perform native memory sampling before normal framework updates")
if engine.find("SampleDataFabric(force: true, includeNative: false)") > engine.find("InitializeNativeHooks()"):
    errors.append("Foretell installs native hooks before fallible initial Data Fabric sampling")
if "if (NativeHookTelemetryEnabled)\n                InitializeNativeHooks();" not in engine:
    errors.append("Foretell native hooks escaped their explicit data-complete gate")
if "if (!includeNative || !NativeSnapshotTelemetryEnabled || PerformanceThrottled)\n            return;" not in fabric:
    errors.append("Foretell native snapshots escaped their explicit data-complete gate")
if "_replay.Enqueue(_replayPath, observation.CopyForRecording())" not in engine or "_replay.WriteLine" in engine:
    errors.append("Foretell replay serialization can run synchronously on the framework thread")
if "_ws.Network.CaptureRawTransport = true" not in engine:
    errors.append("Foretell data-complete raw transport capture is not always armed")
if "if (!_inPull && !gameInCombat && (DateTime.UtcNow - _lastSave).TotalSeconds > 60)" not in engine:
    errors.append("Foretell persistent-store serialization can return to the active-combat frame path")
if "!_inPull && !gameInCombat && _cfg.AutomaticStorageMaintenance" not in engine:
    errors.append("Foretell storage maintenance can return to the active-combat frame path")

plugin = read("BossMod/Framework/Plugin.cs")
if plugin.find("_foretell = new(_ws") > plugin.find("_rsr = new(_dalamud)"):
    errors.append("Foretell startup can again fail after the legacy module installs its large hook graph")
if "_pluginSubscriptions.Dispose" not in plugin or "OpenMainUi -= _openMainUiHandler" not in plugin:
    errors.append("Foretell plugin lifecycle can leak global subscriptions after a partial load/unload")

game_sync = read("BossMod/Framework/WorldStateGameSync.cs")
for forbidden_queue in ["_foretellRawServerPackets", "_foretellRawClientPackets"]:
    if forbidden_queue in game_sync:
        errors.append(f"Foretell raw payloads returned to an unbounded framework-thread queue: {forbidden_queue}")

observer = read("BossMod/Foretell/ForetellObserver.cs")
server_handler = observer[observer.find("private void OnRawServerIPC"):observer.find("private void OnRawClientIPC")]
client_handler = observer[observer.find("private void OnRawClientIPC"):observer.find("private void OnRawActorControl")]
for name, handler in [("server", server_handler), ("client", client_handler)]:
    if "ProcessObservation(" in handler or "ProcessRichObservation(" in handler:
        errors.append(f"Foretell raw {name} transport re-entered the semantic learner")
    if "_raw.Enqueue" not in handler:
        errors.append(f"Foretell raw {name} transport is not retained by the lossless journal")
    if 'transport.payload' in handler or 'Record(' in handler:
        errors.append(f"Foretell raw {name} transport regressed to duplicate JSON serialization on the framework callback")
if "ProcessObservation(obs, enriched: true)" not in observer[observer.find("private void SamplePartyPositions"):observer.find("private static uint ReadActionID")]:
    errors.append("Foretell position sampling re-entered full actor/static enrichment")

config = read("BossMod/Foretell/ForetellConfig.cs")
if "public bool RecordReplay = true" in config:
    errors.append("Foretell high-volume replay recording became default-on again")
if re.search(r"^\s*Compare\s*[=,]", config, re.MULTILINE):
    errors.append("Foretell reintroduced the redundant Compare presentation mode")

main_window = read("BossMod/BossModule/BossModuleMainWindow.cs")
hints_window = read("BossMod/BossModule/BossModuleHintsWindow.cs")
if "ForetellMode.Hybrid" in main_window or "ForetellMode.Hybrid" in hints_window:
    errors.append("Hybrid no longer preserves the complete BMR presentation")

typed_snapshots = read("BossMod/Foretell/ForetellTypedSnapshots.cs")
hot_client_snapshot = typed_snapshots[typed_snapshots.find("private void StoreClient("):typed_snapshots.find("private void StoreDeepDungeon(")]
for forbidden_sweep in ["foreach (var (itemId, quantity) in client.Inventory)", "for (var i = 0; i < client.Cooldowns.Length; ++i)"]:
    if forbidden_sweep in hot_client_snapshot:
        errors.append(f"Foretell reintroduced a high-allocation periodic player-state sweep: {forbidden_sweep!r}")

native_hooks = read("BossMod/Foretell/ForetellNativeHooks.cs")
actor_create = native_hooks[native_hooks.find("private unsafe nint ForetellActorVFXCreateDetour"):native_hooks.find("private void ForetellActorVFXDestroyDetour")]
if actor_create.find(".Original(") > actor_create.find("ReadNativeString("):
    errors.append("Foretell actor VFX detour performs telemetry reads before the game constructor")
static_create = native_hooks[native_hooks.find("private unsafe VfxObject* ForetellStaticVFXCreateDetour"):native_hooks.find("private unsafe void ForetellStaticVFXDestroyDetour")]
if static_create.find(".Original(") > static_create.find("ReadNativeString("):
    errors.append("Foretell static VFX detour performs telemetry reads before the game constructor")
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

topology = read("BossMod/Foretell/ForetellTopology.cs")
if "ConditionFlag.InCombat" not in topology or "Task.Run" not in topology or "ForetellCollisionMeshSource.TryCapture" not in topology:
    errors.append("Foretell automatic topology escaped its native-snapshot/managed-worker safety policy")
invalidate = topology[topology.find("private void InvalidateTopology(") : topology.find("// Primary path")]
if "_topology.Cursor = 0" in invalidate:
    errors.append("Frequent topology invalidations can restart and starve the bounded sweep")
if "RequestTopologyAnalysis(player2, complete: false)" in topology:
    errors.append("Foretell rolling topology exposes progressive fallback chunks to the radar")
if "_topology.ReplaceWith(result.Grid)" not in topology or "_topologyRetainedRebuilds" not in topology:
    errors.append("Foretell rolling topology lost its complete-front/atomic-back-buffer publication policy")
if "AddSeconds(15)" in topology:
    errors.append("Foretell collision capture regressed to a user-visible 15-second retry gap")

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

arena_observer = read("BossMod/Foretell/ForetellArenaBoundary.cs") + read("BossMod/Foretell/ForetellArenaBoundaryCore.cs")
for forbidden in ["BossModuleManager", "BossModuleRegistry", "ArenaBounds", "BossMod.Modules."]:
    if forbidden in arena_observer:
        errors.append(f"Foretell arena observation imports authored BMR arena knowledge: {forbidden!r}")

authored_import = re.compile(r"^\s*using\s+BossMod\.(?:Modules|Components|BossModule)(?:\.|;)", re.MULTILINE)
if authored_import.search(foretell_sources):
    errors.append("Foretell imports hand-authored encounter knowledge")

if errors:
    print("Foretell telemetry contract FAILED:", file=sys.stderr)
    for error in errors:
        print(f" - {error}", file=sys.stderr)
    raise SystemExit(1)

print(f"Foretell telemetry contract OK ({sum(map(len, requirements.values()))} required markers).")
