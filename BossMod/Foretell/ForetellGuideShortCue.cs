namespace BossMod.Foretell;

internal static class GuideShortCue
{
    internal const int MaximumLength = 100;

    internal static bool Valid(string cue) => !string.IsNullOrWhiteSpace(cue) && cue.Length <= MaximumLength
        && !cue.Any(char.IsControl) && !cue.Contains('…') && !cue.Contains("...", StringComparison.Ordinal)
        && cue.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 16;

    internal static bool ValidScope(string scope) => scope is "complete" or "shared";

    internal static string Instruction(GuideMechanic mechanic, string fallback, GuideTrigger? trigger = null)
    {
        if (mechanic.Advice is not { } advice) return mechanic.Name;
        if (trigger != null)
        {
            if (ValidScope(trigger.CueScope) && Valid(trigger.ShortCue)) return trigger.ShortCue;
            return Legacy(trigger.Cue, trigger.Name);
        }
        if (ValidScope(advice.CueScope) && Valid(advice.ShortCue)) return advice.ShortCue;
        return Legacy(advice.Cue, mechanic.Name);
    }

    private static string Legacy(string complete, string name) => Valid(complete) ? complete : name;
}
