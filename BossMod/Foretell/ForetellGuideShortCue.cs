using System.Text.RegularExpressions;

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
        if (mechanic.Advice is not { } advice) return fallback;
        if (trigger != null)
        {
            if (ValidScope(trigger.CueScope) && Valid(trigger.ShortCue)) return trigger.ShortCue;
            return Legacy(trigger.Cue, trigger.ShortCue, trigger.Evidence, advice.Language, trigger.Name);
        }
        if (ValidScope(advice.CueScope) && Valid(advice.ShortCue)) return advice.ShortCue;
        var complete = advice.Cue.Length > 0 ? advice.Cue : fallback;
        if (advice.Responses is [{ } response] && CastCondition(response.When)) complete = response.Instruction;
        else if (advice.Responses.Length == 0 && complete.IndexOf(':') is > 0 and var separator && CastCondition(complete[..separator]))
            complete = complete[(separator + 1)..].Trim();
        return Legacy(complete, advice.ShortCue, advice.Evidence, advice.Language, mechanic.Name);
    }

    private static string Legacy(string complete, string candidate, string[] evidence, GuideLanguage language, string name)
    {
        if (Valid(complete)) return complete;
        var neutral = GuidePreparation.Local(language, "Watch " + name, "Observe " + name, "Achte auf " + name, name + "に注意");
        if (!Valid(neutral)) neutral = GuidePreparation.Local(language, "Watch the mechanic's cues", "Observe les indices de la mécanique", "Mechanikhinweise beachten", "ギミックの予兆に注意");
        if (language != GuideLanguage.English || !Valid(candidate)) return neutral;
        var avoidance = Regex.Match(candidate, @"\A(?:Move to safe zones to a|A)void (?<hazard>[\p{L}-]+(?: [\p{L}-]+){0,3})[.!]?\z",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        if (!avoidance.Success) return neutral;
        var hazard = avoidance.Groups["hazard"].Value;
        if (hazard.Split(' ').Any(word => word is "it" or "them" or "this" or "that" or "these" or "those" or "and" or "or")) return neutral;
        if (Regex.IsMatch(string.Join('\n', evidence),
            @"\b(?:(?:not|never)\s+(?:\w+\s+){0,3}avoid|soak|enter|inside|stand in(?! front\b)|(?:move|run) into)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) return neutral;
        var pattern = @"\brequires players to [^.;!?\r\n]*\bto avoid " + Regex.Escape(hazard) + @"(?![\p{L}-])";
        var sentences = evidence.SelectMany(quote => Regex.Split(quote, @"(?<=[.!?])\s+|[\r\n]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)));
        foreach (var sentence in sentences)
        {
            if (Regex.IsMatch(sentence, @"\b(?:if|when|unless|until|only|except|not|never|without|instead|otherwise|before|after|first|second|subsequent|targeted|marked|tank|healer)\b|n't",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) continue;
            if (Regex.IsMatch(sentence, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) return "Avoid " + hazard;
        }
        return neutral;
    }

    private static bool CastCondition(string condition) => GuideNames.Normalize(condition).TrimEnd(':', '.') is
        "cast" or "on cast" or "when cast" or "ability cast" or "telegraphed" or "telegraph" or "damage occurs";
}
