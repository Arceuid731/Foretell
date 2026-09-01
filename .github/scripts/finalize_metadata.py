from pathlib import Path
import re


def load(path: str) -> str:
    return Path(path).read_text(encoding="utf-8-sig")


def save(path: str, text: str) -> None:
    Path(path).write_text(text, encoding="utf-8")


def replace_once(path: str, old: str, new: str) -> None:
    text = load(path)
    if old not in text:
        raise RuntimeError(f"anchor not found in {path}: {old!r}")
    save(path, text.replace(old, new, 1))


def regex_once(path: str, pattern: str, repl: str, flags: int = 0) -> None:
    text = load(path)
    patched, count = re.subn(pattern, repl, text, count=1, flags=flags)
    if count != 1:
        raise RuntimeError(f"regex anchor not found in {path}: {pattern!r}")
    save(path, patched)


# Rectangle and Cross fitting must share the nested loop scope.
regex_once(
    "BossMod/Foretell/ForetellLearning.cs",
    r"""        for \(var length = 8f; length <= 50f; length \+= 4f\)\n            for \(var halfWidth = 1\.5f; halfWidth <= 12f; halfWidth \+= 1\.5f\)\n                Try\(GeometryKind\.Rectangle, length, halfWidth, p => p\.Forward >= 0 && p\.Forward <= length && MathF\.Abs\(p\.Side\) <= halfWidth\);\n                Try\(GeometryKind\.Cross, length, halfWidth, p =>\n                    \(MathF\.Abs\(p\.Side\) <= halfWidth && MathF\.Abs\(p\.Forward\) <= length\) \|\|\n                    \(MathF\.Abs\(p\.Forward\) <= halfWidth && MathF\.Abs\(p\.Side\) <= length\)\);""",
    """        for (var length = 8f; length <= 50f; length += 4f)
            for (var halfWidth = 1.5f; halfWidth <= 12f; halfWidth += 1.5f)
            {
                Try(GeometryKind.Rectangle, length, halfWidth, p => p.Forward >= 0 && p.Forward <= length && MathF.Abs(p.Side) <= halfWidth);
                Try(GeometryKind.Cross, length, halfWidth, p =>
                    (MathF.Abs(p.Side) <= halfWidth && MathF.Abs(p.Forward) <= length) ||
                    (MathF.Abs(p.Forward) <= halfWidth && MathF.Abs(p.Side) <= length));
            }""",
)

regex_once(
    "BossMod/Foretell/ForetellGeometry.cs",
    r"""        for \(var length = 8f; length <= 50f; length \+= 4f\)\n            for \(var halfWidth = 1\.5f; halfWidth <= 12f; halfWidth \+= 1\.5f\)\n                Try\(new\(GeometryKind\.Rectangle, cast\.Origin, cast\.Rotation, length, halfWidth,\n                    Score\(samples, p => InRect\(p, cast\.Origin, cast\.Rotation, length, halfWidth\)\)\)\);\n                Try\(new\(GeometryKind\.Cross, cast\.Origin, cast\.Rotation, length, halfWidth,\n                    Score\(samples, p => InCross\(p, cast\.Origin, cast\.Rotation, length, halfWidth\)\)\)\);""",
    """        for (var length = 8f; length <= 50f; length += 4f)
            for (var halfWidth = 1.5f; halfWidth <= 12f; halfWidth += 1.5f)
            {
                Try(new(GeometryKind.Rectangle, cast.Origin, cast.Rotation, length, halfWidth,
                    Score(samples, p => InRect(p, cast.Origin, cast.Rotation, length, halfWidth))));
                Try(new(GeometryKind.Cross, cast.Origin, cast.Rotation, length, halfWidth,
                    Score(samples, p => InCross(p, cast.Origin, cast.Rotation, length, halfWidth))));
            }""",
)

