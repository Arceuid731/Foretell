using Dalamud.Bindings.ImGui;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private uint _inspectorTerritory;
    private string _diagnosticsPath = "";
    private Action? _pendingPurge;
    private string _pendingPurgeTitle = "";
    private string _pendingPurgeDescription = "";
    private bool _openPurgeConfirmation;
    private static readonly string[] RadarShapeLabels = ["Auto (learned topology)", "Circle", "Square"];

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
                    Service.ChatGui.Print("Foretell modes: legacy | observe | compare | hybrid | foretell");
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
        Service.ChatGui.Print("Foretell commands: /foretell, inspect, stats, mode <legacy|observe|compare|hybrid|foretell>, learning <on|off>, record <on|off>, replay, export, save, help");
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
        ForetellMode.Compare => "Shows BMR and Foretell together so you can compare what Foretell inferred.",
        ForetellMode.Hybrid => "Foretell guidance is active while BMR remains available as a safety net.",
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

        _inspectorTerritory = _inspectorTerritory == 0 ? _territory : _inspectorTerritory;
        DrawInspectorHeader();
        if (ImGui.BeginTabBar("ForetellInspectorTabs"))
        {
            if (ImGui.BeginTabItem("Dashboard"))
            {
                DrawDashboard();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Settings"))
            {
                DrawInspectorSettings();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Knowledge explorer"))
            {
                DrawKnowledgeExplorer();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Timeline"))
            {
                DrawInspectorTimeline();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Live feed"))
            {
                DrawInspectorObservations();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Replay & export"))
            {
                DrawInspectorReplay();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Help"))
            {
                DrawInspectorHelp();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        DrawPurgeConfirmation();
        ImGui.End();
    }

    private void DrawInspectorHeader()
    {
        if (ImGui.BeginTable("ForetellHeader", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
        {
            DrawMetricCell(_cfg.EnableLearning ? "LEARNING" : "READ-ONLY", "Engine");
            DrawMetricCell(_territory.ToString(), "Territory");
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
        DrawModeButton(ForetellMode.Compare);
        ImGui.SameLine();
        DrawModeButton(ForetellMode.Hybrid);
        ImGui.SameLine();
        DrawModeButton(ForetellMode.Foretell);

        ImGui.SameLine();
        ImGui.TextUnformatted("  Territory");
        ImGui.SameLine();
        if (ImGui.Button($"Current ({_territory})"))
            _inspectorTerritory = _territory;
        foreach (var id in _store.Encounters.Keys.Where(id => id != _territory).OrderByDescending(id => _store.Encounters[id].LastSeen).Take(4))
        {
            ImGui.SameLine();
            if (ImGui.Button($"{id}##territory{id}"))
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
        var visual = encounter.Mechanics.Values.Count(m => m.Confidence >= visualCut);
        var warnings = encounter.Mechanics.Values.Count(m => m.Confidence >= warningCut);
        var safe = encounter.Mechanics.Values.Count(m => m.Confidence >= safeCut);
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
            foreach (var mechanic in encounter.Mechanics.Values.OrderByDescending(m => m.Confidence).Take(12))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{ConfidenceBadge(mechanic.Confidence)} {mechanic.Confidence:P0}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(mechanic.Kind.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(mechanic.Geometry.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(mechanic.Observations.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{mechanic.SourceOID:X8}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{mechanic.TriggerKind} {mechanic.TriggerID:X}");
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
            ImGui.TextDisabled("All learning and replay data stays local.");
        }

        if (ImGui.CollapsingHeader("Combat presentation", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= ImGui.Checkbox("World-space overlay", ref _cfg.WorldOverlay);
            changed |= ImGui.Checkbox("Text hints", ref _cfg.TextHints);
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
            changed |= ImGui.SliderFloat("Zoom: distance to edge (yalms)", ref _cfg.RadarWorldRadius, 5, 120);
            ImGui.TextDisabled($"Current view: {_cfg.RadarWorldRadius:F0} yalms from the player to each edge; smaller means more zoom.");
            if (_cfg.RadarShape == ForetellRadarShape.Auto)
                ImGui.TextDisabled("Auto currently falls back to a circle until collision topology reaches a confidence threshold.");
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
        var rawBacklogged = _raw.PendingItems > 4096 || _raw.PendingBytes > 16 * 1024 * 1024;
        var nativeBacklogged = _nativeHookPending > 2048;
        var healthy = !_raw.Failed && _raw.RejectedItems == 0 && !rawBacklogged && !nativeBacklogged && _nativeHookFailures == 0 && _typedSnapshotFailures == 0 && _nativeSnapshotFailures == 0 && coverage.Unaccounted == 0;
        ImGui.Separator();
        ImGui.TextUnformatted("Telemetry completeness");
        ImGui.SameLine();
        ImGui.TextUnformatted(healthy ? "DATA COMPLETE — HEALTHY" : "DATA COMPLETE — DEGRADED / AUDIT REQUIRED");
        if (ImGui.BeginTable("ForetellTelemetryStatus", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Surface");
            ImGui.TableSetupColumn("State");
            ImGui.TableSetupColumn("Critical-path policy");
            ImGui.TableHeadersRow();
            DrawTelemetryRow("World state + semantic network events", "ACTIVE", "Processed with bounded typed handlers");
            DrawTelemetryRow("Raw server/client IPC + ActorControl", _raw.Failed ? "FAILED" : rawBacklogged ? "BACKLOG" : "LOSSLESS", $"Background gzip journal: {_raw.PendingItems:N0} queued / {_raw.WrittenItems:N0} written / {_raw.RejectedItems:N0} rejected");
            DrawTelemetryRow("Typed runtime snapshots", _typedSnapshotFailures == 0 && _nativeSnapshotFailures == 0 ? "ACTIVE" : "DEGRADED", $"1 Hz typed {_lastTypedSnapshotMilliseconds:F2} ms (peak {_peakTypedSnapshotMilliseconds:F2}); native {_lastNativeActorMilliseconds:F2} ms (peak {_peakNativeActorMilliseconds:F2}); {_typedSnapshotFailures + _nativeSnapshotFailures:N0} rejects");
            DrawTelemetryRow("Generic live reflection", "REPLACED", "Typed roots + WorldState deltas; no unmanaged getters on frame thread");
            DrawTelemetryRow("Native ObjectEffect + VFX lifecycle", _nativeHookFailures == 0 ? nativeBacklogged ? "BACKLOG" : "ACTIVE" : "DEGRADED", $"Primitive queue: {_nativeHookPending:N0} queued / {_nativeHookProcessed:N0} processed / {_nativeHookFailures:N0} rejected; drain {_lastNativeHookDrainMilliseconds:F2} ms (peak {_peakNativeHookDrainMilliseconds:F2})");
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
        ImGui.TextUnformatted("Learned knowledge is grouped by game content, territory, source and mechanic.");
        ImGui.TextDisabled("Deleting an item also removes its dependent timelines and orphaned global fallback. Learning can rediscover it later.");
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
            var encounters = categoryGroup.ToArray();
            var categoryOpen = ImGui.TreeNodeEx($"{category}  ({encounters.Length})##knowledge-category-{category}", ImGuiTreeNodeFlags.DefaultOpen);
            ImGui.SameLine();
            if (ImGui.Button($"Delete category##delete-category-{category}"))
                RequestPurge(category, $"Delete all {encounters.Length} learned content entries under {category}?", () => PurgeCategory(category));
            if (!categoryOpen)
                continue;

            foreach (var encounter in encounters)
                DrawKnowledgeEncounter(encounter);
            ImGui.TreePop();
        }
    }

    private void DrawKnowledgeEncounter(EncounterMemory encounter)
    {
        var name = EncounterDisplayName(encounter);
        var label = encounter.ContentFinderConditionID != 0
            ? $"{name}  — {encounter.TerritoryName}"
            : name;
        var open = ImGui.TreeNodeEx($"{label}##knowledge-territory-{encounter.TerritoryID}", ImGuiTreeNodeFlags.SpanAvailWidth);
        ImGui.SameLine();
        if (ImGui.Button($"Delete##delete-territory-{encounter.TerritoryID}"))
            RequestPurge(name, $"Delete this territory/content, its sources, mechanics, timelines and session history?", () => PurgeEncounter(encounter.TerritoryID));
        if (!open)
            return;

        ImGui.TextDisabled($"Territory {encounter.TerritoryID} | duty {encounter.ContentFinderConditionID} | {encounter.Sessions} sessions | {encounter.Pulls} pulls | {encounter.Mechanics.Count} mechanics");

        var environment = encounter.Mechanics.Values.Where(mechanic => mechanic.SourceOID == 0 || mechanic.SourceKind == SourceKind.Environment).ToArray();
        if (environment.Length != 0)
            DrawEnvironmentKnowledge(encounter, environment);

        var mechanicSources = encounter.Sources.Values
            .Where(source => source.OID != 0 && encounter.Mechanics.Values.Any(mechanic => mechanic.SourceOID == source.OID))
            .OrderByDescending(source => encounter.Mechanics.Values.Count(mechanic => mechanic.SourceOID == source.OID))
            .ToArray();
        if (mechanicSources.Length != 0 && ImGui.TreeNodeEx($"Bosses & mechanic sources  ({mechanicSources.Length})##mechanic-sources-{encounter.TerritoryID}", ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            foreach (var source in mechanicSources)
                DrawSourceKnowledge(encounter, source);
            ImGui.TreePop();
        }

        var otherSources = encounter.Sources.Values
            .Where(source => source.OID != 0 && !encounter.Mechanics.Values.Any(mechanic => mechanic.SourceOID == source.OID))
            .OrderByDescending(source => source.Observations)
            .ToArray();
        if (otherSources.Length != 0 && ImGui.TreeNodeEx($"Other observed mobs / objects  ({otherSources.Length})##other-sources-{encounter.TerritoryID}", ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            foreach (var source in otherSources)
                DrawSourceKnowledge(encounter, source);
            ImGui.TreePop();
        }
        ImGui.TreePop();
    }

    private void DrawEnvironmentKnowledge(EncounterMemory encounter, ContextualMechanic[] mechanics)
    {
        var open = ImGui.TreeNodeEx($"Environment  ({mechanics.Length} mechanics)##environment-{encounter.TerritoryID}", ImGuiTreeNodeFlags.SpanAvailWidth);
        ImGui.SameLine();
        if (ImGui.Button($"Delete##delete-environment-{encounter.TerritoryID}"))
            RequestPurge("Environment", "Delete every learned environmental mechanic and its dependent timelines for this content?", () => PurgeSource(encounter.TerritoryID, 0));
        if (!open)
            return;
        foreach (var mechanic in mechanics.OrderByDescending(mechanic => mechanic.Confidence))
            DrawMechanicKnowledge(encounter, mechanic);
        ImGui.TreePop();
    }

    private void DrawSourceKnowledge(EncounterMemory encounter, SourceMemory source)
    {
        var mechanics = encounter.Mechanics.Values.Where(mechanic => mechanic.SourceOID == source.OID).OrderByDescending(mechanic => mechanic.Confidence).ToArray();
        var name = SourceDisplayName(source);
        var open = ImGui.TreeNodeEx($"{name}  ({mechanics.Length} mechanics)##source-{encounter.TerritoryID}-{source.OID}", ImGuiTreeNodeFlags.SpanAvailWidth);
        ImGui.SameLine();
        if (ImGui.Button($"Delete##delete-source-{encounter.TerritoryID}-{source.OID}"))
            RequestPurge(name, "Delete this source, all mechanics attributed to it and their dependent timelines?", () => PurgeSource(encounter.TerritoryID, source.OID));
        if (!open)
            return;
        ImGui.TextDisabled($"{source.Kind} | OID 0x{source.OID:X8} | {source.Observations:N0} observations | {source.Casts:N0} casts | {source.Signals:N0} signals");
        foreach (var mechanic in mechanics)
            DrawMechanicKnowledge(encounter, mechanic);
        ImGui.TreePop();
    }

    private void DrawMechanicKnowledge(EncounterMemory encounter, ContextualMechanic mechanic)
    {
        var name = MechanicDisplayName(mechanic);
        var open = ImGui.TreeNodeEx($"{ConfidenceBadge(mechanic.Confidence)} {name}  — {mechanic.Kind}, {mechanic.Geometry}, {mechanic.Confidence:P0}##mechanic-{encounter.TerritoryID}-{mechanic.Key}", ImGuiTreeNodeFlags.SpanAvailWidth);
        ImGui.SameLine();
        if (ImGui.Button($"Delete##delete-mechanic-{encounter.TerritoryID}-{mechanic.Key}"))
            RequestPurge(name, "Delete this learned mechanic, its samples and dependent timeline edges?", () => PurgeMechanic(encounter.TerritoryID, mechanic.Key));
        if (!open)
            return;

        if (ImGui.BeginTable($"mechanic-summary-{encounter.TerritoryID}-{mechanic.Key}", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
        {
            DrawMetricCell(mechanic.Observations.ToString(), "Observations");
            DrawMetricCell(mechanic.Confirmations.ToString(), "Confirmations");
            DrawMetricCell(mechanic.AmbiguousSamples.ToString(), "Ambiguous");
            DrawMetricCell($"{mechanic.MeanLeadSeconds:F2}s", "Mean lead");
            ImGui.EndTable();
        }
        ImGui.TextUnformatted(GeometryDescription(mechanic));
        ImGui.TextDisabled($"Trigger {mechanic.TriggerKind} 0x{mechanic.TriggerID:X} | first {mechanic.FirstSeen:u} | last {mechanic.LastSeen:u}");
        ImGui.TreePop();
    }

    private void RequestPurge(string title, string description, Action purge)
    {
        _pendingPurgeTitle = title;
        _pendingPurgeDescription = description;
        _pendingPurge = purge;
        _openPurgeConfirmation = true;
    }

    private void DrawPurgeConfirmation()
    {
        if (_openPurgeConfirmation)
        {
            ImGui.OpenPopup("Confirm learned-data deletion###ForetellPurgeConfirmation");
            _openPurgeConfirmation = false;
        }
        if (!ImGui.BeginPopup("Confirm learned-data deletion###ForetellPurgeConfirmation"))
            return;

        ImGui.TextUnformatted(_pendingPurgeTitle);
        ImGui.Separator();
        ImGui.TextWrapped(_pendingPurgeDescription);
        ImGui.TextDisabled("This removes learned local data. It does not blacklist the item; active learning may discover it again.");
        if (ImGui.Button("Delete learned data"))
        {
            _pendingPurge?.Invoke();
            SaveStore();
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
        var high = encounter?.Mechanics.Values.Count(m => m.Confidence >= _cfg.WarningConfidence / 100f) ?? 0;
        var recommendation = _cfg.Mode switch
        {
            ForetellMode.Legacy => "Foretell is hidden. Switch to Observe to learn without changing the combat UI.",
            ForetellMode.Observe when learned < 3 => "Stay in Observe: more repeated evidence is needed before comparison is useful.",
            ForetellMode.Observe => $"{learned} candidates learned, including {high} high-confidence. Compare is the useful next step.",
            ForetellMode.Compare when high < 3 => "Keep Compare enabled and review Learned mechanics while evidence accumulates.",
            ForetellMode.Compare => $"{high} high-confidence candidates. If the overlay matches the fight, Hybrid is ready to test.",
            ForetellMode.Hybrid => "Validation mode: Foretell guides while BMR remains visible as a reference.",
            ForetellMode.Foretell => "Pure Foretell is active. Review ambiguous mechanics after the run.",
            _ => ""
        };
        ImGui.TextUnformatted("Recommended next step");
        ImGui.SameLine();
        ImGui.TextWrapped(recommendation);
    }

    private static string ConfidenceBadge(float confidence)
        => confidence >= .99f ? "[SAFE]" : confidence >= .95f ? "[HIGH]" : confidence >= .75f ? "[LEARNED]" : "[LEARNING]";

    private void DrawInspectorMechanics()
    {
        if (!_store.Encounters.TryGetValue(_inspectorTerritory, out var encounter) || encounter.Mechanics.Count == 0)
        {
            ImGui.TextUnformatted("No mechanics learned for this territory yet. Use Observe mode and run the content first.");
            return;
        }

        ImGui.TextUnformatted("HOW TO READ THIS: confidence combines fit quality, repetition, agreement and ambiguity. A mechanic can be observed many times and still remain low-confidence if the evidence conflicts.");
        ImGui.TextUnformatted($"Display threshold {_cfg.VisualConfidence:F0}% | warning threshold {_cfg.WarningConfidence:F0}% | safe-guidance threshold {_cfg.SafeConfidence:F0}% (Never Guess Lethal)");
        ImGui.Separator();
        foreach (var mechanic in encounter.Mechanics.Values.OrderByDescending(m => m.Confidence).ThenByDescending(m => m.LastSeen).Take(250))
        {
            var title = $"{ConfidenceBadge(mechanic.Confidence)} {mechanic.Kind} / {mechanic.Geometry} - {mechanic.Confidence:P0} - source {mechanic.SourceOID:X} - {mechanic.TriggerKind} {mechanic.TriggerID:X}##{mechanic.Key}";
            if (!ImGui.CollapsingHeader(title)) continue;
            ImGui.TextUnformatted($"What Foretell thinks: {mechanic.Kind} using {mechanic.Geometry} geometry ({GeometryDescription(mechanic)}).");
            ImGui.TextUnformatted($"Confidence: {mechanic.Confidence:P1} | fit/evidence score: {mechanic.Score:P1}");
            ImGui.TextUnformatted($"Evidence history: {mechanic.Observations} observations | {mechanic.Confirmations} confirmations | {mechanic.AmbiguousSamples} conflicting/ambiguous samples");
            ImGui.TextUnformatted($"Outcome evidence: affected={mechanic.AffectedSamples} | status={mechanic.StatusSamples} | movement={mechanic.MovementSamples} | deaths={mechanic.DeathSamples}");
            ImGui.TextUnformatted($"Average warning lead/cast: {mechanic.MeanLeadSeconds:F2}s | retained geometry samples: {mechanic.Samples?.Count ?? 0}");
            ImGui.TextUnformatted($"Source: OID {mechanic.SourceOID:X8} ({mechanic.SourceKind}) | trigger: {mechanic.TriggerKind} ID {mechanic.TriggerID:X}");
            if (mechanic.PriorConfidence > 0 || mechanic.PriorCastType != 0)
            {
                ImGui.TextUnformatted($"Client-data prior: {mechanic.PriorGeometry} {mechanic.PriorConfidence:P0} | CastType={mechanic.PriorCastType} | EffectRange={mechanic.PriorEffectRange} | XAxis={mechanic.PriorXAxisModifier} | TargetArea={mechanic.PriorTargetArea}");
                ImGui.TextUnformatted($"Omen: {mechanic.PriorOmenID}:{mechanic.PriorOmen}");
                ImGui.TextWrapped($"Prior rationale: {mechanic.PriorEvidence}");
            }
            ImGui.TextUnformatted($"First seen: {mechanic.FirstSeen:u} | last seen: {mechanic.LastSeen:u}");
            ImGui.TextUnformatted("Signals used: " + (mechanic.Evidence.Count == 0 ? "none" : string.Join(", ", mechanic.Evidence.OrderByDescending(e => e.Value).Select(e => $"{e.Key} x{e.Value}"))));
            ImGui.TextUnformatted($"Internal key: {mechanic.Key}");
        }
    }

    private static string GeometryDescription(ContextualMechanic mechanic) => mechanic.Geometry switch
    {
        GeometryKind.Circle => $"radius {mechanic.P1:F1} yalms",
        GeometryKind.Donut => $"inner {mechanic.P1:F1} / outer {mechanic.P2:F1} yalms",
        GeometryKind.Cone => $"range {mechanic.P1:F1} yalms / half-angle {mechanic.P2 * 180 / MathF.PI:F1} degrees",
        GeometryKind.Rectangle => $"length {mechanic.P1:F1} yalms / half-width {mechanic.P2:F1} yalms",
        GeometryKind.Cross => $"four arms {mechanic.P1:F1} yalms / half-width {mechanic.P2:F1} yalms",
        _ => "geometry not confidently identified yet"
    };

    private void DrawInspectorSources()
    {
        if (!_store.Encounters.TryGetValue(_inspectorTerritory, out var encounter) || encounter.Sources.Count == 0)
        {
            ImGui.TextUnformatted("No mobs or event sources observed yet.");
            return;
        }

        if (ImGui.BeginTable("ForetellSources", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("OID");
            ImGui.TableSetupColumn("Kind");
            ImGui.TableSetupColumn("Observations");
            ImGui.TableSetupColumn("Casts");
            ImGui.TableSetupColumn("Signals");
            ImGui.TableSetupColumn("Mechanics");
            ImGui.TableSetupColumn("Deaths");
            ImGui.TableHeadersRow();
            foreach (var source in encounter.Sources.Values.OrderByDescending(x => x.Casts).ThenByDescending(x => x.Signals).Take(250))
            {
                var learned = encounter.Mechanics.Values.Count(m => m.SourceOID == source.OID);
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{source.OID:X8}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(source.Kind.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(source.Observations.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(source.Casts.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(source.Signals.ToString("N0"));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(learned.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(source.Deaths.ToString());
            }
            ImGui.EndTable();
        }
    }

    private void DrawInspectorTimeline()
    {
        if (!_store.Encounters.TryGetValue(_inspectorTerritory, out var encounter))
        {
            ImGui.TextUnformatted("No timeline data yet.");
            return;
        }

        ImGui.TextUnformatted($"{encounter.Phases.Count} phase buckets  |  {encounter.Timeline.Count} learned transitions");
        if (ImGui.BeginTable("ForetellTimeline", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Phase");
            ImGui.TableSetupColumn("From");
            ImGui.TableSetupColumn("To");
            ImGui.TableSetupColumn("Delay");
            ImGui.TableSetupColumn("Deviation");
            ImGui.TableSetupColumn("Seen");
            ImGui.TableSetupColumn("Stability");
            ImGui.TableHeadersRow();
            foreach (var edge in encounter.Timeline.Values.OrderByDescending(x => x.Count).ThenByDescending(x => x.Stability).Take(180))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted(edge.Phase.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted(edge.From);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(edge.To);
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{edge.MeanDelay:F2}s");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"+/- {edge.StdDev:F2}s");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(edge.Count.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{edge.Stability:P0}");
            }
            ImGui.EndTable();
        }
    }

    private void DrawInspectorObservations()
    {
        ImGui.TextUnformatted("Latest normalized observations (raw binary payloads remain in Replay Lab)");
        if (ImGui.BeginTable("ForetellLiveFeed", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Time");
            ImGui.TableSetupColumn("Kind");
            ImGui.TableSetupColumn("Source");
            ImGui.TableSetupColumn("ID");
            ImGui.TableSetupColumn("Target");
            ImGui.TableSetupColumn("Detail");
            ImGui.TableHeadersRow();
            foreach (var observation in _session.Recent.Reverse().Take(80))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{observation.At:T}.{observation.At.Millisecond:000}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(observation.Kind.ToString());
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{observation.ActorOID:X8}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{observation.PrimaryID:X}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{observation.TargetID:X}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(observation.Detail);
            }
            ImGui.EndTable();
        }
    }

    private void DrawInspectorReplay()
    {
        ImGui.TextUnformatted("REPLAY LAB");
        ImGui.TextUnformatted("Foretell records its normalized encounter observations, then can feed them back through the same learner in an isolated sandbox. It is not a video replay and it never mutates your live memory.");
        ImGui.TextUnformatted("This lets us change the inference algorithm and retest the exact same pull without returning to the boss.");
        ImGui.TextUnformatted($"Current recording: {(string.IsNullOrEmpty(_replayPath) ? "none" : _replayPath)}");
        ImGui.TextWrapped($"Lossless raw journal: {_rawPath} | {_raw.PendingItems:N0} queued records | {_raw.WrittenItems:N0} written | {_raw.WrittenBytes / (1024.0 * 1024.0):F1} MiB uncompressed payload");
        if (ImGui.Button("Replay latest in sandbox")) ReplayLatest();
        ImGui.SameLine();
        if (ImGui.Button("Export diagnostics snapshot"))
        {
            try { _diagnosticsPath = ExportDiagnostics(); }
            catch (Exception e) { _diagnosticsPath = $"Export failed: {e.Message}"; }
        }
        ImGui.SameLine();
        if (ImGui.Button("Save learned memory now")) SaveStore();

        ImGui.Separator();
        var replay = _lastReplayReport;
        ImGui.TextUnformatted($"Last Replay Lab run: {(string.IsNullOrEmpty(replay.File) ? "not run yet" : replay.File)}");
        ImGui.TextUnformatted($"Status: {replay.Status}");
        ImGui.TextUnformatted($"Input: {replay.Lines} lines | {replay.Parsed} parsed | {replay.Rejected} rejected | {replay.Territories} territories");
        ImGui.TextUnformatted($"Sandbox result: {replay.RediscoveredMechanics} mechanics rediscovered | {replay.AmbiguousMechanics} ambiguous evidence cases");
        if (replay.First != default) ImGui.TextUnformatted($"Recorded time range: {replay.First:u} -> {replay.Last:u}");
        if (replay.Counts.Count > 0)
            ImGui.TextUnformatted("Observation mix: " + string.Join(", ", replay.Counts.OrderByDescending(kv => kv.Value).Take(16).Select(kv => $"{kv.Key}={kv.Value}")));
        if (!string.IsNullOrEmpty(_diagnosticsPath))
            ImGui.TextUnformatted($"Latest diagnostics export: {_diagnosticsPath}");

        ImGui.Separator();
        ImGui.TextUnformatted("RECENT SESSIONS");
        foreach (var session in _store.Sessions.Where(s => _inspectorTerritory == 0 || s.TerritoryID == _inspectorTerritory).OrderByDescending(s => s.Started).Take(24))
            ImGui.TextUnformatted($"{session.Started:u} | territory {session.TerritoryID} | pulls {session.Pulls} | obs {session.Observations} | mechanics {session.MechanicsFinalized} | new {session.NewMechanics} | ambiguous {session.AmbiguousMechanics} | {session.ReplayFile}");
    }

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
            ImGui.BulletText("/foretell mode observe|compare|hybrid|foretell|legacy");
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
            ImGui.BulletText("No remote API, private player chat or process pointer addresses.");
        }
    }
}
