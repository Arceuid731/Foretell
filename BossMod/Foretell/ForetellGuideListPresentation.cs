using System.Text.RegularExpressions;

namespace BossMod.Foretell;

internal sealed record GuideCombatListEntry(GuidePhase Phase, GuideMechanic Mechanic, GuideSignal? Live);

internal static class GuideCombatListPresentation
{
    internal const int PreferredCount = 6;

    internal static GuideCombatListEntry[] Select(GuideCombatFrame frame, IEnumerable<GuideSignal> signals, Class playerClass, bool currentPhaseOnly)
    {
        if (frame.Boss is not { } boss) return [];
        var active = signals.Where(signal => ReferenceEquals(signal.Boss, boss))
            .GroupBy(signal => signal.Mechanic).ToDictionary(group => group.Key, group => group.OrderBy(signal => signal.Until).First());
        var entries = boss.Phases.SelectMany(phase => phase.Mechanics.Select(mechanic =>
            new GuideCombatListEntry(phase, mechanic, active.GetValueOrDefault(mechanic))))
            .DistinctBy(entry => entry.Mechanic).ToArray();
        var live = entries.Where(entry => entry.Live != null).ToArray();
        var reminders = entries.Where(entry => entry.Live == null
            && (!currentPhaseOnly || GuidePhaseSelection.Visible(frame, entry.Phase, entry.Mechanic)))
            .Select(entry => (Entry: entry, Priority: GuideCombatRelevance.Rank(entry.Mechanic, playerClass)))
            .Where(candidate => candidate.Priority > 0)
            .OrderByDescending(candidate => candidate.Priority)
            .Take(Math.Max(0, PreferredCount - live.Length)).Select(candidate => candidate.Entry);
        return [.. live, .. reminders];
    }

}

internal static class GuideCombatRelevance
{
    private static readonly Regex RoutineInstruction = new(
        @"\A(?:(?:the )?boss is vulnerable\s*:\s*)?(?:no action (?:is )?(?:needed|required)|(?:keep|continue) (?:attacking|(?:dealing |doing )?(?:damage|dps))|(?:attack|dps) (?:the )?boss|wait(?: for (?:(?:the )?(?:phase transition|animation|cutscene)(?: to (?:end|finish))?|[\p{L}\p{M}'’]+(?:['’]s)?(?: (?:help|intervention)| to (?:free you|remove [\p{L}\p{M}'’]+))?))?|aucune action (?:nécessaire|requise)|continue (?:d['’]attaquer|à attaquer|le dps)|attends?(?: la fin de (?:l['’]animation|la cinématique|la transition)| l['’](?:aide|intervention) de [\p{L}\p{M}'’]+| [\p{L}\p{M}'’]+)?|weiter angreifen|keine aktion (?:nötig|erforderlich)|warten|攻撃を続ける|待機|待つ|何もしない)[.!。…]*\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    internal static bool IsActionable(GuideMechanic mechanic)
    {
        if (mechanic.Advice is not { } advice) return true;
        if (advice.ContextOnly) return false;
        var instructions = advice.Responses.Select(response => response.Instruction)
            .Where(instruction => !string.IsNullOrWhiteSpace(instruction)).ToArray();
        if (instructions.Length == 0) instructions = [advice.ShortCue.Length > 0 ? advice.ShortCue : advice.Cue];
        return instructions.Any(instruction => GuideNames.Normalize(instruction).TrimEnd('.', '!') is not ("damage boss" or "damage the boss")
            && !RoutineInstruction.IsMatch(GuideNames.Normalize(instruction)));
    }

    internal static int Rank(GuideMechanic mechanic, Class playerClass = Class.None)
        => !IsActionable(mechanic) ? 0 : !GuideRolePresentation.Relevant(mechanic.Advice?.Roles ?? [], playerClass) ? 1 : 2;
}