# Static client data seeds the learner, but once outcomes exist the empirical geometry arbitrates.
regex_once(
    "BossMod/Foretell/ForetellActionMetadata.cs",
    r"""        var confidence = mechanic\?\.Confidence \?\? p\.Confidence;\n        var geometry = mechanic is \{ Geometry: not GeometryKind\.Unknown \} && mechanic\.Confidence >= p\.Confidence \? mechanic\.Geometry : p\.Geometry;\n        var p1 = geometry == mechanic\?\.Geometry && mechanic\.P1 > 0 \? mechanic\.P1 : p\.P1;\n        var p2 = geometry == mechanic\?\.Geometry && mechanic\.P2 > 0 \? mechanic\.P2 : p\.P2;""",
    """        var preferLearned = mechanic is { Geometry: not GeometryKind.Unknown } && (mechanic.Observations > 0 || mechanic.Confidence >= p.Confidence);
        var confidence = preferLearned ? mechanic!.Confidence : p.Confidence;
        var geometry = preferLearned ? mechanic!.Geometry : p.Geometry;
        var p1 = preferLearned && mechanic!.P1 > 0 ? mechanic.P1 : p.P1;
        var p2 = preferLearned && mechanic!.P2 > 0 ? mechanic.P2 : p.P2;""",
)

regex_once(
    "BossMod/Foretell/ForetellActionMetadata.cs",
    r"""        if \(_predictions\.TryGetValue\(trigger\.Sequence, out var existing\)\)\n        \{\n            if \(existing\.Geometry != geometry && existing\.Confidence >= confidence\) return;\n            confidence = Math\.Max\(confidence, existing\.Confidence\);\n        \}""",
    """        if (_predictions.TryGetValue(trigger.Sequence, out var existing))
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
        }""",
)

# Reformat the confidence fusion and keep the 99% safe gate unreachable from metadata alone.
regex_once(
    "BossMod/Foretell/ForetellModel.cs",
    r"""[ \t]*var empirical = EmpiricalConfidence;\n.*?[ \t]*return Math\.Clamp\(fused, 0, \.999f\);""",
    """            var empirical = EmpiricalConfidence;
            if (PriorConfidence <= 0) return empirical;
            if (Observations == 0) return Math.Min(PriorConfidence, .98f);

            var effectivePrior = PriorConfidence;
            if (PriorGeometry != GeometryKind.Unknown && Geometry != GeometryKind.Unknown)
            {
                if (PriorGeometry != Geometry)
                {
                    // Observed geometry-family disagreement means this Action sheet row is not describing the
                    // correlated encounter effect accurately enough to dominate the learner.
                    effectivePrior *= .20f;
                }
                else if (PriorP1 > 0 && P1 > 0)
                {
                    var drift1 = MathF.Abs(P1 - PriorP1) / MathF.Max(1, PriorP1);
                    var drift2 = PriorP2 > 0 && P2 > 0 ? MathF.Abs(P2 - PriorP2) / MathF.Max(1, PriorP2) : 0;
                    var drift = MathF.Max(drift1, drift2);
                    if (drift > .15f)
                        effectivePrior *= Math.Clamp(1f - drift, .25f, .85f);
                }
            }

            // Client metadata can make ordinary telegraphs useful on the first cast. It can accelerate confidence,
            // but the 99% safe-guidance gate still requires corroborating empirical evidence.
            effectivePrior = Math.Min(effectivePrior, .98f);
            var fused = 1f - (1f - effectivePrior) * (1f - empirical);
            if (AmbiguousSamples > 0)
                fused *= 1f / (1f + AmbiguousSamples * .08f);
            return Math.Clamp(fused, 0, .999f);""",
    re.S,
)

