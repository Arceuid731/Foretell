using System.Text.Json;

namespace BossMod.Foretell;

internal sealed record GuideEventResolution(GuidePhase? Phase, GuideMechanic? Mechanic, GuideTrigger? Trigger, string Reason, string[] Candidates);

internal static class GuideEventMatching
{
    internal static string Scope(GuideDocument document, GuideBoss boss)
        => GuideNames.Hash(document.Duty.Key + "|" + document.SourceHash + "|" + document.ModelRevision + "|" + document.AnalysisLanguage + "|" + JsonSerializer.Serialize(boss));

    internal static bool TargetApplies(string target, ulong targetID, ulong playerID, bool partyTarget)
        => target == "any" || playerID != 0 && partyTarget && targetID != 0 && (target == "self" ? targetID == playerID : target == "other" && targetID != playerID);

    internal static GuideEventResolution Resolve(GuideBoss boss, GuidePhaseDefinition? knownPhase, string kind, string name,
        ulong targetID, ulong playerID, bool partyTarget, int stacks)
    {
        if (string.IsNullOrWhiteSpace(name)) return new(null, null, null, "NameUnavailable", []);
        if (kind is not ("cast" or "status")) return new(null, null, null, "UnsupportedEvent", []);
        List<(GuidePhase Phase, GuideMechanic Mechanic, GuideTrigger? Trigger)> candidates = [];
        var rejected = "NoDocumentedTrigger";
        List<string> named = [];
        foreach (var phase in boss.Phases)
        foreach (var mechanic in phase.Mechanics)
        {
            var advice = mechanic.Advice;
            var triggers = advice?.Triggers.Where(trigger => trigger.Kind == kind && GuideNames.Normalize(trigger.Name) == GuideNames.Normalize(name)).ToArray() ?? [];
            var legacy = !(advice?.Triggers.Any(trigger => GuideNames.Normalize(trigger.Name) == GuideNames.Normalize(name)) ?? false)
                && (kind == "cast" ? GuideSynchronization.MatchesCast(mechanic, name)
                : advice != null ? advice.TriggerKind == "status" && GuideNames.Normalize(advice.TriggerName) == GuideNames.Normalize(name)
                    || GuideNames.Normalize(mechanic.Name) == GuideNames.Normalize(name) && advice.Evidence.Any(quote => GuideRules.Mentions(quote, name))
                    : GuideRules.Mentions(mechanic.Text, name));
            if (triggers.Length == 0 && !legacy) continue;
            named.Add(mechanic.Name);
            if (advice is { Conflict.Length: > 0 } or { ContextOnly: true }) { rejected = "ConflictingOrContextOnly"; continue; }
            if (!GuidePhases.Includes(boss, mechanic, knownPhase)) { rejected = "OtherPhase"; continue; }
            if (triggers.Length == 0) { candidates.Add((phase, mechanic, null)); continue; }
            var applicable = triggers.Where(trigger => TargetApplies(trigger.Target, targetID, playerID, partyTarget)
                && (kind != "status" || stacks >= trigger.MinimumStacks)).ToArray();
            if (applicable.Length == 0) { rejected = "TargetOrStacksNotSatisfied"; continue; }
            var targeted = applicable.Where(trigger => trigger.Target != "any").ToArray();
            if (targeted.Length != 0) applicable = targeted;
            var threshold = applicable.Max(trigger => trigger.MinimumStacks);
            foreach (var trigger in applicable.Where(trigger => trigger.MinimumStacks == threshold).Distinct()) candidates.Add((phase, mechanic, trigger));
        }
        var names = named.Distinct().Take(8).ToArray();
        if (candidates.Count == 0)
        {
            if (rejected == "OtherPhase" && knownPhase != null)
            {
                var transition = Resolve(boss, null, kind, name, targetID, playerID, partyTarget, stacks);
                if (transition.Mechanic is { PhaseMemberships.Length: 1 } next
                    && boss.PhaseDefinitions.Any(phase => phase.ID == next.PhaseMemberships[0].PhaseID))
                    return transition with { Reason = "MatchedPhaseTransition" };
            }
            return new(null, null, null, rejected, names);
        }
        if (candidates.Select(candidate => candidate.Mechanic).Distinct().Count() != 1
            || candidates.Select(candidate => candidate.Trigger?.Cue ?? "").Distinct().Count() != 1)
            return new(null, null, null, "AmbiguousTrigger", names);
        var chosen = candidates[0];
        return new(chosen.Phase, chosen.Mechanic, chosen.Trigger, chosen.Trigger == null ? "MatchedLegacyName" : "MatchedGroundedTrigger", names);
    }
}

internal sealed record GuideBindingAudit(long Sequence, DateTime At, string Scope, string Boss, string? Phase, string Kind,
    ulong SourceID, uint SourceOID, uint SourceNameID, uint ID, string Name, ulong TargetID, int Stacks,
    string Result, string[] Candidates, string? Mechanic, string Instruction, string Mapping, string? BindingKey);
