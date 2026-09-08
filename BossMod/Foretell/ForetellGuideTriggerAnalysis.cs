using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace BossMod.Foretell;

internal static partial class GuidePageAnalysis
{
    private static object TriggerSchema => new
    {
        type = "array", maxItems = 16, items = new
        {
            type = "object", additionalProperties = false, required = new[] { "kind", "name", "cue", "evidence", "target", "minimumStacks" },
            properties = new
            {
                kind = new { type = "string", @enum = new[] { "cast", "status" } }, name = Text(120), cue = Text(600),
                evidence = new { type = "array", minItems = 1, maxItems = 8, items = Text(2400) },
                target = new { type = "string", @enum = new[] { "any", "self", "other" } },
                minimumStacks = new { type = "integer", minimum = 0, maximum = 255 }
            }
        }
    };

    private const string TriggerPrompt = """

        triggers is a separate array of independently grounded automatic events for this mechanic; use [] when none are safe. Keep the legacy triggerKind/triggerName fields. Each trigger is {kind, name, cue, evidence, target, minimumStacks}. kind is ONLY cast or status. name is the EXACT original named cast/status from its own evidence, including spelling and punctuation, never a translated name, category, boss name, inferred ability or invented ID. Multiple named events may have DIFFERENT cues, including a cast followed by a player status. Do not copy the mechanic's general cue to every event. Keep distinct named mechanics distinct.
        Each trigger's evidence contains valid paragraph IDs supporting THAT exact event, target and response, also included in the mechanic evidence. A boss story or another ability's instructions cannot justify the trigger. cue must stand alone as the complete player response to THAT event and target, in the requested language, up to 600 characters when conditions need space. Never use generic boss strategy, 'handle the mechanic', 'do the appropriate response', or pointers such as 'as above'. If the source supplies no specific response, omit the trigger.
        target is any by default. For casts, self/other is allowed ONLY when the source explicitly identifies the cast's actual party-member target, and the response is appropriate when that known cast target is respectively the local player or another party member. Damage recipients, AoE victims, visual markers, tethers, enmity guesses and words in an ability name do not establish a cast target. Runtime can distinguish self/other ONLY with a known party-member cast target. With an unspecified cast target use any, retaining any target-dependent conditions in cue. Never infer a target by stripping 'if targeted' from prose.
        Status events observe party status recipients. target self requires the local player to have that status; any can match another party member too. target other is unsupported for statuses. Use self for instructions only the status recipient should perform. An any-status cue must be appropriate for the local player regardless of which party member received it, preserving recipient-dependent alternatives such as 'If affected: move away; otherwise stay clear'. Never give the observer an unconditional instruction intended only for the recipient. minimumStacks is 0 unless this status's source explicitly states a stack threshold for THAT response. Only thresholds 1..255 are representable (runtime reads the status low byte); casts always use 0. A duration, damage value, phase number or another status's stacks cannot justify a threshold. Preserve 'exactly', 'until', timing and other limitations in cue; minimumStacks only tests a lower bound.
        Preserve ALL unobservable conditions in each cue: phase, weapon appearance, position, role, timing, other statuses and alternatives. Only the observed event, a supported self/other cast target, a status self-recipient restriction and a supported minimum-stack threshold can be resolved automatically. Never automatically strip conditional prose or choose one unobservable branch. If the complete response cannot be stated safely, use []. Conflicting evidence disables every trigger: conflict must explain it, triggerKind must be manual, triggerName empty and triggers=[]. contextOnly also requires triggers=[] and a manual legacy trigger. Within one mechanic, never emit overlapping triggers for the same event/target with different instructions; retain their conditional alternatives in one cue. Distinct mechanics may cite the same event in different documented phases: preserve their evidence and phase memberships so runtime can select by a known phase or decline an ambiguous match.
        """;