# Tidy Inspector formatting and expose Cross plus client-metadata/color semantics.
regex_once(
    "BossMod/Foretell/ForetellInspector.cs",
    r"""            ImGui\.TextUnformatted\(\$"Source: OID \{mechanic\.SourceOID:X8\} \(\{mechanic\.SourceKind\}\) \| trigger: \{mechanic\.TriggerKind\} ID \{mechanic\.TriggerID:X\}"\);\n\s*if \(mechanic\.PriorConfidence > 0 \|\| mechanic\.PriorCastType != 0\)\n\s*\{\n\s*ImGui\.TextUnformatted\(\$"Client-data prior: \{mechanic\.PriorGeometry\} \{mechanic\.PriorConfidence:P0\} \| CastType=\{mechanic\.PriorCastType\} \| EffectRange=\{mechanic\.PriorEffectRange\} \| XAxis=\{mechanic\.PriorXAxisModifier\} \| TargetArea=\{mechanic\.PriorTargetArea\}"\);\n\s*ImGui\.TextUnformatted\(\$"Omen: \{mechanic\.PriorOmenID\}:\{mechanic\.PriorOmen\}"\);\n\s*ImGui\.TextWrapped\(\$"Prior rationale: \{mechanic\.PriorEvidence\}"\);\n\s*\}""",
    """            ImGui.TextUnformatted($"Source: OID {mechanic.SourceOID:X8} ({mechanic.SourceKind}) | trigger: {mechanic.TriggerKind} ID {mechanic.TriggerID:X}");
            if (mechanic.PriorConfidence > 0 || mechanic.PriorCastType != 0)
            {
                ImGui.TextUnformatted($"Client-data prior: {mechanic.PriorGeometry} {mechanic.PriorConfidence:P0} | CastType={mechanic.PriorCastType} | EffectRange={mechanic.PriorEffectRange} | XAxis={mechanic.PriorXAxisModifier} | TargetArea={mechanic.PriorTargetArea}");
                ImGui.TextUnformatted($"Omen: {mechanic.PriorOmenID}:{mechanic.PriorOmen}");
                ImGui.TextWrapped($"Prior rationale: {mechanic.PriorEvidence}");
            }""",
)

replace_once(
    "BossMod/Foretell/ForetellInspector.cs",
    '        GeometryKind.Rectangle => $"length {mechanic.P1:F1} yalms / half-width {mechanic.P2:F1} yalms",',
    '        GeometryKind.Rectangle => $"length {mechanic.P1:F1} yalms / half-width {mechanic.P2:F1} yalms",\n'
    '        GeometryKind.Cross => $"four arms {mechanic.P1:F1} yalms / half-width {mechanic.P2:F1} yalms",',
)

replace_once(
    "BossMod/Foretell/ForetellInspector.cs",
    '        ImGui.TextUnformatted($"At least {_cfg.SafeConfidence:F0}%: eligible for safe-position guidance. This intentionally uses an extremely high Never Guess Lethal threshold.");',
    '        ImGui.TextUnformatted($"At least {_cfg.SafeConfidence:F0}%: eligible for safe-position guidance. This intentionally uses an extremely high Never Guess Lethal threshold.");\n'
    '        ImGui.TextUnformatted("World/radar color encodes confidence, not damage: cyan -> yellow -> orange -> red as reliability increases. The radar also prints the percentage.");',
)

replace_once(
    "BossMod/Foretell/ForetellInspector.cs",
    '        ImGui.TextUnformatted("Casts and hit targets; statuses; icons; VFX; tethers; actor lifecycle/targetability/model state; event objects; action-timeline events; NPC yells; map effects; director updates; party positions and sudden displacement.");',
    '        ImGui.TextUnformatted("Casts and hit targets; statuses; icons; VFX; tethers; actor lifecycle/targetability/model state; event objects; action-timeline events; NPC yells; map effects; director updates; party positions and sudden displacement.");\n'
    '        ImGui.TextUnformatted("For cast actions Foretell also reads local client Action metadata (CastType, EffectRange, XAxisModifier, TargetArea, Omen/VFX and actor hitbox) as a prior before outcome evidence is available.");',
)

print("Foretell metadata finalization patch applied successfully")
