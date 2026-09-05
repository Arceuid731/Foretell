using System.Globalization;
using System.Text.RegularExpressions;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.Action>? _foretellActionSheet;

    // Client data is a typed prior. Complete ordinary telegraph geometry is authoritative for that Action row;
    // empirical outcomes validate it but cannot rewrite it from unrelated ambient movement/status evidence.
    // Ambiguous metadata families remain explicitly unshaped and are left to outcome learning.
    private ActionGeometryPrior? ReadActionGeometryPrior(ForetellObservation trigger)
    {
        if (_isReplay || trigger.Prior != null) return trigger.Prior;
        if (trigger.PrimaryID == 0) return null;
        try
        {
            _foretellActionSheet ??= Service.LuminaSheet<Lumina.Excel.Sheets.Action>();
            if (_foretellActionSheet == null) return null;

            object row = _foretellActionSheet.GetRow(trigger.PrimaryID);
            var castType = ReadInt(Member(row, "CastType"));
            var effectRange = ReadInt(Member(row, "EffectRange"));
            var xAxis = ReadInt(Member(row, "XAxisModifier"));
            var range = ReadInt(Member(row, "Range"));
            var targetArea = ReadBool(Member(row, "TargetArea"));
            var affectsPosition = ReadBool(Member(row, "AffectsPosition"));
            var vfxID = RowRefID(Member(row, "VFX"));
            var (omenID, omen) = ReadOmen(Member(row, "Omen"));

            var actor = trigger.ActorID != 0 ? _ws.Actors.Find(trigger.ActorID) : null;
            var hitbox = Math.Max(0, ReadFloat(Member(actor, "HitboxRadius")));
            var source = new Vector2(trigger.X, trigger.Z);
            var target = new Vector2(trigger.TargetX, trigger.TargetZ);
            var targetDistance = Vector2.Distance(source, target);
            var fanHalfAngle = ParseFanHalfAngle(omen);

            var geometry = GeometryKind.Unknown;
            var kind = MechanicKind.Unknown;
            var p1 = 0f;
            var p2 = 0f;
            var confidence = 0f;
            var why = "metadata-only";

            // CastType semantics are stable game-data hints used by several FFXIV tooling projects. Where a field
            // does not fully determine geometry (notably cone angle/dynamic lines), keep confidence below the normal
            // visualization gate or leave geometry unknown rather than inventing a lethal answer.
            if (ForetellInferenceCore.IsGazeActionVFX(vfxID))
            {
                kind = MechanicKind.Gaze;
                confidence = .94f;
                why = "VFX identifies gaze; direction is semantic, not a danger circle";
            }
            else switch (castType)
            {
                case 1: // generic / sometimes fan; only trust it when Omen independently exposes a fan angle
                    if (effectRange > 0 && fanHalfAngle > 0)
                    {
                        geometry = GeometryKind.Cone;
                        p1 = effectRange;
                        p2 = fanHalfAngle;
                        confidence = .84f;
                        why = "CastType generic + Omen fan angle";
                    }
                    break;
                case 2: // circle, no padding
                    if (effectRange > 0)
                    {
                        p1 = effectRange;
                        if (ForetellInferenceCore.IsAmbiguousLargeCircleAction(castType, effectRange, targetArea, omenID))
                        {
                            confidence = .72f;
                            why = "Arena-scale circle family without target-area/Omen evidence; spatial guidance is ambiguous";
                        }
                        else
                        {
                            geometry = GeometryKind.Circle;
                            confidence = .96f;
                            why = "CastType circle + EffectRange";
                        }
                    }
                    break;
                case 3: // cone/fan with actor padding
                    if (effectRange > 0)
                    {
                        geometry = GeometryKind.Cone;
                        p1 = effectRange + hitbox;
                        p2 = fanHalfAngle;
                        confidence = fanHalfAngle > 0 ? .90f : .62f;
                        why = fanHalfAngle > 0
                            ? "CastType padded cone + EffectRange + actor hitbox + Omen angle"
                            : "CastType identifies padded cone family; angle requires outcome evidence";
                    }
                    break;
                case 4: // static line
                    if (effectRange > 0 && xAxis > 0)
                    {
                        geometry = GeometryKind.Rectangle;
                        p1 = effectRange;
                        p2 = xAxis * .5f;
                        confidence = .91f;
                        why = "CastType static line + EffectRange + XAxisModifier";
                    }
                    break;
                case 5: // circle with actor padding
                    if (effectRange > 0)
                    {
                        p1 = effectRange + hitbox;
                        if (ForetellInferenceCore.IsAmbiguousLargeCircleAction(castType, effectRange, targetArea, omenID))
                        {
                            confidence = .72f;
                            why = "Arena-scale padded-circle family without target-area/Omen evidence; spatial guidance is ambiguous";
                        }
                        else
                        {
                            geometry = GeometryKind.Circle;
                            confidence = .91f;
                            why = "CastType padded circle + EffectRange + actor hitbox";
                        }
                    }
                    break;
                case 6: // meteor/proximity/raidwide/line-of-sight family; EffectRange alone cannot prove guidance
                    if (effectRange > 0)
                    {
                        // Keep the observed family range as evidence, but do not turn it into an AVOID circle.
                        // Ecliptic Meteor demonstrates why: its 100y range describes the affected arena, while
                        // survival depends on line-of-sight rather than leaving that radius.
                        geometry = GeometryKind.Unknown;
                        p1 = effectRange;
                        confidence = .72f;
                        why = "CastType meteor/proximity/raidwide/line-of-sight family + EffectRange; spatial guidance requires outcome evidence";
                    }
                    break;
                case 7: // ground-targeted circle / puddle family
                    if (effectRange > 0)
                    {
                        geometry = GeometryKind.Circle;
                        p1 = effectRange;
                        confidence = .96f;
                        why = "CastType ground circle + EffectRange";
                    }
                    break;
                case 8: // line following target; width is useful, length is contextual/dynamic
                    if (xAxis > 0 && targetDistance > .5f)
                    {
                        geometry = GeometryKind.Rectangle;
                        p1 = Math.Max(targetDistance, effectRange);
                        p2 = xAxis * .5f;
                        // CastType, width and both live endpoints fully establish a line telegraph. Target motion
                        // can still alter the final length, so this is display-grade rather than safe-guidance-grade.
                        confidence = .86f;
                        why = "CastType dynamic line + live source/target endpoints + XAxisModifier";
                    }
                    break;
                case 10: // donut family; inner radius is not safely derivable from verified Action fields alone
                    confidence = .60f;
                    why = "CastType identifies donut family; inner radius requires Omen/outcome evidence before drawing";
                    break;
                case 11: // cross: two perpendicular lines
                    if (effectRange > 0 && xAxis > 0)
                    {
                        geometry = GeometryKind.Cross;
                        p1 = effectRange;
                        p2 = xAxis * .5f;
                        confidence = .90f;
                        why = "CastType cross + EffectRange + XAxisModifier";
                    }
                    break;
                case 12: // line, no padding
                    if (effectRange > 0 && xAxis > 0)
                    {
                        geometry = GeometryKind.Rectangle;
                        p1 = effectRange;
                        p2 = xAxis * .5f;
                        confidence = .94f;
                        why = "CastType line + EffectRange + XAxisModifier";
                    }
                    break;
                case 13: // cone/fan, no padding
                    if (effectRange > 0)
                    {
                        geometry = GeometryKind.Cone;
                        p1 = effectRange;
                        p2 = fanHalfAngle;
                        confidence = fanHalfAngle > 0 ? .94f : .64f;
                        why = fanHalfAngle > 0
                            ? "CastType cone + EffectRange + Omen fan angle"
                            : "CastType identifies cone family; angle requires outcome evidence";
                    }
                    break;
            }

            if (kind == MechanicKind.Unknown && ForetellInferenceCore.IsReliableSpatialActionPrior(MechanicKind.GroundAOE, geometry, confidence, p1, p2))
                kind = MechanicKind.GroundAOE;

            var evidence = $"Action sheet: CastType={castType}, EffectRange={effectRange}, XAxisModifier={xAxis}, Range={range}, " +
                $"TargetArea={targetArea}, AffectsPosition={affectsPosition}, Omen={omenID}:{omen}, VFX={vfxID}; {why}";
            return ForetellInferenceCore.NormalizeActionPrior(new(trigger.PrimaryID, geometry, kind, p1, p2, confidence, castType, effectRange, xAxis, targetArea, omenID, omen, vfxID, evidence));
        }
        catch (Exception e)
        {
            Service.LogVerbose($"[Foretell] Action metadata lookup failed for {trigger.PrimaryID}: {e.Message}");
            return null;
        }
    }

    private void ApplyActionMetadataPrior(ForetellObservation trigger)
    {
        var prior = ReadActionGeometryPrior(trigger);
        if (prior is not ActionGeometryPrior p) return;

        ContextualMechanic? mechanic = _store.Encounters.GetValueOrDefault(trigger.TerritoryID)?.Mechanics.GetValueOrDefault(SignalKey(trigger));
        if (_cfg.EnableLearning)
        {
            var encounter = Encounter(trigger.TerritoryID);
            var key = SignalKey(trigger);
            if (!encounter.Mechanics.TryGetValue(key, out mechanic))
            {
                mechanic = new()
                {
                    Key = key,
                    TerritoryID = trigger.TerritoryID,
                    SourceOID = trigger.ActorOID,
                    SourceKind = trigger.SourceKind,
                    TriggerKind = trigger.Kind,
                    TriggerID = trigger.PrimaryID,
                    FirstSeen = LearningNow,
                    LastSeen = LearningNow
                };
                encounter.Mechanics[key] = mechanic;
                ++_session.NewMechanics;
            }

            mechanic.PriorGeometry = p.Geometry;
            mechanic.PriorKind = p.Kind;
            mechanic.PriorP1 = p.P1;
            mechanic.PriorP2 = p.P2;
            mechanic.PriorConfidence = p.Confidence;
            mechanic.PriorCastType = p.CastType;
            mechanic.PriorEffectRange = p.EffectRange;
            mechanic.PriorXAxisModifier = p.XAxisModifier;
            mechanic.PriorTargetArea = p.TargetArea;
            mechanic.PriorOmenID = p.OmenID;
            mechanic.PriorOmen = p.Omen;
            mechanic.PriorVFXID = p.VFXID;
            mechanic.PriorEvidence = p.Evidence;
            mechanic.Evidence[ObservationKind.ClientMetadata] = 1;
            mechanic.LastSeen = LearningNow;

            ReassertReliableActionPrior(mechanic);
        }

        if (p.Geometry == GeometryKind.Unknown || p.Confidence <= 0
            || !ForetellInferenceCore.GeometryParametersComplete(p.Geometry, p.P1, p.P2))
        {
            // The cast itself is certain even when its danger shape is not. Surface longer encounter casts as a
            // text-only WATCH item, while deliberately leaving geometry and guidance empty so no fake world/radar
            // telegraph or safe position can be produced. This also keeps angle-less cone families honest.
            var hasSemanticPrior = p.Kind != MechanicKind.Unknown;
            var shouldWatch = ForetellInferenceCore.ShouldSurfaceUnshapedCast(trigger.Value1);
            var replaceExisting = !_predictions.TryGetValue(trigger.Sequence, out var existingUnshaped)
                || existingUnshaped.Provenance != "Pre-impact model" && !HasTrustworthyLearnedUnshapedMechanic(mechanic, existingUnshaped);
            if ((hasSemanticPrior || shouldWatch) && replaceExisting)
            {
                var watchSource = new Vector2(trigger.X, trigger.Z);
                var watchTarget = new Vector2(trigger.TargetX, trigger.TargetZ);
                var semanticKind = hasSemanticPrior ? p.Kind : MechanicKind.Unknown;
                var guidance = ForetellInferenceCore.GuidanceFor(semanticKind);
                var watchConfidence = hasSemanticPrior ? p.Confidence : .76f;
                var remainingSeconds = float.IsFinite(trigger.Value1) ? Math.Clamp(trigger.Value1, 0, 120) : 0;
                var evidence = hasSemanticPrior
                    ? $"Client metadata identifies {semanticKind}; spatial geometry intentionally omitted; {p.Evidence}"
                    : $"Observed {trigger.Value1:F1}s cast; spatial geometry incomplete; {p.Evidence}";
                StorePrediction(trigger.Sequence, new(trigger.ActorID, trigger.PrimaryID, GeometryKind.Unknown, semanticKind,
                    watchSource, watchTarget, trigger.Rotation, 0, 0, trigger.At.AddSeconds(remainingSeconds), watchConfidence, evidence,
                    SignalKey(trigger), trigger.TargetID, guidance, false, LookupActionName(trigger.PrimaryID) ?? $"Action 0x{trigger.PrimaryID:X}"), trigger);
                if (_episodes.GetValueOrDefault(trigger.Sequence) is { } watchEpisode)
                {
                    watchEpisode.ForecastIssued = true;
                    watchEpisode.ForecastGeometry = GeometryKind.Unknown;
                    watchEpisode.ForecastKind = semanticKind;
                    watchEpisode.ForecastP1 = 0;
                    watchEpisode.ForecastP2 = 0;
                    watchEpisode.ForecastConfidence = watchConfidence;
                }
            }
            _lastEvidence = p.Evidence;
            return;
        }

        var source = new Vector2(trigger.X, trigger.Z);
        var target = new Vector2(trigger.TargetX, trigger.TargetZ);
        if (trigger.TargetID == 0 && !p.TargetArea) target = source;
        var origin = p.Geometry is GeometryKind.Circle or GeometryKind.Donut ? target : source;
        var confidence = mechanic?.RecentContradictions >= 2 ? Math.Min(p.Confidence, .74f) : p.Confidence;
        var geometry = p.Geometry;
        var p1 = p.P1;
        var p2 = p.P2;

        // StartEpisode may already have emitted a learned prediction. Never downgrade it; when metadata agrees,
        // replace it with the fused confidence/evidence so first-cast priors and learned evidence reinforce each other.
        if (_predictions.TryGetValue(trigger.Sequence, out var existing))
        {
            if (existing.Geometry == geometry && existing.Kind == MechanicKind.GroundAOE && existing.Guidance == GuidanceKind.Avoid)
                confidence = Math.Min(mechanic?.RecentContradictions >= 2 ? .74f : 1f, Math.Max(confidence, existing.Confidence));
        }

        var prediction = new ActivePrediction(trigger.ActorID, trigger.PrimaryID, geometry, MechanicKind.GroundAOE,
            origin, target, trigger.Rotation, p1, p2, trigger.At.AddSeconds(float.IsFinite(trigger.Value1) ? Math.Clamp(trigger.Value1, 0, 120) : 0), confidence, p.Evidence,
            SignalKey(trigger), trigger.TargetID, GuidanceKind.Avoid, false, LookupActionName(trigger.PrimaryID) ?? $"Action 0x{trigger.PrimaryID:X}");
        StorePrediction(trigger.Sequence, prediction, trigger);
        if (_episodes.GetValueOrDefault(trigger.Sequence) is { } episode)
        {
            episode.ForecastIssued = true;
            episode.ForecastGeometry = geometry;
            episode.ForecastKind = MechanicKind.GroundAOE;
            episode.ForecastP1 = p1;
            episode.ForecastP2 = p2;
            episode.ForecastConfidence = confidence;
        }
        _lastEvidence = $"Metadata prior AID {trigger.PrimaryID}: {geometry} {confidence:P0} | {p.Evidence}";
    }

    private static void ReassertReliableActionPrior(ContextualMechanic mechanic)
    {
        if (mechanic.PriorKind is MechanicKind.Gaze or MechanicKind.Knockback && mechanic.PriorConfidence >= .90f)
        {
            mechanic.Kind = mechanic.PriorKind;
            mechanic.Geometry = GeometryKind.Unknown;
            mechanic.P1 = 0;
            mechanic.P2 = 0;
            return;
        }

        if (!ForetellInferenceCore.IsReliableSpatialActionPrior(mechanic.PriorKind, mechanic.PriorGeometry,
            mechanic.PriorConfidence, mechanic.PriorP1, mechanic.PriorP2))
            return;
        mechanic.Kind = MechanicKind.GroundAOE;
        mechanic.Geometry = mechanic.PriorGeometry;
        mechanic.P1 = mechanic.PriorP1;
        mechanic.P2 = mechanic.PriorP2;
    }

    private static bool HasTrustworthyLearnedUnshapedMechanic(ContextualMechanic? mechanic, ActivePrediction prediction)
    {
        if (mechanic == null || mechanic.Observations < 3 || mechanic.Confirmations < 2 || prediction.Confidence < .75f)
            return false;
        var directAffectedEvidence = mechanic.AffectedSamples > 0;
        return mechanic.Kind switch
        {
            MechanicKind.GroundAOE => mechanic.ForecastHits >= 3 && !mechanic.GeometryAmbiguous,
            MechanicKind.Raidwide => mechanic.AffectedSamples >= 3,
            MechanicKind.Tankbuster or MechanicKind.TargetedAOE => directAffectedEvidence,
            MechanicKind.Stack or MechanicKind.Spread or MechanicKind.LineStack => mechanic.AffectedSamples >= 2 && mechanic.Evidence.GetValueOrDefault(ObservationKind.Icon) > 0,
            MechanicKind.Tether => mechanic.Evidence.GetValueOrDefault(ObservationKind.TetherStart) > 0,
            MechanicKind.Knockback or MechanicKind.ForcedMovement => mechanic.MovementSamples >= 2,
            MechanicKind.Environment or MechanicKind.Transition => mechanic.Evidence.GetValueOrDefault(ObservationKind.MapEffect) > 0
                || mechanic.Evidence.GetValueOrDefault(ObservationKind.EventObjectState) > 0
                || mechanic.Evidence.GetValueOrDefault(ObservationKind.DirectorUpdate) > 0
                || mechanic.Evidence.GetValueOrDefault(ObservationKind.TopologySnapshot) > 0,
            _ => false
        };
    }

    private static int ReadInt(object? value)
    {
        try { return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static float ReadFloat(object? value)
    {
        try
        {
            var result = value == null ? 0 : Convert.ToSingle(value, CultureInfo.InvariantCulture);
            return float.IsFinite(result) ? result : 0;
        }
        catch { return 0; }
    }

    private static bool ReadBool(object? value)
    {
        try { return value != null && Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
        catch { return false; }
    }

    private static uint RowRefID(object? rowRef)
        => ToUInt(Member(rowRef, "RowId")) ?? ToUInt(Member(rowRef, "RowID")) ?? ToUInt(Member(rowRef, "Row")) ?? 0;

    private static (uint ID, string Name) ReadOmen(object? rowRef)
    {
        var id = RowRefID(rowRef);
        try
        {
            var value = Member(rowRef, "Value");
            var name = Member(value, "Path")?.ToString()
                ?? Member(value, "Name")?.ToString()
                ?? Member(value, "File")?.ToString()
                ?? "";
            return (id, name);
        }
        catch { return (id, ""); }
    }

    private static float ParseFanHalfAngle(string omen)
    {
        if (string.IsNullOrWhiteSpace(omen)) return 0;
        var match = Regex.Match(omen, @"fan(?<deg>\d{2,3})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !float.TryParse(match.Groups["deg"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fullDegrees))
            return 0;
        if (fullDegrees <= 0 || fullDegrees > 360) return 0;
        return fullDegrees * MathF.PI / 360f;
    }
}
