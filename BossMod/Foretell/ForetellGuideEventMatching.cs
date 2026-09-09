using System.Text.Json;

namespace BossMod.Foretell;

internal sealed record GuideEventResolution(GuidePhase? Phase, GuideMechanic? Mechanic, GuideTrigger? Trigger, string Reason, string[] Candidates);
internal sealed record GuideEventCandidate(GuidePhase Phase, GuideMechanic Mechanic, GuideTrigger? Trigger);

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
        List<GuideEventCandidate> found = [];
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
            foreach (var trigger in triggers) found.Add(new(phase, mechanic, trigger));
            if (legacy) found.Add(new(phase, mechanic, null));
        }
        return Choose(boss, knownPhase, kind, found, targetID, playerID, partyTarget, stacks);
    }

    internal static GuideEventResolution Choose(GuideBoss boss, GuidePhaseDefinition? knownPhase, string kind,
        IReadOnlyList<GuideEventCandidate> found, ulong targetID, ulong playerID, bool partyTarget, int stacks)
    {
        List<GuideEventCandidate> candidates = [];
        var rejected = "NoDocumentedTrigger";
        var otherPhase = false;
        foreach (var group in found.GroupBy(candidate => candidate.Mechanic))
        {
            var mechanic = group.Key;
            var advice = mechanic.Advice;
            if (advice is { Conflict.Length: > 0 } or { ContextOnly: true }) { rejected = "ConflictingOrContextOnly"; continue; }
            if (!GuidePhases.Includes(boss, mechanic, knownPhase)) { rejected = "OtherPhase"; otherPhase = true; continue; }
            var applicable = group.Where(candidate => candidate.Trigger == null || TargetApplies(candidate.Trigger.Target, targetID, playerID, partyTarget)
                && (kind != "status" || stacks >= candidate.Trigger.MinimumStacks)).ToArray();
            if (applicable.Length == 0) { rejected = "TargetOrStacksNotSatisfied"; continue; }
            var targeted = applicable.Where(candidate => candidate.Trigger is { Target: not "any" }).ToArray();
            if (targeted.Length != 0) applicable = targeted;
            var threshold = applicable.Max(candidate => candidate.Trigger?.MinimumStacks ?? 0);
            candidates.AddRange(applicable.Where(candidate => (candidate.Trigger?.MinimumStacks ?? 0) == threshold).Distinct());
        }
        var names = found.Select(candidate => candidate.Mechanic.Name).Distinct().Take(8).ToArray();
        if (candidates.Count == 0)
        {
            if (otherPhase && knownPhase != null)
            {
                var transition = Choose(boss, null, kind, found, targetID, playerID, partyTarget, stacks);
                if (transition.Mechanic is { PhaseMemberships.Length: 1 } next
                    && boss.PhaseDefinitions.Any(phase => phase.ID == next.PhaseMemberships[0].PhaseID))
                    return transition with { Reason = "MatchedPhaseTransition" };
                if (transition.Mechanic != null)
                    return transition with { Reason = "MatchedPhaseUncertain" };
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
    string Result, string[] Candidates, string? Mechanic, string Instruction, string Mapping, string? BindingKey)
{
    public string CatalogHash { get; init; } = "";
    public uint[] CandidateIDs { get; init; } = [];
    public int CandidateIDsOmitted { get; init; }
    public int CastType { get; init; }
    public float EffectRange { get; init; }
}
