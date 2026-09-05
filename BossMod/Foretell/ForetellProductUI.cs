using Dalamud.Bindings.ImGui;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private static readonly Vector4 ProductAccent = new(.35f, .8f, .92f, 1);

    private void DrawDashboard()
    {
        var frame = PresentationFrame;
        ImGui.TextColored(ProductAccent, "NOW");
        var player = _ws.Party[PartyState.PlayerSlot];
        var active = ForetellDecisionCore.Prioritize(frame, player == null ? Vector2.Zero : V(player.Position), player?.InstanceID ?? 0)
            .Where(h => h.Prediction.Confidence >= _cfg.VisualConfidence / 100f || h.Provenance == "Terrain cue").Take(4).ToArray();
        if (active.Length == 0)
        {
            ImGui.TextUnformatted("Observing the encounter");
            ImGui.TextWrapped("No actionable prediction is available. This does not establish that the area is safe.");
        }
        foreach (var hazard in active)
        {
            var p = hazard.Prediction;
            ImGui.TextColored(ConfidenceTextColor(p.Confidence), $"{GuidanceInstruction(p.Guidance, p.Kind, p.Geometry)} · {UserFacingPredictionLabel(p)}");
            ImGui.TextWrapped($"{Math.Max(0, (p.Activation - frame.At).TotalSeconds):F1}s · {hazard.Provenance} · {(hazard.SpatiallyKnown ? "shape available" : "shape / response unresolved")}");
        }
        ImGui.Spacing();
        ImGui.TextDisabled(frame.TerrainFresh ? "Terrain: current local surface" : "Terrain: acquiring or refreshing");
        if (_routeResult is { } route && (frame.At - route.At).TotalSeconds <= 2)
            ImGui.TextWrapped($"Route assessment: {route.Assessment.Reason}");
        if (!frame.EvidenceComplete) ImGui.TextWrapped("Capture is incomplete. Strong movement recommendations are suspended.");

        ImGui.Separator();
        ImGui.TextColored(ProductAccent, "UNDERSTANDING THIS CONTENT");
        if (_store.Encounters.TryGetValue(_inspectorTerritory, out var encounter))
        {
            var mechanics = encounter.Mechanics.Values.ToArray();
            if (ImGui.BeginTable("UnderstandingCounts", 3, ImGuiTableFlags.SizingStretchSame))
            {
                DrawMetricCell(mechanics.Count(m => m.HasReliableActionPrior).ToString(), "Client shapes available");
                DrawMetricCell(mechanics.Count(m => !m.HasReliableActionPrior).ToString(), "Learning / unresolved");
                DrawMetricCell(mechanics.Count(m => m.RecentContradictions >= 2).ToString(), "Contradictions to review");
                ImGui.EndTable();
            }
            ImGui.Spacing();
            if (ImGui.BeginTable("UnderstandingMechanics", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Mechanic", ImGuiTableColumnFlags.WidthStretch, 3);
                ImGui.TableSetupColumn("Knowledge", ImGuiTableColumnFlags.WidthStretch, 2);
                ImGui.TableSetupColumn("Seen / assessed");
                ImGui.TableSetupColumn("Correct / wrong");
                ImGui.TableHeadersRow();
                foreach (var mechanic in mechanics.OrderByDescending(m => m.LastSeen).Take(12))
                {
                    var reliability = ForetellReliability.Describe(mechanic);
                    ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextWrapped(MechanicDisplayName(mechanic));
                    ImGui.TableNextColumn(); ImGui.TextWrapped(reliability.ClientShape ? "Client shape + outcome learning" : reliability.Maturity.ToString());
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{reliability.Observations} / {reliability.Verified}");
                    ImGui.TableNextColumn(); ImGui.TextUnformatted($"{reliability.Hits} / {reliability.Misses}");
                }
                ImGui.EndTable();
            }
            ImGui.TextDisabled("Open Knowledge for the evidence and missing information behind each mechanic.");
        }
        else ImGui.TextWrapped("No observations for the selected content yet.");

        ImGui.Separator();
        ImGui.TextColored(ProductAccent, "HOW MANY OCCURRENCES?");
        ImGui.TextWrapped("A complete shape supplied by the client can be shown on its first cast. That does not prove every consequence or the correct response.");
        ImGui.TextWrapped("An inferred rule needs informative outcomes. Ten identical ambiguous observations may still leave several explanations. Only predictions made before the outcome can be counted as independently tested.");
        if (ImGui.CollapsingHeader("What the confidence thresholds mean"))
        {
            ImGui.TextWrapped("The visual threshold controls hypotheses. The warning threshold controls stronger learned guidance. A route additionally requires current terrain, timing, complete capture and no unresolved personal constraint.");
            ImGui.TextWrapped($"For the statistical outcome check alone, starting from zero and assuming only correct independent results: at least {ForetellReliability.AdditionalSuccesses(0, 0, _cfg.VisualConfidence / 100f)} results for {_cfg.VisualConfidence:F0}%, {ForetellReliability.AdditionalSuccesses(0, 0, _cfg.WarningConfidence / 100f)} for {_cfg.WarningConfidence:F0}%. These are best-case evidence requirements, not guaranteed cast counts or survival probabilities.");
        }
        if (ImGui.CollapsingHeader("Pre-impact learning"))
        {
            var model = _store.PreImpact;
            var assessed = model.Classes.Values.Sum(c => c.Assessed);
            var correct = model.Classes.Values.Sum(c => c.Hits);
            ImGui.TextWrapped($"{model.Model.Updates:N0} training updates · {correct}/{assessed} assessed predictions matched · {model.MissingOutcomes} outcomes unassessable");
            ImGui.BeginDisabled(_inPull);
            if (ImGui.Button("Reset pre-impact model"))
                RequestPurge("Pre-impact model", "Reset cross-content classifier weights and calibration? Encounter geometry, recordings and timelines remain available.", () =>
                { _store.PreImpact = new(); _preImpact = new(_store.PreImpact); });
            ImGui.EndDisabled();
            ImGui.TextWrapped("Inputs are frozen at the trigger, before hits, damage and displacement. Outcomes evaluate the prediction before updating the model. This is chronological evaluation; independent unseen-session evaluation is available through the detached replay API.");
        }
    }

    private void DrawMechanicReliability(ContextualMechanic mechanic)
    {
        var summary = ForetellReliability.Describe(mechanic, _cfg.VisualConfidence / 100f, _cfg.WarningConfidence / 100f);
        ImGui.TextColored(ProductAccent, summary.ClientShape ? "CLIENT SHAPE AVAILABLE" : summary.Maturity.ToString().ToUpperInvariant());
        ImGui.TextWrapped(summary.Reason);
        if (ImGui.BeginTable("EvidenceCounts", 4, ImGuiTableFlags.SizingStretchSame))
        {
            DrawMetricCell(summary.Observations.ToString(), "Occurrences seen");
            DrawMetricCell(summary.Verified.ToString(), "Independently assessed");
            DrawMetricCell($"{summary.Hits} / {summary.Misses}", "Correct / contradicted");
            DrawMetricCell(summary.Unverifiable.ToString(), "Outcome unassessable");
            ImGui.EndTable();
        }
        ImGui.TextWrapped($"Conservative outcome estimate: {summary.LowerBound:P1}. This measures tested predictions, not the probability of surviving a mechanic.");
        ImGui.TextWrapped(summary.AdditionalForWarning < 0 ? "No finite number of observations can establish this configured threshold."
            : summary.AdditionalForWarning == 0 ? "The statistical warning requirement is met. Context, geometry and contradictions still gate guidance."
            : $"At least {summary.AdditionalForWarning} additional correct, informative outcomes would be needed to reach the {_cfg.WarningConfidence:F0}% statistical warning threshold, assuming no new errors.");
        if (mechanic.GeometryAmbiguous && !mechanic.HasReliableActionPrior)
            ImGui.TextWrapped("Several spatial shapes fit these positions. The footprint stays hidden until observations distinguish them.");
        if (mechanic.Hypotheses.Count > 0)
        {
            ImGui.TextUnformatted("Explanations still under consideration");
            foreach (var hypothesis in mechanic.Hypotheses.OrderByDescending(h => h.Supports).Take(6))
                ImGui.BulletText($"{hypothesis.Kind}: {hypothesis.Supports} compatible observations — {hypothesis.Reason}");
        }
        if (mechanic.Stages.Count > 0)
        {
            ImGui.TextUnformatted("Observed follow-up stages");
            foreach (var stage in mechanic.Stages.Take(8))
                ImGui.BulletText($"+{stage.Delay:F1}s · {stage.Geometry} · {stage.Observations} seen · {stage.Hits}/{stage.Hits + stage.Misses} assessed");
        }
        ImGui.Separator();
    }

    private void DrawDiagnostics()
    {
        if (ImGui.BeginTabBar("DiagnosticTabs"))
        {
            DrawInspectorTab("Health & counters", DrawTelemetryDashboard);
            DrawInspectorTab("Event stream", DrawInspectorObservations);
            DrawInspectorTab("Reference", DrawInspectorHelp);
            ImGui.EndTabBar();
        }
    }
}
