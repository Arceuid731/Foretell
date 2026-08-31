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
            default:
                return false;
        }
    }

    private void DrawInspector()
    {
        if (!_inspectorOpen) return;
        ImGui.SetNextWindowSize(new Vector2(860, 680), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Foretell Inspector###ForetellInspector", ref _inspectorOpen))
        {
            ImGui.End();
            return;
        }

        _inspectorTerritory = _inspectorTerritory == 0 ? _territory : _inspectorTerritory;
        DrawInspectorHeader();
        if (ImGui.BeginTabBar("ForetellInspectorTabs"))
        {
            if (ImGui.BeginTabItem("Overview"))
            {
                DrawInspectorOverview();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Mechanics"))
            {
                DrawInspectorMechanics();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Sources"))
            {
                DrawInspectorSources();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Timeline"))
            {
                DrawInspectorTimeline();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Observations"))
            {
                DrawInspectorObservations();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Replay Lab"))
            {
                DrawInspectorReplay();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        ImGui.End();
    }

    private void DrawInspectorHeader()
    {
        ImGui.TextUnformatted($"Territory {_territory} | session {_session.ID} | mode {_cfg.Mode} | learning {(_cfg.EnableLearning ? "ON" : "OFF")}");
        ImGui.TextUnformatted($"Live: {_session.Observations:N0} observations, {_session.MechanicsFinalized} finalized mechanics, {_session.AmbiguousMechanics} ambiguous, {_episodes.Values.Count(e => !e.Finalized)} active candidates");
        ImGui.TextUnformatted($"ML updates: {_store.ML.Updates:N0} | predictions: {_predictions.Count} | replay recording: {(_replay != null ? "ON" : "OFF")}");
        ImGui.TextUnformatted($"Last evidence: {_lastEvidence}");
        ImGui.Separator();

        ImGui.TextUnformatted("Inspect territory:");
        ImGui.SameLine();
        if (ImGui.Button($"Current ({_territory})")) _inspectorTerritory = _territory;
        foreach (var id in _store.Encounters.Keys.Where(id => id != _territory).OrderByDescending(id => _store.Encounters[id].LastSeen).Take(8))
        {
            ImGui.SameLine();
            if (ImGui.Button($"{id}##territory{id}")) _inspectorTerritory = id;
        }
    }

    private void DrawInspectorOverview()
    {
        if (!_store.Encounters.TryGetValue(_inspectorTerritory, out var encounter))
        {
            ImGui.TextUnformatted("No learned data for this territory yet.");
            return;
        }

        ImGui.TextUnformatted($"Territory {_inspectorTerritory}: {encounter.Sessions} sessions, {encounter.Pulls} pull heuristics, {encounter.Sources.Count} sources, {encounter.Mechanics.Count} learned candidates");
        ImGui.TextUnformatted($"Seen: {encounter.FirstSeen:u} -> {encounter.LastSeen:u}");
        ImGui.Separator();
        ImGui.TextUnformatted("Observation mix");
        foreach (var item in encounter.ObservationCounts.OrderByDescending(kv => kv.Value).Take(24))
            ImGui.TextUnformatted($"  {item.Key,-24} {item.Value,8:N0}");

        ImGui.Separator();
        ImGui.TextUnformatted("Highest-confidence learned mechanics");
        foreach (var mechanic in encounter.Mechanics.Values.OrderByDescending(m => m.Confidence).Take(12))
            ImGui.TextUnformatted($"  {mechanic.Key,-34} {mechanic.Kind,-14} {mechanic.Geometry,-10} {mechanic.Confidence,6:P0}  n={mechanic.Observations} amb={mechanic.AmbiguousSamples}");
    }

    private void DrawInspectorMechanics()
    {
        if (!_store.Encounters.TryGetValue(_inspectorTerritory, out var encounter) || encounter.Mechanics.Count == 0)
        {
            ImGui.TextUnformatted("No mechanics learned yet.");
            return;
        }

        ImGui.TextUnformatted("Confidence intentionally includes repetition, agreement and ambiguity penalties. Low confidence is learning-only.");
        ImGui.Separator();
        foreach (var mechanic in encounter.Mechanics.Values.OrderByDescending(m => m.LastSeen).Take(200))
        {
            var title = $"{mechanic.Kind} / {mechanic.Geometry} - {mechanic.Confidence:P0} - OID {mechanic.SourceOID:X} - {mechanic.TriggerKind} {mechanic.TriggerID:X}##{mechanic.Key}";
            if (!ImGui.CollapsingHeader(title)) continue;
            ImGui.TextUnformatted($"Key: {mechanic.Key}");
            ImGui.TextUnformatted($"Observations: {mechanic.Observations} | confirmations: {mechanic.Confirmations} | ambiguous: {mechanic.AmbiguousSamples} | fit score: {mechanic.Score:P1}");
            ImGui.TextUnformatted($"Affected samples: {mechanic.AffectedSamples} | statuses: {mechanic.StatusSamples} | movement: {mechanic.MovementSamples} | deaths: {mechanic.DeathSamples}");
            ImGui.TextUnformatted($"Lead/cast mean: {mechanic.MeanLeadSeconds:F2}s | geometry samples retained: {mechanic.Samples?.Count ?? 0}");
            ImGui.TextUnformatted($"Geometry params: {GeometryDescription(mechanic)}");
            ImGui.TextUnformatted($"First/last: {mechanic.FirstSeen:u} / {mechanic.LastSeen:u}");
            ImGui.TextUnformatted("Evidence: " + (mechanic.Evidence.Count == 0 ? "none" : string.Join(", ", mechanic.Evidence.OrderByDescending(e => e.Value).Select(e => $"{e.Key}={e.Value}"))));
        }
    }

    private static string GeometryDescription(ContextualMechanic mechanic) => mechanic.Geometry switch
    {
        GeometryKind.Circle => $"radius {mechanic.P1:F1}y",
        GeometryKind.Donut => $"inner {mechanic.P1:F1}y / outer {mechanic.P2:F1}y",
        GeometryKind.Cone => $"range {mechanic.P1:F1}y / half-angle {mechanic.P2 * 180 / MathF.PI:F1}°",
        GeometryKind.Rectangle => $"length {mechanic.P1:F1}y / half-width {mechanic.P2:F1}y",
        _ => "unknown"
    };

    private void DrawInspectorSources()
    {
        if (!_store.Encounters.TryGetValue(_inspectorTerritory, out var encounter) || encounter.Sources.Count == 0)
        {
            ImGui.TextUnformatted("No sources observed yet.");
            return;
        }
        foreach (var source in encounter.Sources.Values.OrderByDescending(s => s.Casts).ThenByDescending(s => s.Signals).Take(200))
        {
            var learned = encounter.Mechanics.Values.Count(m => m.SourceOID == source.OID);
            ImGui.TextUnformatted($"OID {source.OID:X8}  {source.Kind,-11} obs={source.Observations,6} casts={source.Casts,4} signals={source.Signals,4} deaths={source.Deaths,3} learned={learned,3} last={source.LastSeen:T}");
        }
    }

    private void DrawInspectorTimeline()
    {
        if (!_store.Encounters.TryGetValue(_inspectorTerritory, out var encounter)) return;
        ImGui.TextUnformatted($"Learned phases: {encounter.Phases.Count} | transitions: {encounter.Timeline.Count}");
        ImGui.Separator();
        foreach (var edge in encounter.Timeline.Values.OrderByDescending(e => e.Count).ThenByDescending(e => e.Stability).Take(150))
            ImGui.TextUnformatted($"P{edge.Phase}  {edge.From} -> {edge.To}   {edge.MeanDelay:F2}s ± {edge.StdDev:F2}s   n={edge.Count} stability={edge.Stability:P0}");
    }

    private void DrawInspectorObservations()
    {
        ImGui.TextUnformatted("Most recent normalized observations (max 100 kept in UI memory):");
        ImGui.Separator();
        foreach (var observation in _session.Recent.Reverse().Take(60))
            ImGui.TextUnformatted($"{observation.At:T}.{observation.At.Millisecond:000}  {observation.Kind,-22} OID={observation.ActorOID:X8} ID={observation.PrimaryID:X} target={observation.TargetID:X} v1={observation.Value1:F2} {observation.Detail}");
    }

    private void DrawInspectorReplay()
    {
        ImGui.TextUnformatted("Replay Lab re-injects the recorded normalized event stream through the same learner in an isolated sandbox. It does not replay FFXIV graphics and never mutates live memory.");
        ImGui.TextUnformatted($"Recording file: {(string.IsNullOrEmpty(_replayPath) ? "none" : _replayPath)}");
        if (ImGui.Button("Replay latest in sandbox"))
            ReplayLatest();
        ImGui.SameLine();
        if (ImGui.Button("Export diagnostics snapshot"))
        {
            try { _diagnosticsPath = ExportDiagnostics(); }
            catch (Exception e) { _diagnosticsPath = $"Export failed: {e.Message}"; }
        }
        ImGui.SameLine();
        if (ImGui.Button("Save memory now")) SaveStore();

        ImGui.Separator();
        var replay = _lastReplayReport;
        ImGui.TextUnformatted($"Replay: {(string.IsNullOrEmpty(replay.File) ? "not run" : replay.File)}");
        ImGui.TextUnformatted($"Status: {replay.Status}");
        ImGui.TextUnformatted($"Lines: {replay.Lines} | parsed: {replay.Parsed} | rejected: {replay.Rejected} | territories: {replay.Territories}");
        ImGui.TextUnformatted($"Rediscovered mechanics: {replay.RediscoveredMechanics} | ambiguous evidence: {replay.AmbiguousMechanics}");
        if (replay.First != default) ImGui.TextUnformatted($"Range: {replay.First:u} -> {replay.Last:u}");
        if (replay.Counts.Count > 0)
            ImGui.TextUnformatted("Replay observations: " + string.Join(", ", replay.Counts.OrderByDescending(kv => kv.Value).Take(16).Select(kv => $"{kv.Key}={kv.Value}")));
        if (!string.IsNullOrEmpty(_diagnosticsPath))
            ImGui.TextUnformatted($"Diagnostics: {_diagnosticsPath}");

        ImGui.Separator();
        ImGui.TextUnformatted("Recent sessions");
        foreach (var session in _store.Sessions.Where(s => _inspectorTerritory == 0 || s.TerritoryID == _inspectorTerritory).OrderByDescending(s => s.Started).Take(20))
            ImGui.TextUnformatted($"{session.Started:u} T{session.TerritoryID} pulls={session.Pulls} obs={session.Observations} mechanics={session.MechanicsFinalized} new={session.NewMechanics} amb={session.AmbiguousMechanics} replay={session.ReplayFile}");
    }
}
