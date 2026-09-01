using Dalamud.Bindings.ImGui;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private uint _inspectorTerritory;
    private string _diagnosticsPath = "";

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
            if (ImGui.BeginTabItem("Learned mechanics"))
            {
                DrawInspectorMechanics();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Sources / mobs"))
            {
                DrawInspectorSources();
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
        ImGui.End();
    }

    private void DrawInspectorHeader()
    {
        ImGui.TextUnformatted($"Foretell is {(_cfg.EnableLearning ? "LEARNING" : "READ-ONLY")} | Territory {_territory} | Session {_session.ID}");
        ImGui.TextUnformatted($"Current mode: {_cfg.Mode} - {ModeDescription(_cfg.Mode)}");
        ImGui.TextUnformatted($"Live session: {_session.Observations:N0} observations | {_session.MechanicsFinalized} mechanic candidates reviewed | {_session.AmbiguousMechanics} ambiguous");
        ImGui.Separator();

        ImGui.TextUnformatted("Mode:");
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

        ImGui.TextUnformatted("Inspect data from:");
        ImGui.SameLine();
        if (ImGui.Button($"Current territory ({_territory})")) _inspectorTerritory = _territory;
        foreach (var id in _store.Encounters.Keys.Where(id => id != _territory).OrderByDescending(id => _store.Encounters[id].LastSeen).Take(6))
        {
            ImGui.SameLine();
            if (ImGui.Button($"{id}##territory{id}")) _inspectorTerritory = id;
        }
        ImGui.Separator();
    }

    private void DrawModeButton(ForetellMode mode)
    {
        var selected = _cfg.Mode == mode;
        if (ImGui.Button($"{(selected ? "[x]" : "[ ]")} {mode}##mode{mode}")) SetMode(mode);
    }

    private void DrawDashboard()
    {
        ImGui.TextUnformatted("QUICK START");
        ImGui.TextUnformatted("1. Start in Observe on a duty you know. Foretell watches silently and records evidence.");
        ImGui.TextUnformatted("2. After a few pulls/runs, switch to Compare. Check whether Foretell agrees with BMR and with what you saw.");
        ImGui.TextUnformatted("3. Use Hybrid once the learned mechanics look sane. Pure Foretell is the final validation mode.");
        ImGui.Separator();

        DrawRecommendedNextStep();
        ImGui.Separator();

        ImGui.TextUnformatted("CORE FEATURES");
        var changed = false;
        changed |= ImGui.Checkbox("Adaptive learning##ftlearn", ref _cfg.EnableLearning);
        ImGui.SameLine();
        changed |= ImGui.Checkbox("Local ML classifier##ftml", ref _cfg.EnableML);
        ImGui.SameLine();
        changed |= ImGui.Checkbox("Record replay stream##ftrecord", ref _cfg.RecordReplay);
        changed |= ImGui.Checkbox("World overlay##ftworld", ref _cfg.WorldOverlay);
        ImGui.SameLine();
        changed |= ImGui.Checkbox("Mini radar##ftradar", ref _cfg.MiniRadar);
        ImGui.SameLine();
        changed |= ImGui.Checkbox("Text hints##fttext", ref _cfg.TextHints);
        changed |= ImGui.Checkbox("Safe-position suggestions##ftsafe", ref _cfg.SafePositionSuggestions);
        if (changed) _cfg.Modified.Fire();

        ImGui.TextUnformatted("Learning OFF = Foretell still observes/displays existing memory, but does not update learned mechanics or the ML model.");
        ImGui.TextUnformatted("Replay recording is local only. Safe-position suggestions never move your character.");
        ImGui.Separator();

        if (!_store.Encounters.TryGetValue(_inspectorTerritory, out var encounter))
        {
            ImGui.TextUnformatted("NO DATA YET");
            ImGui.TextUnformatted("Enter content with learning enabled. Casts, hits, statuses, icons, tethers, VFX, event objects, map effects and movement will appear here automatically.");
            return;
        }

        var visualCut = _cfg.VisualConfidence / 100f;
        var warningCut = _cfg.WarningConfidence / 100f;
        var safeCut = _cfg.SafeConfidence / 100f;
        var visual = encounter.Mechanics.Values.Count(m => m.Confidence >= visualCut);
        var warnings = encounter.Mechanics.Values.Count(m => m.Confidence >= warningCut);
        var safe = encounter.Mechanics.Values.Count(m => m.Confidence >= safeCut);
        var ambiguous = encounter.Mechanics.Values.Count(m => m.AmbiguousSamples > 0);

        ImGui.TextUnformatted($"TERRITORY {_inspectorTerritory} SUMMARY");
        ImGui.TextUnformatted($"{encounter.Sessions} sessions | {encounter.Pulls} detected pulls | {encounter.Sources.Count} observed sources | {encounter.Mechanics.Count} mechanic candidates");
        ImGui.TextUnformatted($"Confidence gates: {visual} visualizable (>= {_cfg.VisualConfidence:F0}%) | {warnings} warning-grade (>= {_cfg.WarningConfidence:F0}%) | {safe} safe-guidance-grade (>= {_cfg.SafeConfidence:F0}%) | {ambiguous} with conflicting evidence");
        ImGui.TextUnformatted("Overlay/radar colors encode reliability: cyan = early/visual -> yellow = learned -> orange = high -> red = safe-guidance-grade. They do NOT encode damage severity.");
        ImGui.TextUnformatted($"ML updates: {_store.ML.Updates:N0} | current predictions: {_predictions.Count} | active candidates awaiting outcome: {_episodes.Values.Count(e => !e.Finalized)}");
        ImGui.TextUnformatted($"Last inference: {_lastEvidence}");
        ImGui.Separator();

        ImGui.TextUnformatted("BEST LEARNED MECHANICS");
        foreach (var mechanic in encounter.Mechanics.Values.OrderByDescending(m => m.Confidence).Take(10))
            ImGui.TextUnformatted($"{ConfidenceBadge(mechanic.Confidence)} {mechanic.Kind,-14} {mechanic.Geometry,-10} {mechanic.Confidence,6:P0} | seen {mechanic.Observations}x | source OID {mechanic.SourceOID:X} | trigger {mechanic.TriggerKind} {mechanic.TriggerID:X}");
    }

    private void DrawRecommendedNextStep()
    {
        var learned = _store.Encounters.TryGetValue(_territory, out var encounter) ? encounter.Mechanics.Count : 0;
        var high = encounter?.Mechanics.Values.Count(m => m.Confidence >= _cfg.WarningConfidence / 100f) ?? 0;
        var recommendation = _cfg.Mode switch
        {
            ForetellMode.Legacy => "Foretell is effectively hidden. Switch to Observe if you want it to start learning without changing your combat UI.",
            ForetellMode.Observe when learned < 3 => "Stay in Observe for now. Foretell needs more repeated evidence before comparison is useful.",
            ForetellMode.Observe => $"You already have {learned} learned candidates ({high} high-confidence). Compare mode is the useful next step.",
            ForetellMode.Compare when high < 3 => "Keep Compare enabled and review the Learned mechanics tab. More evidence is useful before relying on adaptive guidance.",
            ForetellMode.Compare => $"You have {high} high-confidence candidates. If the overlay matches the real mechanics, Hybrid is ready for practical testing.",
            ForetellMode.Hybrid => "Use this as the normal validation mode: Foretell guides you while BMR remains visible as a reference/safety net.",
            ForetellMode.Foretell => "Pure Foretell is active. Use this only after Compare/Hybrid look reliable for the encounter; review ambiguous mechanics after the run.",
            _ => ""
        };
        ImGui.TextUnformatted("RECOMMENDED NEXT STEP");
        ImGui.TextUnformatted(recommendation);
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
        _ => "geometry not confidently identified yet"
    };

    private void DrawInspectorSources()
    {
        if (!_store.Encounters.TryGetValue(_inspectorTerritory, out var encounter) || encounter.Sources.Count == 0)
        {
            ImGui.TextUnformatted("No mobs/event sources observed yet.");
            return;
        }
        ImGui.TextUnformatted("A source is an enemy, add, event object or environment channel Foretell has observed. OID is the stable game object type ID, not the temporary instance ID.");
        ImGui.Separator();
        foreach (var source in encounter.Sources.Values.OrderByDescending(s => s.Casts).ThenByDescending(s => s.Signals).Take(250))
        {
            var learned = encounter.Mechanics.Values.Count(m => m.SourceOID == source.OID);
            ImGui.TextUnformatted($"OID {source.OID:X8} | {source.Kind,-11} | {source.Observations,6} observations | {source.Casts,4} casts | {source.Signals,4} signals | {learned,3} learned mechanics | deaths {source.Deaths}");
        }
    }

    private void DrawInspectorTimeline()
    {
        if (!_store.Encounters.TryGetValue(_inspectorTerritory, out var encounter))
        {
            ImGui.TextUnformatted("No timeline data yet.");
            return;
        }
        ImGui.TextUnformatted("Foretell learns repeated signal order and timing. Stability rises when the same transition repeats with similar timing.");
        ImGui.TextUnformatted($"Learned phase buckets: {encounter.Phases.Count} | learned transitions: {encounter.Timeline.Count}");
        ImGui.Separator();
        foreach (var edge in encounter.Timeline.Values.OrderByDescending(e => e.Count).ThenByDescending(e => e.Stability).Take(180))
            ImGui.TextUnformatted($"Phase {edge.Phase} | {edge.From} -> {edge.To} | delay {edge.MeanDelay:F2}s +/- {edge.StdDev:F2}s | seen {edge.Count}x | stability {edge.Stability:P0}");
    }

    private void DrawInspectorObservations()
    {
        ImGui.TextUnformatted("RAW LIVE FEED - useful when Foretell missed or misclassified something. The last 100 normalized observations are kept in UI memory.");
        ImGui.TextUnformatted("Position samples are intentionally frequent; combat signals such as CastStart, Icon, VFX, Tether, Status, MapEffect and DirectorUpdate are usually the interesting rows.");
        ImGui.Separator();
        foreach (var observation in _session.Recent.Reverse().Take(80))
            ImGui.TextUnformatted($"{observation.At:T}.{observation.At.Millisecond:000} | {observation.Kind,-22} | source OID {observation.ActorOID:X8} | ID {observation.PrimaryID:X} | target {observation.TargetID:X} | v1 {observation.Value1:F2} | {observation.Detail}");
    }

    private void DrawInspectorReplay()
    {
        ImGui.TextUnformatted("REPLAY LAB");
        ImGui.TextUnformatted("Foretell records its normalized encounter observations, then can feed them back through the same learner in an isolated sandbox. It is not a video replay and it never mutates your live memory.");
        ImGui.TextUnformatted("This lets us change the inference algorithm and retest the exact same pull without returning to the boss.");
        ImGui.TextUnformatted($"Current recording: {(string.IsNullOrEmpty(_replayPath) ? "none" : _replayPath)}");
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
        ImGui.TextUnformatted("WHAT IS FORETELL?");
        ImGui.TextUnformatted("Foretell is the adaptive layer added on top of BossMod Reborn. BMR supplies mature FFXIV world-state/rendering infrastructure; Foretell observes encounters, correlates signals and outcomes, learns reusable mechanic candidates, and only surfaces guidance when confidence passes configured gates.");
        ImGui.Separator();

        ImGui.TextUnformatted("MODES");
        foreach (var mode in Enum.GetValues<ForetellMode>())
            ImGui.TextUnformatted($"{mode,-8} - {ModeDescription(mode)}");
        ImGui.Separator();

        ImGui.TextUnformatted("CONFIDENCE / SAFETY");
        ImGui.TextUnformatted($"Below {_cfg.VisualConfidence:F0}%: learning only, normally hidden from combat presentation.");
        ImGui.TextUnformatted($"{_cfg.VisualConfidence:F0}% to {_cfg.WarningConfidence:F0}%: may be visualized as a learned hypothesis, not treated as reliable warning guidance.");
        ImGui.TextUnformatted($"{_cfg.WarningConfidence:F0}% to {_cfg.SafeConfidence:F0}%: high-confidence warning-grade inference.");
        ImGui.TextUnformatted($"At least {_cfg.SafeConfidence:F0}%: eligible for safe-position guidance. This intentionally uses an extremely high Never Guess Lethal threshold.");
        ImGui.Separator();

        ImGui.TextUnformatted("WHAT FORETELL OBSERVES");
        ImGui.TextUnformatted("Casts and hit targets; statuses; icons; VFX; tethers; actor lifecycle/targetability/model state; event objects; action-timeline events; NPC yells; map effects; director updates; party positions and sudden displacement.");
        ImGui.TextUnformatted("Learning is contextual by territory, source OID and trigger, so the same numeric signal does not automatically mean the same mechanic everywhere.");
        ImGui.Separator();

        ImGui.TextUnformatted("COMMANDS");
        ImGui.TextUnformatted("/foretell - toggle this Foretell cockpit");
        ImGui.TextUnformatted("/foretell inspect  (or stats/debug) - open this cockpit");
        ImGui.TextUnformatted("/foretell mode observe|compare|hybrid|foretell|legacy - change presentation mode");
        ImGui.TextUnformatted("/foretell learning on|off - enable/disable mutation of learned memory");
        ImGui.TextUnformatted("/foretell record on|off - enable/disable local normalized replay recording");
        ImGui.TextUnformatted("/foretell replay - re-run the latest Foretell replay through an isolated learner sandbox");
        ImGui.TextUnformatted("/foretell export - write a diagnostics snapshot for review/debugging");
        ImGui.TextUnformatted("/foretell save - force-save learned memory now");
        ImGui.TextUnformatted("/foretell help - print command summary and open this cockpit");
        ImGui.Separator();

        ImGui.TextUnformatted("FILES / PRIVACY");
        ImGui.TextUnformatted("foretell-memory.json contains persistent local learned memory. foretell-replays/*.jsonl contains normalized local event streams. Foretell does not send these to a remote API.");
        ImGui.TextUnformatted("The Replay Lab uses a temporary sandbox store and restores live memory after analysis.");
    }
}