    private static GuideTrigger[] ValidateTriggers(Mechanic mechanic, string passage, GuideLanguage language)
    {
        if (mechanic.Triggers == null || mechanic.Triggers.Length > 16)
            throw new InvalidDataException("Invalid trigger array. Use [] when automatic events are unsupported.");
        foreach (var trigger in mechanic.Triggers)
        {
            if (trigger == null || trigger.Kind is not ("cast" or "status") || !ValidText(trigger.Name, 120, true)
                || trigger.Name != trigger.Name.Trim() || !ValidText(trigger.Cue, 600, true)
                || trigger.Target is not ("any" or "self" or "other") || trigger.MinimumStacks is < 0 or > 255
                || trigger.Kind == "cast" && trigger.MinimumStacks != 0 || trigger.Kind == "status" && trigger.Target == "other"
                || !GroundedEvidence(trigger.Evidence, passage))
                throw new InvalidDataException("Invalid cast/status trigger fields, target, stack threshold or source evidence.");
            if (trigger.Evidence.Any(quote => !mechanic.Evidence.Any(parent =>
                GuideSourceAssembly.NormalizeEvidence(parent).Contains(GuideSourceAssembly.NormalizeEvidence(quote), StringComparison.Ordinal))))
                throw new InvalidDataException("Trigger evidence must also support its containing mechanic.");
            var namedEvidence = trigger.Evidence.Where(quote => ExactTriggerName(quote, trigger.Name)).ToArray();
            if (namedEvidence.Length == 0)
                throw new InvalidDataException("Trigger name must be the exact original name in its own cited source, not another event or a paraphrase.");
            var evidence = string.Join('\n', trigger.Evidence);
            if (!GuideSummaryValidation.Accept(trigger.Cue, evidence) || GenericTriggerCue(trigger.Cue)
                || GuideNames.Normalize(trigger.Cue).TrimEnd('.', '!') == GuideNames.Normalize(trigger.Name)
                || language == GuideLanguage.French && !FrenchCue(trigger.Cue))
                throw new InvalidDataException("Trigger cue must give a specific, self-contained response grounded in that event's evidence.");
            var eventSentences = namedEvidence.SelectMany(quote => Regex.Split(quote, @"(?<=[.!?;])\s+|[\r\n]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
                .Where(sentence => ExactTriggerName(sentence, trigger.Name)).ToArray();
            if (trigger.Kind == "cast" && trigger.Target != "any" && !eventSentences.Any(sentence => ExplicitPartyCastTarget(sentence.Replace(trigger.Name, "", StringComparison.Ordinal))))
                throw new InvalidDataException("Self/other requires an explicitly documented party-member cast target, not an affected player or marker.");
            if (trigger.MinimumStacks > 0 && !eventSentences.Any(sentence => ExplicitStackThreshold(sentence, trigger)))
                throw new InvalidDataException("Status stack threshold requires explicit source evidence for that status and count.");
        }
        if (mechanic.Conflict.Length > 0 || mechanic.ContextOnly) return [];
        ValidateTriggerAmbiguity(mechanic.Triggers);
        var triggers = mechanic.Triggers.Select(trigger => trigger with { Cue = GroundArenaReference(trigger.Cue, string.Join('\n', trigger.Evidence)) }).ToArray();
        if (triggers.Any(trigger => !ValidText(trigger.Cue, 600, true)))
            throw new InvalidDataException("Shorten the grounded trigger cue to 600 characters without losing its conditions.");
        return triggers;
    }

    private static bool ExactTriggerName(string evidence, string name) => Regex.Matches(evidence,
        @"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?![\p{L}\p{N}]|[ \t]+\p{Lu}[\p{L}\p{N}])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
        .Any(match => !Regex.IsMatch(evidence[..match.Index], @"\b(?!(?:The|A|An)\b)\p{Lu}[\p{L}\p{N}'-]*[ \t]+$",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)));

    private static bool TriggerPattern(string text, string pattern) => Regex.IsMatch(text, pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    private static bool ExplicitPartyCastTarget(string sentence) => !TriggerPattern(sentence, @"\b(?:marker|tether|not|never|untargeted)\b")
        && TriggerPattern(sentence, @"\b(?:targets?|targeting|cast (?:on|at)|aimed at)\s+(?:(?:a|the|one|random|single|main|off|current|primary)\s+)*(?:player|party member|tank|healer)\b");

    private static bool ExplicitStackThreshold(string sentence, GuideTrigger trigger)
    {
        var count = trigger.MinimumStacks.ToString(CultureInfo.InvariantCulture);
        var name = Regex.Escape(trigger.Name);
        return TriggerPattern(sentence, @"\b(?:at|with|reach(?:es|ing)?|gain(?:s|ing)?)\s+" + count + @"(?:\s+or more)?\s+stacks?\s+of\s+[""']?" + name + @"(?![\p{L}\p{N}])")
            || TriggerPattern(sentence, @"(?<![\p{L}\p{N}])" + name + @"[""']?(?:\s+(?:debuff|status))?\s*[:,;-]?\s+(?:at|with|reach(?:es|ing)?)\s+" + count + @"(?:\s+or more)?\s+stacks?\b");
    }

    private static bool GenericTriggerCue(string cue) => TriggerPattern(cue,
        @"\b(?:see (?:above|below|source)|as (?:above|before)|same as|handle (?:it|the mechanic)|(?:do|use|perform) (?:the )?(?:correct|appropriate|usual) (?:action|response|mechanic)|follow (?:the )?(?:guide|strategy|instructions)|keep attacking|wait for (?:the )?next)\b")
        || GuideNames.Normalize(cue).TrimEnd('.', '!') is "be careful" or "stay alert" or "watch the boss" or "react accordingly" or "dodge" or "avoid the attack"
            or "tankbuster" or "raidwide" or "stack marker";

    private static void ValidateTriggerAmbiguity(GuideTrigger[] triggers)
    {
        for (var first = 0; first < triggers.Length; ++first)
            for (var second = first + 1; second < triggers.Length; ++second)
            {
                var left = triggers[first];
                var right = triggers[second];
                if (left.Kind == right.Kind && GuideNames.Normalize(left.Name) == GuideNames.Normalize(right.Name)
                    && (left.Kind == "status" || left.Target == right.Target || left.Target == "any" || right.Target == "any"))
                    throw new InvalidDataException("Overlapping triggers for the same event and target. Preserve alternatives in one self-contained cue or mark the conflict manual.");
            }
    }

    private static bool SameTriggers(GuideTrigger[] saved, GuideTrigger[] validated) => saved.Length == validated.Length
        && saved.Zip(validated).All(pair => pair.First.Kind == pair.Second.Kind && pair.First.Name == pair.Second.Name
            && pair.First.Cue == pair.Second.Cue && pair.First.Target == pair.Second.Target && pair.First.MinimumStacks == pair.Second.MinimumStacks
            && pair.First.Evidence.SequenceEqual(pair.Second.Evidence));
}
