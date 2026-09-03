using System.Globalization;
using System.Text.RegularExpressions;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.Action>? _foretellActionSheet;

    // Client data is a PRIOR, not an oracle. CastType/EffectRange/XAxisModifier/Omen can explain many normal
    // telegraphs immediately, while empirical observations remain responsible for confirming or correcting it.
    private ActionGeometryPrior? ReadActionGeometryPrior(ForetellObservation trigger)
    {
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
            var p1 = 0f;
            var p2 = 0f;
            var confidence = 0f;
            var why = "metadata-only";

            // CastType semantics are stable game-data hints used by several FFXIV tooling projects. Where a field
            // does not fully determine geometry (notably cone angle/dynamic lines), keep confidence below the normal
            // visualization gate or leave geometry unknown rather than inventing a lethal answer.
            switch (castType)
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
                        geometry = GeometryKind.Circle;
                        p1 = effectRange;
                        confidence = .96f;
                        why = "CastType circle + EffectRange";
                    }
                    break;
                case 3: // cone/fan with actor padding
                    if (effectRange > 0 && fanHalfAngle > 0)
                    {
                        geometry = GeometryKind.Cone;
                        p1 = effectRange + hitbox;
                        p2 = fanHalfAngle;
                        confidence = .90f;
                        why = "CastType padded cone + EffectRange + actor hitbox + Omen angle";
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
                        geometry = GeometryKind.Circle;
                        p1 = effectRange + hitbox;
                        confidence = .91f;
                        why = "CastType padded circle + EffectRange + actor hitbox";
                    }
                    break;
                case 6: // meteor/proximity-like circle family; shape/range useful but semantic falloff may differ
                    if (effectRange > 0)
                    {
                        geometry = GeometryKind.Circle;
                        p1 = effectRange;
                        confidence = .82f;
                        why = "CastType meteor circle + EffectRange (falloff semantics unverified)";
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
                        confidence = .70f; // intentionally below the default visual gate until corroborated
                        why = "CastType dynamic line + current target distance + XAxisModifier";
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
                    if (effectRange > 0 && fanHalfAngle > 0)
                    {
                        geometry = GeometryKind.Cone;
                        p1 = effectRange;
                        p2 = fanHalfAngle;
                        confidence = .94f;
                        why = "CastType cone + EffectRange + Omen fan angle";
                    }
                    break;
            }

            var evidence = $"Action sheet: CastType={castType}, EffectRange={effectRange}, XAxisModifier={xAxis}, Range={range}, " +
                $"TargetArea={targetArea}, AffectsPosition={affectsPosition}, Omen={omenID}:{omen}, VFX={vfxID}; {why}";
            return new(trigger.PrimaryID, geometry, p1, p2, confidence, castType, effectRange, xAxis, targetArea, omenID, omen, evidence);
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

        ContextualMechanic? mechanic = null;
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
                    FirstSeen = DateTime.UtcNow,
                    LastSeen = DateTime.UtcNow
                };
                encounter.Mechanics[key] = mechanic;
                ++_session.NewMechanics;
            }

            mechanic.PriorGeometry = p.Geometry;
            mechanic.PriorP1 = p.P1;
            mechanic.PriorP2 = p.P2;
            mechanic.PriorConfidence = p.Confidence;
            mechanic.PriorCastType = p.CastType;
            mechanic.PriorEffectRange = p.EffectRange;
            mechanic.PriorXAxisModifier = p.XAxisModifier;
            mechanic.PriorTargetArea = p.TargetArea;
            mechanic.PriorOmenID = p.OmenID;
            mechanic.PriorOmen = p.Omen;
            mechanic.PriorEvidence = p.Evidence;
            mechanic.Evidence[ObservationKind.ClientMetadata] = 1;
            mechanic.LastSeen = DateTime.UtcNow;

            if (p.Geometry != GeometryKind.Unknown && (mechanic.Geometry == GeometryKind.Unknown || mechanic.Observations == 0))
            {
                mechanic.Geometry = p.Geometry;
                mechanic.Kind = mechanic.Kind == MechanicKind.Unknown ? MechanicKind.GroundAOE : mechanic.Kind;
                mechanic.P1 = p.P1;
                mechanic.P2 = p.P2;
            }
        }

        if (p.Geometry == GeometryKind.Unknown || p.Confidence <= 0)
        {
            _lastEvidence = p.Evidence;
            return;
        }

        var source = new Vector2(trigger.X, trigger.Z);
        var target = new Vector2(trigger.TargetX, trigger.TargetZ);
        var origin = p.Geometry is GeometryKind.Circle or GeometryKind.Donut ? target : source;
        var preferLearned = mechanic is { Geometry: not GeometryKind.Unknown } && (mechanic.Observations > 0 || mechanic.Confidence >= p.Confidence);
        var confidence = preferLearned ? mechanic!.Confidence : p.Confidence;
        var geometry = preferLearned ? mechanic!.Geometry : p.Geometry;
        var p1 = preferLearned && mechanic!.P1 > 0 ? mechanic.P1 : p.P1;
        var p2 = preferLearned && mechanic!.P2 > 0 ? mechanic.P2 : p.P2;
        confidence = ForetellInferenceCore.GuidanceConfidence(confidence, mechanic?.ForecastHits ?? 0, mechanic?.ForecastMisses ?? 0);

        // StartEpisode may already have emitted a learned prediction. Never downgrade it; when metadata agrees,
        // replace it with the fused confidence/evidence so first-cast priors and learned evidence reinforce each other.
        if (_predictions.TryGetValue(trigger.Sequence, out var existing))
        {
            if (existing.Geometry != geometry)
            {
                // StartEpisode's prediction comes from persisted learned memory. A disagreeing static prior may not
                // replace it once real outcomes exist for the signal.
                if (mechanic?.Observations > 0 || existing.Confidence >= confidence) return;
            }
            else
            {
                confidence = Math.Max(confidence, existing.Confidence);
            }
        }

        var prediction = new ActivePrediction(trigger.ActorID, trigger.PrimaryID, geometry, MechanicKind.GroundAOE,
            origin, target, trigger.Rotation, p1, p2, trigger.At.AddSeconds(float.IsFinite(trigger.Value1) ? Math.Clamp(trigger.Value1, 0, 120) : 0), confidence, p.Evidence,
            SignalKey(trigger), trigger.TargetID, GuidanceKind.Avoid, false, LookupActionName(trigger.PrimaryID) ?? $"Action 0x{trigger.PrimaryID:X}");
        StorePrediction(trigger.Sequence, prediction, trigger);
        if (_episodes.GetValueOrDefault(trigger.Sequence) is { } episode)
        {
            episode.ForecastIssued = true;
            episode.ForecastGeometry = geometry;
            episode.ForecastKind = mechanic?.Kind ?? MechanicKind.GroundAOE;
            episode.ForecastP1 = p1;
            episode.ForecastP2 = p2;
            episode.ForecastConfidence = confidence;
        }
        _lastEvidence = $"Metadata prior AID {trigger.PrimaryID}: {geometry} {confidence:P0} | {p.Evidence}";
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
