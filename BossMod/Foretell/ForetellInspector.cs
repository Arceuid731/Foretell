using Dalamud.Bindings.ImGui;
using System.IO;
using System.Threading;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private uint _inspectorTerritory;
    private string _diagnosticsPath = "";
    private string _knowledgeExportPath = "";
    private Action? _pendingPurge;
    private string _pendingPurgeTitle = "";
    private string _pendingPurgeDescription = "";
    private string _pendingConfirmationButton = "Delete local data";
    private string _pendingConfirmationNote = "This removes local data permanently. Learned items can be rediscovered while learning is enabled.";
    private bool _openPurgeConfirmation;
    private string _purgeResult = "";
    private string _knowledgeFilter = "";
    private int _knowledgeConfidenceFilter;
    private bool _liveFeedPaused;
    private string _liveFeedFilter = "";
    private int _liveFeedKindFilter;
    private List<ForetellObservation> _pausedLiveFeed = [];
    private DateTime _lastStorageRefresh;
    private List<StorageFileEntry> _storageFiles = [];
    private static readonly string[] RadarShapeLabels = ["Auto (observed arena boundary)", "Circle", "Square"];
    private static readonly string[] RadarZoomLabels = ["Automatic (bounded arena fit)", "Manual"];
    private static readonly string[] RadarTerrainStyleLabels = ["Outline only", "Outline + filled surface"];
    private static readonly string[] KnowledgeConfidenceLabels = ["All confidence levels", "Learned (75%+)", "High (95%+)", "Safe (99%+)"];
    private static readonly string[] ObservationKindLabels = ["All event types", .. Enum.GetNames<ObservationKind>()];
    private readonly record struct StorageFileEntry(string Path, string Kind, long Bytes, DateTime Updated, bool Active);

    public bool HandleCommand(string[] args)
    {
        if (args.Length == 0)
        {
            ToggleInspector();
            return true;
        }

        switch (args[0].ToUpperInvariant())
        {
            case "INSPECT":
            case "DEBUG":
            case "STATS":
                OpenInspector();
                return true;
            case "HELP":
            case "?":
                OpenInspector();
                PrintCommandHelp();
                return true;
            case "MODE":
                if (args.Length < 2 || !Enum.TryParse<ForetellMode>(args[1], true, out var mode))
                {
                    Service.ChatGui.Print("Foretell modes: legacy | observe | hybrid | foretell");
                    return true;
                }
                SetMode(mode);
                Service.ChatGui.Print($"Foretell mode: {mode} - {ModeDescription(mode)}");
                return true;
            case "LEARNING":
            case "LEARN":
                if (TryToggle(args, ref _cfg.EnableLearning, "learning")) return true;
                break;
            case "RECORD":
            case "RECORDING":
                if (TryToggle(args, ref _cfg.RecordReplay, "replay recording")) return true;
                break;
            case "REPLAY":
                var report = ReplayLatest();
                OpenInspector();
                Service.ChatGui.Print($"Foretell replay: {report.Status}");
                return true;
            case "EXPORT":
                try
                {
                    _diagnosticsPath = ExportDiagnostics();
                    OpenInspector();
                    Service.ChatGui.Print($"Foretell diagnostics: {_diagnosticsPath}");
                }
                catch (Exception e)
                {
                    Service.ChatGui.PrintError($"Foretell diagnostics export failed: {e.Message}");
                }
                return true;
            case "SAVE":
                SaveStore();
                Service.ChatGui.Print("Foretell memory saved.");
                return true;
        }
        return false;
    }

    private bool TryToggle(string[] args, ref bool value, string label)
    {
        if (args.Length < 2)
        {
            Service.ChatGui.Print($"Foretell {label}: {(value ? "ON" : "OFF")} (use on/off)");
            return true;
        }
        if (args[1].Equals("on", StringComparison.OrdinalIgnoreCase)) value = true;
        else if (args[1].Equals("off", StringComparison.OrdinalIgnoreCase)) value = false;
        else
        {
            Service.ChatGui.Print($"Foretell {label}: use on/off");
            return true;
        }
        _cfg.Modified.Fire();
        Service.ChatGui.Print($"Foretell {label}: {(value ? "ON" : "OFF")}");
        return true;
    }

    private void PrintCommandHelp()
    {
        Service.ChatGui.Print("Foretell commands: /foretell, inspect, stats, mode <legacy|observe|hybrid|foretell>, learning <on|off>, record <on|off>, replay, export, save, help");
    }

    private void SetMode(ForetellMode mode)
    {
        if (_cfg.Mode == mode) return;
        _cfg.Mode = mode;
        _cfg.Modified.Fire();
    }

    private static string ModeDescription(ForetellMode mode) => mode switch
    {
        ForetellMode.Legacy => "BossMod Reborn presentation only; Foretell guidance is hidden.",
        ForetellMode.Observe => "Recommended first step: Foretell learns silently while BMR remains your guide.",
        ForetellMode.Hybrid => "Shows the complete BMR and Foretell presentations together.",
        ForetellMode.Foretell => "Pure Foretell presentation; legacy BMR encounter hints are hidden.",
        _ => ""
    };

    private void DrawInspector()
    {
        if (!_inspectorOpen) return;
        ImGui.SetNextWindowSize(new Vector2(960, 720), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Foretell - Adaptive Encounter Intelligence###ForetellInspector", ref _inspectorOpen))
        {
            ImGui.End();
            return;
        }

        try
        {
            _inspectorTerritory = _inspectorTerritory == 0 ? _territory : _inspectorTerritory;
            DrawInspectorHeader();
            if (ImGui.BeginTabBar("ForetellInspectorTabs"))
            {
                try
                {
                    DrawInspectorTab("Dashboard", DrawDashboard);
                    DrawInspectorTab("Knowledge explorer", DrawKnowledgeExplorer);
                    DrawInspectorTab("Timeline", DrawInspectorTimeline);
                    DrawInspectorTab("Live feed", DrawInspectorObservations);
                    DrawInspectorTab("Replay & storage", DrawInspectorReplay);
                    DrawInspectorTab("Settings", DrawInspectorSettings);
                    DrawInspectorTab("Help", DrawInspectorHelp);
                }
                finally { ImGui.EndTabBar(); }
            }
            DrawPurgeConfirmation();
        }
        finally
        {
            ImGui.End();
        }
    }

    private void DrawInspectorHeader()
    {
        if (ImGui.BeginTable("ForetellHeader", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
        {
            DrawMetricCell(_cfg.EnableLearning ? "LEARNING" : "READ-ONLY", "Engine");
            DrawMetricCell(EncounterName(_territory), "Current content");
            DrawMetricCell(_session.Observations.ToString("N0"), "Observations");
            DrawMetricCell(_session.MechanicsFinalized.ToString(), "Reviewed");
            ImGui.EndTable();
        }

        ImGui.TextUnformatted("Mode");
        ImGui.SameLine();
        DrawModeButton(ForetellMode.Legacy);
        ImGui.SameLine();
        DrawModeButton(ForetellMode.Observe);
        ImGui.SameLine();
        DrawModeButton(ForetellMode.Hybrid);
        ImGui.SameLine();
        DrawModeButton(ForetellMode.Foretell);

        ImGui.SameLine();
        ImGui.TextUnformatted("  Territory");
        ImGui.SameLine();
        if (ImGui.Button($"Current: {EncounterName(_territory)}##current-territory"))
            _inspectorTerritory = _territory;
        foreach (var id in _store.Encounters.Keys.Where(id => id != _territory).OrderByDescending(id => _store.Encounters[id].LastSeen).Take(4))
        {
            ImGui.SameLine();
            if (ImGui.Button($"{EncounterName(id)}##territory{id}"))
                _inspectorTerritory = id;
        }
        ImGui.Separator();
    }

    private static void DrawMetricCell(string value, string label)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
        ImGui.TextDisabled(label);
    }

    private void DrawModeButton(ForetellMode mode)
    {
        var selected = _cfg.Mode == mode;
        if (ImGui.Button($"{(selected ? "[x]" : "[ ]")} {mode}##mode{mode}"))
            SetMode(mode);
    }

    private void DrawDashboard()
    {
        DrawRecommendedNextStep();
        DrawTelemetryStatus();

        if (!_store.Encounters.TryGetValue(_inspectorTerritory, out var encounter))
        {
            ImGui.Separator();
            ImGui.TextUnformatted("No encounter data yet");
            ImGui.TextWrapped("Enter content with learning enabled. Raw packets, casts, effects, statuses, VFX, tethers, actor state and movement are captured automatically.");
            return;
        }

        var visualCut = _cfg.VisualConfidence / 100f;
        var warningCut = _cfg.WarningConfidence / 100f;
        var safeCut = _cfg.SafeConfidence / 100f;
        var visual = encounter.Mechanics.Values.Count(m => m.GuidanceConfidence >= visualCut);
        var warnings = encounter.Mechanics.Values.Count(m => m.GuidanceConfidence >= warningCut);
        var safe = encounter.Mechanics.Values.Count(m => m.GuidanceConfidence >= safeCut);
        var coverage = _store.Coverage;

        ImGui.Separator();
        if (ImGui.BeginTable("ForetellSummary", 5, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
        {
            DrawMetricCell(encounter.Sessions.ToString(), "Sessions");
            DrawMetricCell(encounter.Pulls.ToString(), "Pulls");
            DrawMetricCell(encounter.Sources.Count.ToString(), "Sources");
            DrawMetricCell(encounter.Mechanics.Count.ToString(), "Candidates");
            DrawMetricCell(_predictions.Count.ToString(), "Live predictions");
            ImGui.EndTable();
        }
        if (ImGui.BeginTable("ForetellConfidence", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
        {
            DrawMetricCell(visual.ToString(), $">= {_cfg.VisualConfidence:F0}% visual");
            DrawMetricCell(warnings.ToString(), $">= {_cfg.WarningConfidence:F0}% warning");
            DrawMetricCell(safe.ToString(), $">= {_cfg.SafeConfidence:F0}% safe");
            DrawMetricCell(_store.ML.Updates.ToString("N0"), "ML updates");
            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Data Fabric");
        ImGui.SameLine();
        ImGui.TextUnformatted($"{coverage.Ingested}/{coverage.Discovered} ingested  |  {coverage.Used} used  |  {coverage.Excluded} excluded  |  {coverage.Unaccounted} unaccounted");
        ImGui.TextUnformatted($"Live scanner: {_fabricDeferredTraversals:N0} yielded slices  |  {_fabricRejectedGetters:N0} unsafe getters rejected");
        if (coverage.Unaccounted != 0)
            ImGui.TextWrapped("Some discovered fields still require typed ingestion. Raw, semantic and native sensors continue independently.");
        ImGui.TextWrapped($"Last inference: {_lastEvidence}");

        ImGui.Separator();
        ImGui.TextUnformatted("Best learned mechanics");
        if (ImGui.BeginTable("ForetellBestMechanics", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Confidence");
            ImGui.TableSetupColumn("Kind");
            ImGui.TableSetupColumn("Geometry");
            ImGui.TableSetupColumn("Seen");
            ImGui.TableSetupColumn("Source");
            ImGui.TableSetupColumn("Trigger");
            ImGui.TableHeadersRow();
            foreach (var mechanic in encounter.Mechanics.Values.OrderByDescending(m => m.GuidanceConfidence).Take(12))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{ConfidenceBadge(mechanic.GuidanceConfidence)} {mechanic.GuidanceConfidence:P0} verified");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(mechanic.Kind.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(mechanic.Geometry.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(mechanic.Observations.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(encounter.Sources.TryGetValue(mechanic.SourceOID, out var source) ? SourceDisplayName(source) : mechanic.SourceKind.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(MechanicDisplayName(mechanic));
            }
            ImGui.EndTable();
        }
    }

    private void DrawInspectorSettings()
    {
        var changed = false;

        if (ImGui.CollapsingHeader("Learning and storage", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= ImGui.Checkbox("Adaptive learning", ref _cfg.EnableLearning);
            changed |= ImGui.Checkbox("Local ML classifier", ref _cfg.EnableML);
            changed |= ImGui.Checkbox("Record local replay stream", ref _cfg.RecordReplay);
            changed |= ImGui.Checkbox("Automatically prune old recordings", ref _cfg.AutomaticStorageMaintenance);
            changed |= ImGui.SliderInt("Recording retention (days)", ref _cfg.RecordingRetentionDays, 1, 365);
            changed |= ImGui.SliderInt("Recording storage quota (GiB)", ref _cfg.MaximumRecordingStorageGiB, 1, 100);
            ImGui.TextDisabled("All data stays local. Automatic cleanup only touches inactive raw/replay files; learned memory and active files are protected.");
        }

        if (ImGui.CollapsingHeader("Combat presentation", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= ImGui.Checkbox("World-space overlay", ref _cfg.WorldOverlay);
            changed |= ImGui.Checkbox("Text hints", ref _cfg.TextHints);
            changed |= ImGui.Checkbox("Unlock text hints to move them", ref _cfg.TextHintsUnlocked);
            if (ImGui.Button("Reset text hints to top-center"))
            {
                _cfg.TextPositionX = -1;
                _cfg.TextPositionY = -1;
                changed = true;
            }
            ImGui.SameLine();
            ImGui.TextDisabled(_cfg.TextHintsUnlocked ? "Drag the guidance window, then lock it here." : "Locked: text hints ignore mouse input.");
            changed |= ImGui.Checkbox("Safe-position suggestions", ref _cfg.SafePositionSuggestions);
            changed |= ImGui.SliderFloat("Visual threshold (%)", ref _cfg.VisualConfidence, 50, 100);
            changed |= ImGui.SliderFloat("Warning threshold (%)", ref _cfg.WarningConfidence, 50, 100);
            changed |= ImGui.SliderFloat("Safe threshold (%)", ref _cfg.SafeConfidence, 50, 100);
            changed |= ImGui.SliderInt("Maximum simultaneous mechanics", ref _cfg.MaxRenderedMechanics, 1, 32);
            _cfg.WarningConfidence = Math.Max(_cfg.VisualConfidence, _cfg.WarningConfidence);
            _cfg.SafeConfidence = Math.Max(_cfg.WarningConfidence, _cfg.SafeConfidence);
        }

        if (ImGui.CollapsingHeader("Mini radar", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= ImGui.Checkbox("Show mini radar", ref _cfg.MiniRadar);
            changed |= ImGui.Checkbox("Unlock radar to move it", ref _cfg.RadarUnlocked);
            var radarShape = (int)_cfg.RadarShape;
            if (ImGui.Combo("Arena frame", ref radarShape, RadarShapeLabels, RadarShapeLabels.Length))
            {
                _cfg.RadarShape = (ForetellRadarShape)radarShape;
                changed = true;
            }
            changed |= ImGui.SliderFloat("Radar size on screen (pixels)", ref _cfg.RadarSize, 140, 600);
            var radarZoom = (int)_cfg.RadarZoom;
            if (ImGui.Combo("Zoom mode", ref radarZoom, RadarZoomLabels, RadarZoomLabels.Length))
            {
                _cfg.RadarZoom = (ForetellRadarZoom)radarZoom;
                changed = true;
            }
            if (_cfg.RadarZoom == ForetellRadarZoom.Manual)
                changed |= ImGui.SliderFloat("Manual distance to edge (yalms)", ref _cfg.RadarWorldRadius, 5, 120);
            else
            {
                changed |= ImGui.SliderFloat("Auto zoom minimum (yalms)", ref _cfg.RadarAutoMinimumRadius, 10, 60);
                _cfg.RadarAutoMaximumRadius = Math.Max(_cfg.RadarAutoMaximumRadius, _cfg.RadarAutoMinimumRadius);
                changed |= ImGui.SliderFloat("Auto zoom maximum (yalms)", ref _cfg.RadarAutoMaximumRadius, Math.Max(20, _cfg.RadarAutoMinimumRadius), 120);
                ImGui.TextDisabled("Closed rooms are fitted automatically; open terrain and unfinished corridors keep the minimum zoom.");
            }
            var terrainStyle = (int)_cfg.RadarTerrainStyle;
            if (ImGui.Combo("Terrain drawing", ref terrainStyle, RadarTerrainStyleLabels, RadarTerrainStyleLabels.Length))
            {
                _cfg.RadarTerrainStyle = (ForetellRadarTerrainStyle)terrainStyle;
                changed = true;
            }
            var terrainColor = new Vector4(
                (_cfg.RadarTerrainColor & 0xFF) / 255f,
                ((_cfg.RadarTerrainColor >> 8) & 0xFF) / 255f,
                ((_cfg.RadarTerrainColor >> 16) & 0xFF) / 255f,
                ((_cfg.RadarTerrainColor >> 24) & 0xFF) / 255f);
            if (ImGui.ColorEdit4("Terrain colour", ref terrainColor, ImGuiColorEditFlags.PickerHueWheel | ImGuiColorEditFlags.AlphaBar))
            {
                static uint Channel(float value) => (uint)Math.Clamp((int)MathF.Round(value * 255), 0, 255);
                _cfg.RadarTerrainColor = Channel(terrainColor.X) | Channel(terrainColor.Y) << 8
                    | Channel(terrainColor.Z) << 16 | Channel(terrainColor.W) << 24;
                changed = true;
            }
            if (_cfg.RadarShape == ForetellRadarShape.Auto)
                ImGui.TextDisabled(_topologyAnalysis is { PassableCells: > 0 }
                    ? $"Auto uses the live local collision mesh ({_topologyAnalysis.PassableCells:N0} connected cells; {_topologyEdgeSamples:N0} barrier probes; {_topologySampleRadius:F0}y survey radius)."
                    : CurrentArenaBoundary is { } boundary
                        ? $"Auto temporarily uses a near-enclosed wall outline ({boundary.Hits}/{boundary.Rays} rays)."
                        : "Auto is building the nearby walkable mesh; a circle is used until the connected seed is ready.");
            if (ImGui.Button("Reset radar to top-right"))
            {
                _cfg.RadarPositionX = -1;
                _cfg.RadarPositionY = -1;
                changed = true;
            }
            ImGui.SameLine();
            ImGui.TextDisabled(_cfg.RadarUnlocked ? "Drag the radar title bar, then lock it here." : "Locked: radar ignores mouse input.");
        }

        if (changed)
            _cfg.Modified.Fire();
    }

    private void DrawTelemetryStatus()
    {
        var coverage = _store.Coverage;
        var rawBacklogged = _raw.PendingItems > 4096 || _raw.PendingBytes > 16 * 1024 * 1024 || _raw.PendingFeatureWindows > 256
            || _raw.RejectedFeatureWindows != 0 || Interlocked.Read(ref _ws.Network.RejectedActorControlSemantic) != 0;
        var nativeBacklogged = _nativeHookPending > 2048;
        var replayDegraded = _replay is { } replayWriter && (replayWriter.Failed || replayWriter.Rejected != 0 || replayWriter.Pending > 4096);
        var topologyHealthy = !TopologySuspended;
        var runtimeHealthy = _updateFailures == 0 && _drawFailures == 0 && _episodeRejections == 0 && _learningEvictions == 0
            && _semanticObservationsRejected == 0 && !PerformanceThrottled;
        var healthy = !_raw.Failed && _raw.RejectedItems == 0 && !rawBacklogged && !nativeBacklogged && !replayDegraded && _nativeHookFailures == 0 && _typedSnapshotFailures == 0 && _nativeSnapshotFailures == 0 && topologyHealthy && runtimeHealthy && coverage.Unaccounted == 0;
        ImGui.Separator();
        ImGui.TextUnformatted("Telemetry completeness");
        ImGui.SameLine();
        ImGui.TextUnformatted(healthy ? "FULL SENSOR CONTRACT — HEALTHY" : "SENSOR CONTRACT — DEGRADED / AUDIT REQUIRED");
        if (ImGui.BeginTable("ForetellTelemetryStatus", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Surface");
            ImGui.TableSetupColumn("State");
            ImGui.TableSetupColumn("Critical-path policy");
            ImGui.TableHeadersRow();
            DrawTelemetryRow("World state + semantic network events", "ACTIVE", "Processed with bounded typed handlers");
            DrawTelemetryRow("Raw server/client IPC + ActorControl", _raw.Failed ? "FAILED" : rawBacklogged ? "BACKLOG" : "LOSSLESS", $"Gzip: {_raw.PendingItems:N0} queued / {_raw.WrittenItems:N0} written / {_raw.RejectedItems:N0} rejected; features {_raw.PendingFeatureWindows:N0} queued / {_rawFeatureWindowsProcessed:N0} learned / {_raw.RejectedFeatureWindows:N0} rejected; semantic ActorControl {Interlocked.Read(ref _ws.Network.RejectedActorControlSemantic):N0} rejected; drain {_lastRawFeatureDrainMilliseconds:F2} ms (peak {_peakRawFeatureDrainMilliseconds:F2})");
            DrawTelemetryRow("Readable Replay Lab stream", !_cfg.RecordReplay ? "OPTIONAL / OFF" : replayDegraded ? "DEGRADED" : "ACTIVE", _replay == null ? "Exact raw journal remains active" : $"{_replay.Pending:N0} queued / {_replay.Written:N0} written / {_replay.Rejected:N0} rejected; normalized events only");
            DrawTelemetryRow("Typed runtime snapshots", _typedSnapshotFailures == 0 && _nativeSnapshotFailures == 0 ? "ACTIVE" : "DEGRADED", $"1 Hz typed {_lastTypedSnapshotMilliseconds:F2} ms (peak {_peakTypedSnapshotMilliseconds:F2}); native {_lastNativeActorMilliseconds:F2} ms (peak {_peakNativeActorMilliseconds:F2}); {_typedSnapshotFailures + _nativeSnapshotFailures:N0} rejects");
            DrawTelemetryRow("Generic live reflection", "REPLACED", "Typed roots + WorldState deltas; no unmanaged getters on frame thread");
            DrawTelemetryRow("Native ObjectEffect + VFX lifecycle", _nativeHookFailures == 0 ? nativeBacklogged ? "BACKLOG" : "ACTIVE" : "DEGRADED", $"Primitive queue: {_nativeHookPending:N0} queued / {_nativeHookProcessed:N0} processed / {_nativeHookFailures:N0} rejected; drain {_lastNativeHookDrainMilliseconds:F2} ms (peak {_peakNativeHookDrainMilliseconds:F2})");
            DrawTelemetryRow("Native collision topology", !topologyHealthy ? "SAFE COOLDOWN" : CurrentArenaBoundary != null || _topologyAnalysis != null ? "ACTIVE" : "SCANNING", $"Local only; {_arenaBoundaryRays:N0} radial rays / {_arenaBoundarySweeps:N0} boundary sweeps / {_arenaBoundaryChanges:N0} changes; {_topologyFloorSamples:N0} floor + {_topologyEdgeSamples:N0} barrier probes / {_topologySweeps:N0} mesh sweeps; {_topologyInvalidations:N0} structural signals; {_lastTopologyMilliseconds:F2} ms (peak {_peakTopologyMilliseconds:F2}); {_topologyOverruns:N0} overruns / {_topologyFailures:N0} rejects");
            DrawTelemetryRow("Foretell frame budget", runtimeHealthy ? "HEALTHY" : PerformanceThrottled ? "ADAPTIVE THROTTLE" : "DEGRADED", $"update {_lastUpdateMilliseconds:F2} ms last / {_meanUpdateMilliseconds:F2} ms mean / {_peakUpdateMilliseconds:F2} ms peak; semantic {_semanticMillisecondsThisFrame:F2} ms this frame / {_semanticPeakMilliseconds:F2} ms peak observation; {_semanticBudgetTrips:N0} burst trips / {_semanticObservationsRejected:N0} derived observations shed / {_updateOverruns:N0} update overruns / {_episodeRejections:N0} episode rejects");
            DrawTelemetryRow("Coverage ledger", coverage.Unaccounted == 0 ? "ACCOUNTED" : "INCOMPLETE", $"{coverage.Ingested} ingested / {coverage.Excluded} explicitly excluded / {coverage.Unaccounted} unaccounted");
            ImGui.EndTable();
        }
        if (_raw.Failed)
            ImGui.TextWrapped($"Raw journal failure: {_raw.Failure}. Foretell is explicitly degraded until storage is restored; the failure is not hidden as successful capture.");
        ImGui.TextWrapped("Data-complete mode keeps unique events lossless and samples continuous state at bounded, change-detected cadences. Raw journals stay local in the Foretell configuration directory and are never uploaded automatically.");
    }

    private static void DrawTelemetryRow(string surface, string state, string policy)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(surface);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(state);
        ImGui.TableNextColumn();
        ImGui.TextDisabled(policy);
    }

    private void DrawKnowledgeExplorer()
    {
        ImGui.SetNextItemWidth(360);
        ImGui.InputText("Search names, zones, IDs or mechanic types", ref _knowledgeFilter, 160);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(190);
        ImGui.Combo("Confidence", ref _knowledgeConfidenceFilter, KnowledgeConfidenceLabels, KnowledgeConfidenceLabels.Length);
        ImGui.SameLine();
        if (ImGui.Button("Clear filters")) { _knowledgeFilter = ""; _knowledgeConfidenceFilter = 0; }
        ImGui.TextDisabled("Content → territory/duty → arena, environment, bosses/mobs → learned mechanics. Every level can be deleted.");
        ImGui.Separator();

        if (_store.Encounters.Count == 0)
        {
            ImGui.TextUnformatted("Nothing discovered yet.");
            return;
        }

        foreach (var categoryGroup in _store.Encounters.Values
            .OrderBy(encounter => encounter.ContentCategory)
            .ThenBy(encounter => EncounterDisplayName(encounter))
            .GroupBy(encounter => string.IsNullOrWhiteSpace(encounter.ContentCategory) ? "Unclassified" : encounter.ContentCategory))
        {
            var category = categoryGroup.Key;
            var allEncounters = categoryGroup.ToArray();
            var encounters = allEncounters.Where(KnowledgeEncounterVisible).ToArray();
            if (encounters.Length == 0) continue;
            var categoryOpen = ImGui.TreeNodeEx($"{category}  ({encounters.Length})##knowledge-category-{category}", ImGuiTreeNodeFlags.DefaultOpen);
            ImGui.SameLine();
            if (ImGui.Button($"Delete category##delete-category-{category}"))
                RequestPurge(category, $"Delete all {allEncounters.Length} learned content entries under {category}, including items currently hidden by filters?", () => PurgeCategory(category));
            if (!categoryOpen)
                continue;

            foreach (var encounter in encounters)
                DrawKnowledgeEncounter(encounter);
            ImGui.TreePop();
        }
    }

    private void DrawKnowledgeEncounter(EncounterMemory encounter)
    {
        var parentMatchesSearch = KnowledgeEncounterIdentityMatches(encounter);
        var name = EncounterDisplayName(encounter);
        var label = encounter.ContentFinderConditionID != 0
            ? $"{name}  — {encounter.TerritoryName}"
            : name;
        var open = ImGui.TreeNodeEx($"{label}##knowledge-territory-{encounter.TerritoryID}", ImGuiTreeNodeFlags.SpanAvailWidth);
        ImGui.SameLine();
        if (ImGui.Button($"Export JSON##export-territory-{encounter.TerritoryID}"))
        {
            try
            {
                _knowledgeExportPath = ExportEncounterKnowledge(encounter);
                Service.ChatGui.Print($"Foretell content export: {_knowledgeExportPath}");
            }
            catch (Exception e) { Service.ChatGui.PrintError($"Foretell content export failed: {e.Message}"); }
        }
        ImGui.SameLine();
        var bundleRunning = _analysisBundleTask is { IsCompleted: false };
        ImGui.BeginDisabled(bundleRunning);
        if (ImGui.Button($"Analysis ZIP##analysis-territory-{encounter.TerritoryID}"))
        {
            try { StartAnalysisBundleExport(encounter); }
            catch (Exception e) { Service.ChatGui.PrintError($"Foretell analysis bundle failed: {e.Message}"); }
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("After leaving the duty, packages the sealed raw journal, learned content and the complete semantic decision audit into one shareable ZIP.");
        ImGui.SameLine();
        if (ImGui.Button($"Delete##delete-territory-{encounter.TerritoryID}"))
            RequestPurge(name, $"Delete this territory/content, its sources, mechanics, timelines and session history?", () => PurgeEncounter(encounter.TerritoryID));
        if (!open)
            return;

        ImGui.TextDisabled($"Territory {encounter.TerritoryID} | duty {encounter.ContentFinderConditionID} | {encounter.Sessions} sessions | {encounter.Pulls} pulls | {encounter.Mechanics.Count} mechanics");
        if (!string.IsNullOrWhiteSpace(_knowledgeExportPath))
            ImGui.TextDisabled($"Latest focused export: {_knowledgeExportPath}");
        if (!string.IsNullOrWhiteSpace(_analysisBundleStatus))
            ImGui.TextDisabled($"Analysis bundle: {_analysisBundleStatus}");
        if (!string.IsNullOrWhiteSpace(_analysisBundlePath))
            ImGui.TextDisabled($"Latest analysis ZIP: {_analysisBundlePath}");

        if (encounter.ArenaBoundaries.Count > 0 && ImGui.TreeNodeEx($"Observed room / arena boundaries  ({encounter.ArenaBoundaries.Count} states)##boundaries-{encounter.TerritoryID}", ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            foreach (var boundary in encounter.ArenaBoundaries.Values.OrderByDescending(item => item.LastSeen))
            {
                ImGui.BulletText($"{boundary.Fingerprint[..Math.Min(8, boundary.Fingerprint.Length)]} · {boundary.Hits}/{boundary.Rays} wall rays · area {boundary.Area:F0} · {(boundary.ArenaLike ? "arena candidate" : "room/corridor")} · seen {boundary.Observations}x");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Delete##delete-boundary-{encounter.TerritoryID}-{boundary.Fingerprint}"))
                    RequestPurge("Observed arena boundary", "Delete this learned collision boundary? It can be scanned again from the live scene.", () => PurgeArenaBoundary(encounter.TerritoryID, boundary.Fingerprint));
            }
            ImGui.TreePop();
        }

        if (encounter.Topologies.Count > 0 && ImGui.TreeNodeEx($"Arena topology  ({encounter.Topologies.Count} states)##topologies-{encounter.TerritoryID}", ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            foreach (var topology in encounter.Topologies.Values.OrderByDescending(t => t.LastSeen))
            {
                ImGui.BulletText($"{topology.Fingerprint[..Math.Min(8, topology.Fingerprint.Length)]} · {topology.PassableCells:N0} reachable cells · {topology.Contours.Count} contours · seen {topology.Observations}x");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Delete##delete-topology-{encounter.TerritoryID}-{topology.Fingerprint}"))
                    RequestPurge("Arena topology", "Delete this learned collision state? It can be scanned again during the next pull.", () => PurgeTopology(encounter.TerritoryID, topology.Fingerprint));
            }
            ImGui.TreePop();
        }

        var environment = encounter.Mechanics.Values.Where(mechanic => mechanic.SourceKind == SourceKind.Environment && (parentMatchesSearch || KnowledgeMechanicVisible(mechanic))).ToArray();
        if (environment.Length != 0)
            DrawEnvironmentKnowledge(encounter, environment);

        var mechanicsBySource = encounter.Mechanics.Values
            .Where(mechanic => parentMatchesSearch || KnowledgeMechanicVisible(mechanic))
            .GroupBy(mechanic => mechanic.SourceOID)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(mechanic => mechanic.GuidanceConfidence).ToArray());
        var visibleMechanicSourceIDs = mechanicsBySource.Keys.ToHashSet();
        bool DirectSourceMatch(SourceMemory source) => string.IsNullOrWhiteSpace(_knowledgeFilter)
            || ContainsFilter(SourceDisplayName(source)) || ContainsFilter($"{source.OID:X}") || visibleMechanicSourceIDs.Contains(source.OID);
        var visibleSources = encounter.Sources.Values
            .Where(source => source.OID != 0 && (parentMatchesSearch || DirectSourceMatch(source))
                && (_knowledgeConfidenceFilter == 0 || mechanicsBySource.ContainsKey(source.OID)))
            .ToArray();
        DrawGroup("Boss arenas — bosses & adds", "arena-sources", visibleSources.Where(source => source.Kind == SourceKind.Enemy && source.ArenaContextObservations > 0));
        DrawGroup("Trash & open-world mobs", "normal-enemies", visibleSources.Where(source => source.Kind == SourceKind.Enemy && source.ArenaContextObservations == 0));
        DrawGroup("Dungeon / encounter objects", "encounter-objects", visibleSources.Where(source => source.Kind == SourceKind.EventObject));
        DrawGroup("Other observed sources", "other-sources", visibleSources.Where(source => source.Kind is not SourceKind.Enemy and not SourceKind.EventObject and not SourceKind.Player and not SourceKind.Pet));
        DrawGroup("Party, allies & pets", "party-sources", visibleSources.Where(source => source.Kind is SourceKind.Player or SourceKind.Pet));
        ImGui.TreePop();

        void DrawGroup(string groupLabel, string id, IEnumerable<SourceMemory> candidates)
        {
            var sources = candidates
                .OrderByDescending(source => source.BossCandidateObservations)
                .ThenByDescending(source => mechanicsBySource.GetValueOrDefault(source.OID)?.Length ?? 0)
                .ThenByDescending(source => source.Observations)
                .ToArray();
            if (sources.Length == 0 || !ImGui.TreeNodeEx($"{groupLabel}  ({sources.Length})##{id}-{encounter.TerritoryID}", ImGuiTreeNodeFlags.SpanAvailWidth))
                return;
            foreach (var source in sources)
                DrawSourceKnowledge(encounter, source, mechanicsBySource.GetValueOrDefault(source.OID) ?? []);
            ImGui.TreePop();
        }
    }

    private void DrawEnvironmentKnowledge(EncounterMemory encounter, ContextualMechanic[] mechanics)
    {
        var open = ImGui.TreeNodeEx($"Environment  ({mechanics.Length} mechanics)##environment-{encounter.TerritoryID}", ImGuiTreeNodeFlags.SpanAvailWidth);
        ImGui.SameLine();
        if (ImGui.Button($"Delete##delete-environment-{encounter.TerritoryID}"))
            RequestPurge("Environment", "Delete every learned environmental mechanic and its dependent timelines for this content?", () => PurgeSource(encounter.TerritoryID, 0));
        if (!open)
            return;
        foreach (var mechanic in mechanics.OrderByDescending(mechanic => mechanic.GuidanceConfidence))
            DrawMechanicKnowledge(encounter, mechanic);
        ImGui.TreePop();
    }

    private void DrawSourceKnowledge(EncounterMemory encounter, SourceMemory source, ContextualMechanic[] mechanics)
    {
        var name = SourceDisplayName(source);
        var open = ImGui.TreeNodeEx($"{name}  ({mechanics.Length} mechanics)##source-{encounter.TerritoryID}-{source.OID}", ImGuiTreeNodeFlags.SpanAvailWidth);
        ImGui.SameLine();
        if (ImGui.Button($"Delete##delete-source-{encounter.TerritoryID}-{source.OID}"))
            RequestPurge(name, "Delete this source, all mechanics attributed to it and their dependent timelines?", () => PurgeSource(encounter.TerritoryID, source.OID));
        if (!open)
            return;
        var context = source.BossCandidateObservations > 0 ? "boss candidate (observed arena)"
            : source.ArenaContextObservations > 0 ? "arena add/source" : source.Kind.ToString();
        ImGui.TextDisabled($"{context} | OID 0x{source.OID:X8} | HP max {source.MaximumHP:N0} | {source.Observations:N0} observations | {source.Casts:N0} casts | {source.Signals:N0} signals");
        foreach (var mechanic in mechanics)
            DrawMechanicKnowledge(encounter, mechanic);
        ImGui.TreePop();
    }

    private void DrawMechanicKnowledge(EncounterMemory encounter, ContextualMechanic mechanic)
    {
        var name = MechanicDisplayName(mechanic);
        var open = ImGui.TreeNodeEx($"{ConfidenceBadge(mechanic.GuidanceConfidence)} {name}  — {mechanic.Kind}, {mechanic.Geometry}, {mechanic.GuidanceConfidence:P0} verified##mechanic-{encounter.TerritoryID}-{mechanic.Key}", ImGuiTreeNodeFlags.SpanAvailWidth);
        ImGui.SameLine();
        if (ImGui.Button($"Delete##delete-mechanic-{encounter.TerritoryID}-{mechanic.Key}"))
            RequestPurge(name, "Delete this learned mechanic, its samples and dependent timeline edges?", () => PurgeMechanic(encounter.TerritoryID, mechanic.Key));
        if (!open)
            return;

        if (ImGui.BeginTable($"mechanic-summary-{encounter.TerritoryID}-{mechanic.Key}", 6, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
        {
            DrawMetricCell(mechanic.Observations.ToString(), "Observations");
            DrawMetricCell(mechanic.Confirmations.ToString(), "Confirmations");
            DrawMetricCell(mechanic.AmbiguousSamples.ToString(), "Ambiguous");
            DrawMetricCell($"{mechanic.MeanLeadSeconds:F2}s", "Mean lead");
            DrawMetricCell($"{mechanic.Confidence:P0}", "Evidence");
            DrawMetricCell($"{mechanic.ForecastHits}/{mechanic.Forecasts}", "Forecasts verified");
            ImGui.EndTable();
        }
        var mechanicForecasts = encounter.Mechanics.Values.Sum(mechanic => mechanic.Forecasts);
        var mechanicHits = encounter.Mechanics.Values.Sum(mechanic => mechanic.ForecastHits);
        var transitionForecasts = encounter.Timeline.Values.Sum(edge => edge.Forecasts);
        var transitionHits = encounter.Timeline.Values.Sum(edge => edge.Hits);
        if (ImGui.BeginTable("ForetellInferenceReadiness", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
        {
            DrawMetricCell($"{mechanicHits}/{mechanicForecasts}", "Mechanic forecasts verified");
            DrawMetricCell($"{transitionHits}/{transitionForecasts}", "Timeline forecasts verified");
            DrawMetricCell(encounter.CausalEdges.Count.ToString("N0"), "Learned causal links");
            DrawMetricCell(encounter.RawOpcodes.Count.ToString("N0"), "Raw protocol profiles");
            ImGui.EndTable();
        }
        ImGui.TextUnformatted(GeometryDescription(mechanic));
        ImGui.TextDisabled($"Guidance: {ForetellInferenceCore.GuidanceFor(mechanic.Kind)} | calibrated lower bound {mechanic.GuidanceConfidence:P1} | misses {mechanic.ForecastMisses} | Brier {(mechanic.Forecasts == 0 ? 0 : mechanic.BrierScoreSum / mechanic.Forecasts):F3}");
        ImGui.TextDisabled($"Trigger {mechanic.TriggerKind} 0x{mechanic.TriggerID:X} | first {mechanic.FirstSeen:u} | last {mechanic.LastSeen:u}");
        if (mechanic.Evidence.Count > 0)
            ImGui.TextWrapped("Evidence: " + string.Join(" · ", mechanic.Evidence.OrderByDescending(item => item.Value).Select(item => $"{ObservationLabel(item.Key)} ×{item.Value}")));
        ImGui.TreePop();
    }

    private static void DrawInspectorTab(string label, Action draw)
    {
        if (!ImGui.BeginTabItem(label)) return;
        try { draw(); }
        finally { ImGui.EndTabItem(); }
    }

    private bool KnowledgeEncounterVisible(EncounterMemory encounter)
    {
        var confidenceVisible = _knowledgeConfidenceFilter == 0 || encounter.Mechanics.Values.Any(KnowledgeMechanicVisible);
        if (!confidenceVisible) return false;
        if (string.IsNullOrWhiteSpace(_knowledgeFilter)) return true;
        return KnowledgeEncounterIdentityMatches(encounter)
            || encounter.Sources.Values.Any(source => ContainsFilter(SourceDisplayName(source)) || ContainsFilter($"{source.OID:X}"))
            || encounter.Mechanics.Values.Any(mechanic => ContainsFilter(MechanicDisplayName(mechanic)) || ContainsFilter(mechanic.Kind.ToString()) || ContainsFilter(mechanic.Geometry.ToString()));
    }

    private bool KnowledgeEncounterIdentityMatches(EncounterMemory encounter)
        => string.IsNullOrWhiteSpace(_knowledgeFilter) || ContainsFilter(EncounterDisplayName(encounter)) || ContainsFilter(encounter.TerritoryName)
            || ContainsFilter(encounter.ContentCategory) || ContainsFilter(encounter.TerritoryID.ToString());

    private bool KnowledgeMechanicVisible(ContextualMechanic mechanic)
    {
        var threshold = _knowledgeConfidenceFilter switch { 1 => .75f, 2 => .95f, 3 => .99f, _ => 0 };
        if (mechanic.GuidanceConfidence < threshold) return false;
        return string.IsNullOrWhiteSpace(_knowledgeFilter) || ContainsFilter(MechanicDisplayName(mechanic))
            || ContainsFilter(mechanic.Kind.ToString()) || ContainsFilter(mechanic.Geometry.ToString())
            || ContainsFilter(mechanic.TriggerKind.ToString()) || ContainsFilter($"{mechanic.TriggerID:X}");
    }

    private bool ContainsFilter(string? value)
        => value?.Contains(_knowledgeFilter.Trim(), StringComparison.OrdinalIgnoreCase) == true;

    private void RequestPurge(string title, string description, Action purge)
        => RequestConfirmation(title, description, "Delete local data", "This removes local data permanently. Learned items can be rediscovered while learning is enabled.", purge);

    private void RequestConfirmation(string title, string description, string button, string note, Action action)
    {
        _pendingPurgeTitle = title;
        _pendingPurgeDescription = description;
        _pendingConfirmationButton = button;
        _pendingConfirmationNote = note;
        _pendingPurge = action;
        _openPurgeConfirmation = true;
    }

    private void DrawPurgeConfirmation()
    {
        if (_openPurgeConfirmation)
        {
            ImGui.OpenPopup("Confirm Foretell change###ForetellPurgeConfirmation");
            _openPurgeConfirmation = false;
        }
        if (!ImGui.BeginPopup("Confirm Foretell change###ForetellPurgeConfirmation"))
            return;

        ImGui.TextUnformatted(_pendingPurgeTitle);
        ImGui.Separator();
        ImGui.TextWrapped(_pendingPurgeDescription);
        ImGui.TextDisabled(_pendingConfirmationNote);
        if (ImGui.Button(_pendingConfirmationButton))
        {
            try
            {
                _pendingPurge?.Invoke();
                SaveStore();
                _purgeResult = $"Done: {_pendingPurgeTitle}";
            }
            catch (Exception e) { _purgeResult = $"Delete failed safely: {e.Message}"; }
            _pendingPurge = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            _pendingPurge = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawRecommendedNextStep()
    {
        var learned = _store.Encounters.TryGetValue(_territory, out var encounter) ? encounter.Mechanics.Count : 0;
        var high = encounter?.Mechanics.Values.Count(m => m.GuidanceConfidence >= _cfg.WarningConfidence / 100f) ?? 0;
        var recommendation = _cfg.Mode switch
        {
            ForetellMode.Legacy => "Foretell is hidden. Switch to Observe to learn without changing the combat UI.",
            ForetellMode.Observe when learned < 3 => "Stay in Observe: more repeated evidence is needed before comparison is useful.",
            ForetellMode.Observe => $"{learned} candidates learned, including {high} high-confidence. Hybrid is the useful next step.",
            ForetellMode.Hybrid when high < 3 => "Keep Hybrid enabled: BMR remains complete while you review Foretell's learned mechanics.",
            ForetellMode.Hybrid => $"Combined validation mode: BMR and Foretell are both active; {high} Foretell candidates are high-confidence.",
            ForetellMode.Foretell => "Pure Foretell is active. Review ambiguous mechanics after the run.",
            _ => ""
        };
        ImGui.TextUnformatted("Recommended next step");
        ImGui.SameLine();
        ImGui.TextWrapped(recommendation);
    }

    private static string ConfidenceBadge(float confidence)
        => confidence >= .99f ? "[SAFE]" : confidence >= .95f ? "[HIGH]" : confidence >= .75f ? "[LEARNED]" : "[LEARNING]";

    private static string GeometryDescription(ContextualMechanic mechanic) => mechanic.Geometry switch
    {
        GeometryKind.Circle => $"radius {mechanic.P1:F1} yalms",
        GeometryKind.Donut => $"inner {mechanic.P1:F1} / outer {mechanic.P2:F1} yalms",
        GeometryKind.Cone => $"range {mechanic.P1:F1} yalms / half-angle {mechanic.P2 * 180 / MathF.PI:F1} degrees",
        GeometryKind.Rectangle => $"length {mechanic.P1:F1} yalms / half-width {mechanic.P2:F1} yalms",
        GeometryKind.Cross => $"four arms {mechanic.P1:F1} yalms / half-width {mechanic.P2:F1} yalms",
        _ when mechanic.PriorGeometry == GeometryKind.Cone && mechanic.PriorP1 > 0 && mechanic.PriorP2 <= 0
            => $"cone family / range {mechanic.PriorP1:F1} yalms; angle still learning from outcomes",
        _ => "geometry not confidently identified yet"
    };

    private void DrawInspectorTimeline()
    {
        if (!_store.Encounters.TryGetValue(_inspectorTerritory, out var encounter))
        {
            ImGui.TextUnformatted("No timeline data yet.");
            return;
        }

        ImGui.TextUnformatted($"{encounter.Phases.Count} phases  |  {encounter.Timeline.Count} transitions  |  {encounter.TriggerContexts.Count} time/HP anchors  |  {encounter.Composites.Count} simultaneous patterns");
        ImGui.TextDisabled("Names come from the game's own data sheets; IDs remain visible only when no name exists.");
        ImGui.TextDisabled("Ignore suppresses a recurring signal for this territory while keeping raw capture and diagnostics intact.");
        if (ImGui.Button("Export signal filters"))
        {
            try { _purgeResult = $"Exported: {ExportSignalFilters()}"; }
            catch (Exception e) { _purgeResult = $"Filter export failed safely: {e.Message}"; }
        }
        ImGui.SameLine();
        if (ImGui.Button("Import / merge signal filters"))
        {
            try { _purgeResult = $"Imported {ImportSignalFilters():N0} signal exclusions from {_signalFilterPath}"; SaveStore(); }
            catch (Exception e) { _purgeResult = $"Filter import failed safely: {e.Message}"; }
        }
        if (!string.IsNullOrWhiteSpace(_purgeResult)) ImGui.TextDisabled(_purgeResult);
        if (encounter.ExcludedSignals.Count > 0 && ImGui.TreeNode($"Ignored signals ({encounter.ExcludedSignals.Count})"))
        {
            foreach (var exclusion in encounter.ExcludedSignals.Values.OrderBy(item => item.Label).Take(4096).ToArray())
            {
                ImGui.BulletText($"{exclusion.Label} · {exclusion.Signal}");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Restore##restore-signal-{encounter.TerritoryID}-{exclusion.Signal}"))
                {
                    RestoreSignal(encounter.TerritoryID, exclusion.Signal);
                    SaveStore();
                }
            }
            ImGui.TreePop();
        }
        if (encounter.PhaseBoundaries.Count > 0 && ImGui.TreeNode($"Phase-boundary evidence ({encounter.PhaseBoundaries.Count})"))
        {
            foreach (var pair in encounter.PhaseBoundaries.OrderByDescending(item => item.Value.Accepted).ThenByDescending(item => item.Value.PullsSeen))
            {
                var boundary = pair.Value;
                ImGui.BulletText($"{(boundary.Accepted ? "Accepted" : "Learning")} · {ObservationLabel(boundary.EvidenceKind)} · repeated in {boundary.PullsSeen} pull(s) · {boundary.Signature}");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Delete##delete-boundary-{pair.Key}"))
                    RequestPurge("Phase-boundary evidence", "Delete this learned structural-change candidate?", () => PurgePhaseBoundary(encounter.TerritoryID, pair.Key));
            }
            ImGui.TreePop();
        }
        foreach (var phase in encounter.Phases.Values.OrderBy(item => item.Phase))
        {
            var phaseLabel = TimelinePhaseLabel(phase.Phase);
            var open = ImGui.TreeNodeEx($"{phaseLabel} · {phase.Signals.Count} signals · {phase.Seen:N0} observations##phase-{encounter.TerritoryID}-{phase.Phase}", ImGuiTreeNodeFlags.SpanAvailWidth);
            ImGui.SameLine();
            if (ImGui.SmallButton($"Delete phase##delete-phase-{encounter.TerritoryID}-{phase.Phase}"))
                RequestPurge(phaseLabel, "Delete this learned context, its signal sequence, transitions and simultaneous patterns?", () => PurgePhase(encounter.TerritoryID, phase.Phase));
            if (!open) continue;
            foreach (var signal in phase.Signals.OrderByDescending(item => item.Value).Take(80))
            {
                ImGui.BulletText($"{SignalDisplayName(encounter, signal.Key)} · {signal.Value:N0}×");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Delete##delete-phase-signal-{phase.Phase}-{signal.Key}"))
                    RequestPurge("Phase signal", "Delete this signal from the phase and remove dependent transitions/composites?", () => PurgePhaseSignal(encounter.TerritoryID, phase.Phase, signal.Key));
                ImGui.SameLine();
                if (ImGui.SmallButton($"Ignore##ignore-phase-signal-{phase.Phase}-{signal.Key}"))
                {
                    var label = SignalDisplayName(encounter, signal.Key);
                    RequestConfirmation("Ignore signal", $"Suppress {label} from predictive learning and overlays in this territory?", "Ignore signal",
                        "The exclusion persists across sessions, remains visible above, and can be exported or restored.", () => IgnoreSignal(encounter.TerritoryID, signal.Key, label));
                }
            }
            ImGui.TreePop();
        }

        if (encounter.TriggerContexts.Count > 0 && ImGui.CollapsingHeader($"Phase-clock / boss-HP triggers ({encounter.TriggerContexts.Count})", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextDisabled("Occurrence-specific evidence chooses boss HP only when it is more stable across pulls than elapsed time; otherwise the phase clock wins.");
            if (ImGui.BeginTable("ForetellTriggerContexts", 9, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Phase");
                ImGui.TableSetupColumn("Mechanic");
                ImGui.TableSetupColumn("Occurrence");
                ImGui.TableSetupColumn("Phase time");
                ImGui.TableSetupColumn("Boss HP");
                ImGui.TableSetupColumn("Chosen basis");
                ImGui.TableSetupColumn("Samples");
                ImGui.TableSetupColumn("Forecasts");
                ImGui.TableSetupColumn("Manage");
                ImGui.TableHeadersRow();
                foreach (var pair in encounter.TriggerContexts
                    .OrderByDescending(item => Math.Max(item.Value.TimeStability, item.Value.HealthStability))
                    .ThenByDescending(item => Math.Max(item.Value.Samples, item.Value.HealthSamples)).Take(180))
                {
                    var trigger = pair.Value;
                    var healthBasis = trigger.PreferHealth;
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(TimelinePhaseLabel(trigger.Phase));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(SignalDisplayName(encounter, trigger.Signal));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"#{trigger.Occurrence}");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"T+{trigger.MeanPhaseSeconds:F1}s +/- {trigger.PhaseSecondsStdDev:F1}s ({trigger.TimeStability:P0})");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(trigger.HealthSamples == 0 ? "—" : $"{trigger.MeanBossHPRatio:P1} +/- {trigger.BossHPRatioStdDev:P1} ({trigger.HealthStability:P0})");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(healthBasis ? "Boss HP" : "Phase clock");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{trigger.Samples} time / {trigger.HealthSamples} HP");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(healthBasis
                        ? trigger.HealthForecasts == 0 ? "not tested" : $"{trigger.HealthHits}/{trigger.HealthForecasts} · lower {trigger.HealthForecastReliability:P0}"
                        : trigger.TimeForecasts == 0 ? "not tested" : $"{trigger.TimeHits}/{trigger.TimeForecasts} · lower {trigger.TimeForecastReliability:P0}");
                    ImGui.TableNextColumn();
                    if (ImGui.SmallButton($"Delete##delete-trigger-context-{pair.Key}"))
                        RequestPurge("Time/HP trigger", "Delete this occurrence-specific trigger model? It can be relearned from future pulls.", () => PurgeTriggerContext(encounter.TerritoryID, pair.Key));
                }
                ImGui.EndTable();
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Learned transitions");
        var outgoingCounts = encounter.Timeline.Values.GroupBy(edge => (edge.Phase, edge.From)).ToDictionary(group => group.Key, group => group.Sum(edge => edge.Count));
        if (ImGui.BeginTable("ForetellTimeline", 10, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Phase");
            ImGui.TableSetupColumn("From");
            ImGui.TableSetupColumn("To");
            ImGui.TableSetupColumn("Delay");
            ImGui.TableSetupColumn("Deviation");
            ImGui.TableSetupColumn("Seen");
            ImGui.TableSetupColumn("Stability");
            ImGui.TableSetupColumn("Branch chance");
            ImGui.TableSetupColumn("Forecasts");
            ImGui.TableSetupColumn("Manage");
            ImGui.TableHeadersRow();
            foreach (var pair in encounter.Timeline.OrderByDescending(x => x.Value.Count).ThenByDescending(x => x.Value.Stability).Take(180))
            {
                var edge = pair.Value;
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(TimelinePhaseLabel(edge.Phase));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(SignalDisplayName(encounter, edge.From));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(SignalDisplayName(encounter, edge.To));
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{edge.MeanDelay:F2}s");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"+/- {edge.StdDev:F2}s");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(edge.Count.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{edge.Stability:P0}");
                var outgoing = outgoingCounts.GetValueOrDefault((edge.Phase, edge.From));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(outgoing > 0 ? $"{edge.Count / (float)outgoing:P0}" : "—");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(edge.Forecasts == 0 ? "not tested" : $"{edge.Hits}/{edge.Forecasts} · lower {edge.ForecastReliability:P0}");
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"Delete##delete-edge-{pair.Key}"))
                    RequestPurge("Timeline transition", $"Delete {SignalDisplayName(encounter, edge.From)} → {SignalDisplayName(encounter, edge.To)}?", () => PurgeTimelineEdge(encounter.TerritoryID, pair.Key));
            }
            ImGui.EndTable();
        }
        if (encounter.Composites.Count > 0 && ImGui.CollapsingHeader("Simultaneous / composite mechanics", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var pair in encounter.Composites.OrderByDescending(c => c.Value.Count).Take(80))
            {
                var composite = pair.Value;
                ImGui.BulletText($"{TimelinePhaseLabel(composite.Phase)}: {string.Join(" + ", composite.Signals.Select(signal => SignalDisplayName(encounter, signal)))} · {composite.Count}× · skew {composite.MeanSkewSeconds:F2}s ± {composite.StdDev:F2}s · forecasts {composite.Hits}/{composite.Forecasts} ({composite.ForecastReliability:P0} lower)");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Delete##delete-composite-{pair.Key}"))
                    RequestPurge("Simultaneous pattern", "Delete this learned composite pattern? Individual mechanics are kept.", () => PurgeComposite(encounter.TerritoryID, pair.Key));
            }
        }
        if (encounter.CausalEdges.Count > 0 && ImGui.CollapsingHeader($"Learned causal graph ({encounter.CausalEdges.Count})"))
        {
            foreach (var pair in encounter.CausalEdges.OrderByDescending(item => item.Value.Confidence).ThenByDescending(item => item.Value.Count).Take(160))
            {
                var edge = pair.Value;
                ImGui.BulletText($"{SignalDisplayName(encounter, edge.Cause)} -> {edge.Effect} · {edge.MeanDelay:F2}s +/- {edge.StdDev:F2}s · {edge.Count}x · causal {edge.Confidence:P0} · exact {edge.ExactLinks}");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Delete##delete-causal-{pair.Key}"))
                    RequestPurge("Causal association", "Delete this learned cause/effect association? It can be rediscovered from future pulls or Replay Lab.", () => PurgeCausalEdge(encounter.TerritoryID, pair.Key));
            }
            ImGui.TreePop();
        }
        if (encounter.RawOpcodes.Count > 0 && ImGui.CollapsingHeader($"Raw protocol families ({encounter.RawOpcodes.Count})"))
        {
            ImGui.TextDisabled("Opaque packets remain lossless on disk; these bounded profiles expose recurring lengths, byte-field changes and ordering to the learner.");
            foreach (var pair in encounter.RawOpcodes.OrderByDescending(item => item.Value.Packets).Take(160))
            {
                var raw = pair.Value;
                ImGui.BulletText($"0x{raw.OpcodeFamily:X8} · {raw.Packets:N0} packets / {raw.Windows:N0} windows · {raw.MeanLength:F1} +/- {raw.LengthStdDev:F1} bytes · range {raw.MinLength}-{raw.MaxLength} · {raw.StructuralChanges:N0} structural changes");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Delete##delete-raw-opcode-{raw.OpcodeFamily}"))
                    RequestPurge("Raw protocol family", "Delete this learned structural profile? Exact journal records are not deleted.", () => PurgeRawOpcode(encounter.TerritoryID, raw.OpcodeFamily));
            }
            ImGui.TreePop();
        }
    }

    private string SignalDisplayName(EncounterMemory encounter, string signal)
    {
        if (encounter.Mechanics.TryGetValue(signal, out var mechanic))
            return MechanicDisplayName(mechanic);
        var parts = signal.Split(':', 3);
        if (parts.Length != 3 || !uint.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var oid)
            || !Enum.TryParse<ObservationKind>(parts[1], out var kind)
            || !uint.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out var id))
            return signal;
        var source = encounter.Sources.TryGetValue(oid, out var knownSource) ? SourceDisplayName(knownSource) : oid == 0 ? "Environment" : $"Object 0x{oid:X}";
        string eventName = kind.ToString();
        if (id != 0 && kind is ObservationKind.CastStart or ObservationKind.CastFinish or ObservationKind.ActionResolved or ObservationKind.AffectedTarget)
        {
            var action = LookupActionName(id);
            eventName = string.IsNullOrWhiteSpace(action) ? $"{ObservationLabel(kind)} 0x{id:X}" : action;
        }
        else if (id != 0) eventName = $"{ObservationLabel(kind)} 0x{id:X}";
        return $"{source} — {eventName}";
    }

    private static string TimelinePhaseLabel(int phase)
        => phase == ForetellInferenceCore.OutOfCombatHazardPhase ? "Out-of-combat hazards" : $"Phase {phase + 1}";

    private static string ObservationLabel(ObservationKind kind) => kind switch
    {
        ObservationKind.CastStart => "Cast started",
        ObservationKind.CastFinish => "Cast finished",
        ObservationKind.ActionResolved => "Action impact",
        ObservationKind.AffectedTarget => "Affected target",
        ObservationKind.EffectResult => "Effect confirmed",
        ObservationKind.Icon => "Target marker",
        ObservationKind.VFX or ObservationKind.NativeVFXSpawn => "Visual effect",
        ObservationKind.TetherStart => "Tether",
        ObservationKind.StatusGain => "Status gained",
        ObservationKind.MapEffect or ObservationKind.LegacyMapEffect => "Arena change",
        ObservationKind.DirectorUpdate => "Encounter state",
        ObservationKind.TopologySnapshot => "Arena topology",
        _ => kind.ToString()
    };

    private void DrawInspectorObservations()
    {
        var pause = _liveFeedPaused;
        if (ImGui.Checkbox("Pause view", ref pause))
        {
            _liveFeedPaused = pause;
            if (pause) _pausedLiveFeed = _session.Recent.ToList();
            else _pausedLiveFeed.Clear();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        ImGui.Combo("Event type", ref _liveFeedKindFilter, ObservationKindLabels, ObservationKindLabels.Length);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(280);
        ImGui.InputText("Search", ref _liveFeedFilter, 120);
        ImGui.TextDisabled("Readable normalized events are shown here; complete binary payloads remain in Replay & storage.");
        IEnumerable<ForetellObservation> source = _liveFeedPaused ? _pausedLiveFeed : _session.Recent;
        var observations = source.AsEnumerable();
        if (_liveFeedKindFilter > 0)
            observations = observations.Where(observation => (int)observation.Kind == _liveFeedKindFilter - 1);
        if (!string.IsNullOrWhiteSpace(_liveFeedFilter))
            observations = observations.Where(observation => ObservationMatchesFilter(observation, _liveFeedFilter));
        if (ImGui.BeginTable("ForetellLiveFeed", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Time");
            ImGui.TableSetupColumn("Kind");
            ImGui.TableSetupColumn("Source");
            ImGui.TableSetupColumn("ID");
            ImGui.TableSetupColumn("Target");
            ImGui.TableSetupColumn("Detail");
            ImGui.TableHeadersRow();
            foreach (var observation in observations.Reverse().Take(120))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{observation.At:T}.{observation.At.Millisecond:000}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(ObservationLabel(observation.Kind));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(ObservationSourceName(observation));
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{observation.PrimaryID:X}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{observation.TargetID:X}");
                ImGui.TableNextColumn();
                var detail = DisplayText(observation.Detail, 72);
                ImGui.TextUnformatted(detail);
                if (detail != observation.Detail && ImGui.IsItemHovered()) ImGui.SetTooltip(observation.Detail);
            }
            ImGui.EndTable();
        }
    }

    private bool ObservationMatchesFilter(ForetellObservation observation, string filter)
        => ObservationLabel(observation.Kind).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || observation.Detail.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || ObservationSourceName(observation).Contains(filter, StringComparison.OrdinalIgnoreCase)
            || $"{observation.PrimaryID:X}".Contains(filter, StringComparison.OrdinalIgnoreCase);

    private string ObservationSourceName(ForetellObservation observation)
    {
        var live = observation.ActorID == 0 ? null : _ws.Actors.Find(observation.ActorID);
        if (live != null && !string.IsNullOrWhiteSpace(live.Name)) return live.Name;
        if (_store.Encounters.TryGetValue(observation.TerritoryID, out var encounter)
            && encounter.Sources.TryGetValue(observation.ActorOID, out var source)) return SourceDisplayName(source);
        return observation.SourceKind == SourceKind.Environment ? "Environment" : observation.ActorOID == 0 ? observation.SourceKind.ToString() : $"{observation.SourceKind} 0x{observation.ActorOID:X}";
    }

    private static string DisplayText(string value, int maxCharacters)
        => value.Length <= maxCharacters ? value : value[..Math.Max(1, maxCharacters - 1)] + "…";

    private void DrawInspectorReplay()
    {
        PollRawAnalysis();
        ImGui.TextUnformatted("Replay Lab");
        ImGui.TextDisabled("Reprocess a recorded pull through the current learner without changing live knowledge.");
        if (ImGui.BeginTable("ReplayStorageMetrics", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
        {
            DrawMetricCell(_raw.WrittenItems.ToString("N0"), "Raw records written");
            DrawMetricCell($"{_raw.WrittenBytes / (1024.0 * 1024.0):F1} MiB", "Raw payload");
            DrawMetricCell(_raw.PendingItems.ToString("N0"), "Writer backlog");
            DrawMetricCell(_raw.RejectedItems.ToString("N0"), "Rejected");
            ImGui.EndTable();
        }
        ImGui.BeginDisabled(_inPull);
        if (ImGui.Button("Replay latest in sandbox")) ReplayLatest();
        ImGui.EndDisabled();
        if (_inPull && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("Replay is disabled during combat to protect frame time.");
        ImGui.SameLine();
        if (ImGui.Button(_rawAnalysisTask is { IsCompleted: false } ? "Indexing raw journal..." : "Index latest raw journal")) StartRawAnalysis();
        ImGui.SameLine();
        if (ImGui.Button("Export diagnostics snapshot"))
        {
            try { _diagnosticsPath = ExportDiagnostics(); }
            catch (Exception e) { _diagnosticsPath = $"Export failed: {e.Message}"; }
        }
        ImGui.SameLine();
        if (ImGui.Button("Save learned memory now")) SaveStore();
        if (!string.IsNullOrWhiteSpace(_purgeResult)) ImGui.TextDisabled(_purgeResult);

        ImGui.Separator();
        var replay = _lastReplayReport;
        ImGui.TextUnformatted($"Last sandbox run · {(string.IsNullOrEmpty(replay.File) ? "not run yet" : replay.File)}");
        ImGui.TextDisabled(replay.Status);
        if (ImGui.BeginTable("ReplayResultMetrics", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
        {
            DrawMetricCell(replay.Parsed.ToString("N0"), "Events parsed");
            DrawMetricCell(replay.RawRecords.ToString("N0"), "Raw records");
            DrawMetricCell(replay.RediscoveredMechanics.ToString("N0"), "Mechanics rediscovered");
            DrawMetricCell((replay.Rejected + replay.RawErrors).ToString("N0"), "Errors/rejected");
            ImGui.EndTable();
        }
        if (replay.First != default) ImGui.TextUnformatted($"Recorded time range: {replay.First:u} -> {replay.Last:u}");
        if (!string.IsNullOrEmpty(_diagnosticsPath))
            ImGui.TextUnformatted($"Latest diagnostics export: {_diagnosticsPath}");
        if (_lastRawAnalysis is { } raw)
        {
            ImGui.Separator();
            ImGui.TextUnformatted($"RAW INDEX · {Path.GetFileName(raw.Path)} · schema {raw.Schema} · {(raw.Complete ? "COMPLETE" : "DEGRADED")}");
            ImGui.TextUnformatted($"{raw.Records:N0} records · {raw.PayloadBytes / (1024.0 * 1024.0):F1} MiB · {raw.Windows.Count:N0} windows · {raw.Opcodes.Count:N0} opcode families");
            ImGui.TextUnformatted($"Server {raw.ServerPackets:N0} · client {raw.ClientPackets:N0} · ActorControl {raw.ActorControls:N0}");
            if (raw.FirstAt != default) ImGui.TextUnformatted($"{raw.FirstAt:u} → {raw.LastAt:u}");
            foreach (var error in raw.Errors.Take(4)) ImGui.TextColored(new Vector4(1, .35f, .25f, 1), error);
        }

        DrawStorageManager();

        if (ImGui.CollapsingHeader("Recent learning sessions"))
        {
            foreach (var session in _store.Sessions.Where(s => _inspectorTerritory == 0 || s.TerritoryID == _inspectorTerritory).OrderByDescending(s => s.Started).Take(40))
            {
                ImGui.BulletText($"{session.Started:u} · {EncounterName(session.TerritoryID)} · {session.Observations:N0} events · {session.MechanicsFinalized} mechanics");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Delete##delete-session-{session.SessionID}"))
                    RequestPurge("Learning session", "Delete this historical session summary? Learned mechanics remain intact.", () => PurgeSession(session.SessionID));
            }
        }
    }

    private void DrawStorageManager()
    {
        if (!ImGui.CollapsingHeader("Local recordings & storage", ImGuiTreeNodeFlags.DefaultOpen)) return;
        RefreshStorageFiles();
        var bytes = _storageFiles.Sum(file => file.Bytes);
        ImGui.TextUnformatted($"{_storageFiles.Count:N0} files · {FormatBytes(bytes)} total");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh")) { _lastStorageRefresh = default; RefreshStorageFiles(); }
        ImGui.SameLine();
        if (_storageMaintenanceTask != null) ImGui.TextDisabled("Cleanup running...");
        else if (ImGui.SmallButton("Apply retention & quota now"))
            RequestPurge("Recording cleanup",
                $"Delete inactive recordings older than {_cfg.RecordingRetentionDays} days, then oldest files above {_cfg.MaximumRecordingStorageGiB} GiB? Active files and learned memory remain protected.",
                StartStorageMaintenance);
        ImGui.TextDisabled($"Active files are protected. Automatic cleanup is {(_cfg.AutomaticStorageMaintenance ? "ON" : "OFF")} · {_cfg.RecordingRetentionDays} days · {_cfg.MaximumRecordingStorageGiB} GiB.");
        ImGui.TextDisabled("When sharing a .ftraw.gz, choose an inactive file: the active gzip receives its final footer only after rotation or plugin shutdown.");
        if (_lastStorageMaintenanceResult.CompletedAt != default)
        {
            if (string.IsNullOrEmpty(_lastStorageMaintenanceResult.Error))
                ImGui.TextDisabled($"Last cleanup {_lastStorageMaintenanceResult.CompletedAt:u}: {_lastStorageMaintenanceResult.Deleted} deleted · {FormatBytes(_lastStorageMaintenanceResult.BytesBefore - _lastStorageMaintenanceResult.BytesAfter)} freed.");
            else
                ImGui.TextColored(new Vector4(1, .45f, .25f, 1), $"Cleanup failed safely: {_lastStorageMaintenanceResult.Error}");
        }
        if (ImGui.BeginTable("ForetellStorageFiles", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Type"); ImGui.TableSetupColumn("File"); ImGui.TableSetupColumn("Updated"); ImGui.TableSetupColumn("Size"); ImGui.TableSetupColumn("Manage");
            ImGui.TableHeadersRow();
            foreach (var file in _storageFiles.OrderByDescending(file => file.Updated).Take(100))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(file.Active ? $"{file.Kind} · active" : file.Kind);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(Path.GetFileName(file.Path));
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{file.Updated:u}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(FormatBytes(file.Bytes));
                ImGui.TableNextColumn();
                if (file.Active) ImGui.TextDisabled("Protected");
                else if (ImGui.SmallButton($"Delete##delete-file-{file.Path}"))
                    RequestPurge(Path.GetFileName(file.Path), $"Permanently delete this {file.Kind.ToLowerInvariant()} file ({FormatBytes(file.Bytes)})?", () => DeleteStorageFile(file.Path));
            }
            ImGui.EndTable();
        }
        if (ImGui.TreeNode("Current file paths"))
        {
            ImGui.TextWrapped($"Raw: {_rawPath}");
            ImGui.TextWrapped($"Readable replay: {(string.IsNullOrEmpty(_replayPath) ? "disabled" : _replayPath)}");
            ImGui.TreePop();
        }
    }

    private void RefreshStorageFiles()
    {
        if ((DateTime.UtcNow - _lastStorageRefresh).TotalSeconds < 5) return;
        _lastStorageRefresh = DateTime.UtcNow;
        try
        {
            var writerPath = _raw.ActivePath;
            _storageFiles = Directory.EnumerateFiles(_rawDir, "*.ftraw.gz").Take(5000)
                .Select(path => StorageEntry(path, "Raw", string.Equals(path, _rawPath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(path, writerPath, StringComparison.OrdinalIgnoreCase)))
                .Concat(Directory.EnumerateFiles(_replayDir, "*.jsonl").Take(5000).Select(path => StorageEntry(path, "Replay", string.Equals(path, _replayPath, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(file => file.Updated).Take(5000).ToList();
        }
        catch (Exception e) { _purgeResult = $"Storage scan failed safely: {e.Message}"; }
    }

    private static StorageFileEntry StorageEntry(string path, string kind, bool active)
    {
        var info = new FileInfo(path);
        return new(path, kind, info.Exists ? info.Length : 0, info.Exists ? info.LastWriteTimeUtc : default, active);
    }

    private string EncounterName(uint territoryID)
        => _store.Encounters.TryGetValue(territoryID, out var encounter) ? EncounterDisplayName(encounter) : $"Territory {territoryID}";

    private static string FormatBytes(long bytes)
        => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):F2} GiB"
            : bytes >= 1024L * 1024 ? $"{bytes / (1024d * 1024):F1} MiB"
            : bytes >= 1024 ? $"{bytes / 1024d:F1} KiB" : $"{bytes} B";

    private void DrawInspectorHelp()
    {
        if (ImGui.CollapsingHeader("How Foretell works", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.BulletText("Observe raw, semantic and native game evidence.");
            ImGui.BulletText("Correlate signals with effects, movement, statuses and deaths.");
            ImGui.BulletText("Surface guidance only after confidence gates are met.");
            ImGui.BulletText("Never import hand-authored encounter answers.");
        }

        if (ImGui.CollapsingHeader("Modes", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var mode in Enum.GetValues<ForetellMode>())
                ImGui.TextUnformatted($"{mode,-8}  {ModeDescription(mode)}");
        }

        if (ImGui.CollapsingHeader("Confidence and safety"))
        {
            ImGui.BulletText($"Below {_cfg.VisualConfidence:F0}%: learning only.");
            ImGui.BulletText($"{_cfg.VisualConfidence:F0}-{_cfg.WarningConfidence:F0}%: visible hypothesis.");
            ImGui.BulletText($"{_cfg.WarningConfidence:F0}-{_cfg.SafeConfidence:F0}%: warning-grade.");
            ImGui.BulletText($"At least {_cfg.SafeConfidence:F0}%: eligible for safe-position guidance.");
            ImGui.TextDisabled("Cyan -> yellow -> orange -> red encodes confidence, not damage.");
        }

        if (ImGui.CollapsingHeader("Telemetry coverage"))
        {
            ImGui.TextWrapped("Casts, full ActionEffect and EffectResult sequences, statuses, icons, VFX paths, both native tether slots, actors, event objects, timelines, map/director state, environment, camera, IPC, ActorControl and structured Dalamud signals feed the learner.");
            ImGui.TextUnformatted($"{_store.Coverage.Discovered} discovered  |  {_store.Coverage.Ingested} ingested  |  {_store.Coverage.Used} used  |  {_store.Coverage.Excluded} excluded  |  {_store.Coverage.Unaccounted} unaccounted");
            ImGui.TextUnformatted($"{_fabricDeferredTraversals:N0} budget yields  |  {_fabricRejectedGetters:N0} unsafe generic getters rejected");
        }

        if (ImGui.CollapsingHeader("Commands"))
        {
            ImGui.BulletText("/foretell - toggle cockpit");
            ImGui.BulletText("/foretell mode observe|hybrid|foretell|legacy");
            ImGui.BulletText("/foretell learning on|off");
            ImGui.BulletText("/foretell record on|off");
            ImGui.BulletText("/foretell replay | export | save");
            ImGui.BulletText("/bmr - open the separate legacy BMR interface");
        }

        if (ImGui.CollapsingHeader("Files and privacy"))
        {
            ImGui.BulletText("foretell-memory.json: persistent learned memory.");
            ImGui.BulletText("foretell-raw/*.ftraw.gz: always-on compressed lossless IPC and ActorControl journal.");
            ImGui.BulletText("foretell-replays/*.jsonl: optional human-readable Replay Lab stream.");
            ImGui.BulletText("foretell-signal-filters.json: portable per-territory signal exclusions.");
            ImGui.BulletText("No remote API, private player chat or process pointer addresses.");
        }
    }
}
