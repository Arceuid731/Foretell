using BossMod.Foretell;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class GuideShortCueTests
{
    private const string Detailed = "First cast : Move to the empty lane ; Subsequent casts, two whirlwinds moving : Stand in front of one stationary whirlwind ; First two whirlwinds gone past : Dodge behind them into safe space";
    private const string Source = "Typhoon is the main mechanic of this fight; it requires players to reposition into safe zones to avoid tornadoes that move in straight lines down the length of the arena dealing moderate damage to any players caught in their path.";
    private static readonly GuideResponse[] Branches =
    [
        new("First cast", "Move to the empty lane"),
        new("Subsequent casts, two whirlwinds moving", "Stand in front of one stationary whirlwind"),
        new("First two whirlwinds gone past", "Dodge behind them into safe space")
    ];

    private static GuideMechanic Mechanic(string cue = Detailed, string shortCue = "Move to safe zones to avoid tornadoes", string evidence = Source,
        string scope = "", GuideResponse[]? branches = null)
        => new("Typhoon", evidence, "") { Advice = new(GuideLanguage.English, "Typhoon", cue, Detailed, "cast", "Typhoon", [evidence])
            { ShortCue = shortCue, CueScope = scope, Responses = branches ?? Branches } };

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    internal static void Run()
    {
        var legacy = Mechanic();
        var before = JsonSerializer.Serialize(legacy.Advice);
        Check(GuideListFlow.Instruction(legacy, "") == "Avoid tornadoes", "Real cached conditional cue remains a paragraph.");
        Check(GuideShortCue.Instruction(legacy, "") == "Avoid tornadoes", "Central and list cue selection disagree.");
        Check(JsonSerializer.Serialize(legacy.Advice) == before, "Short presentation mutated cached branches or evidence.");
        var unrelated = Mechanic(shortCue: "Avoid rolling boulders", evidence:
            "Avalanche requires players to change lanes to avoid rolling boulders that cross the platform.");
        Check(GuideShortCue.Instruction(unrelated, "") == "Avoid rolling boulders", "Legacy shortening depends on a particular ability or hazard name.");
        foreach (var unsafeSource in new[]
        {
            "If marked, it requires players to reposition into safe zones to avoid tornadoes.",
            "It requires players to reposition into safe zones to avoid tornadoes only after the knockback.",
            "It does not require players to avoid tornadoes.",
            "It requires players to not move to avoid tornadoes.",
            "It requires players to reposition into safe zones to avoid tornadoes, except healers.",
            "It requires players to reposition into safe zones to avoid tornadoes when the floor is dark.",
            "It requires players to reposition into safe zones to avoid tornadoes, but not during the charge.",
            "It requires players to reposition into safe zones to avoid tornadoeslashes."
        })
            Check(GuideShortCue.Instruction(Mechanic(evidence: unsafeSource), "") == "Watch Typhoon", "Legacy simplification discarded a source condition or matched a partial hazard name.");
        Check(GuideShortCue.Instruction(Mechanic(shortCue: "Move to the empty lane"), "") == "Watch Typhoon", "Legacy short cue selected just the first branch.");
        Check(GuideShortCue.Instruction(Mechanic(shortCue: "Avoid them", evidence: "It requires players to move to avoid them."), "") == "Watch Typhoon",
            "Legacy cue retains an ambiguous pronoun.");
        Check(GuideShortCue.Instruction(Mechanic(evidence: Source + " If marked, stand inside the tornadoes."), "") == "Watch Typhoon",
            "An exception elsewhere in the source became an unconditional avoidance instruction.");
        var conditional = "If marked: spread; otherwise stack";
        Check(GuideShortCue.Instruction(Mechanic(conditional, "Spread", branches: []), "") == conditional,
            "A complete short conditional cue lost its exception.");
        Check(GuideShortCue.Instruction(Mechanic(scope: "shared", shortCue: "Avoid tornadoes"), "") == "Avoid tornadoes", "New shared cue is not selected.");
        Check(GuideShortCue.Instruction(Mechanic(scope: "complete", shortCue: conditional), "") == conditional, "New complete cue lost a branch.");
        var trigger = new GuideTrigger("status", "Scorch", "If affected: move away; otherwise stay clear", ["Scorch affects a player."])
            { Target = "any", ShortCue = "If affected: move away; otherwise stay clear", CueScope = "complete" };
        Check(GuideShortCue.Instruction(legacy, "", trigger) == trigger.ShortCue, "Mechanic-wide cue overwrites a precise event cue.");
        Check(GuideShortCue.Instruction(legacy, "", trigger with { CueScope = "", ShortCue = "Move away" }) == trigger.Cue,
            "Untrusted old trigger short cue removed the recipient condition.");
        foreach (var text in new[] { "", new string('a', 101), "Avoid…", "Avoid...", "Move\naway", string.Join(' ', Enumerable.Repeat("go", 17)) })
            Check(!GuideShortCue.Valid(text), "Invalid short cue contract accepted.");
        AnalysisContract();
        Console.WriteLine("Short guide cues: shared/complete contract, retained details, safe legacy fallback and event-specific instructions passed.");
    }

    private static void AnalysisContract()
    {
        var profile = GuideModelCatalog.Get(GuideModelCatalog.DefaultID);
        var text = "Warden\n" + Source + "\n" + Detailed;
        var source = new GuideDocument(GuideDocument.CurrentSchema, new(11, 12, "Synthetic trial"), "Synthetic trial", 1,
            DateTime.UtcNow, GuideNames.Hash(text), []) { Page = new("Fixture", "https://example.invalid/short-cue", text, "") };
        var draft = JsonSerializer.SerializeToNode(new
        {
            summary = "Avoid tornadoes", bosses = new[] { new
            {
                name = "Warden", displayName = "Warden", summary = "Avoid tornadoes", mechanics = new[] { new
                {
                    name = "Typhoon", displayName = "Typhoon", cue = "Avoid tornadoes", cueScope = "shared", description = Detailed,
                    triggerKind = "cast", triggerName = "Typhoon", evidence = new[] { Source, Detailed }, responses = Branches,
                    triggers = new[] { new GuideTrigger("cast", "Typhoon", "Avoid tornadoes", [Source]) { ShortCue = "Avoid tornadoes", CueScope = "complete" } }
                } }
            } }
        })!.AsObject();
        GuideDocument Parse(JsonObject input) => GuidePageAnalysis.Parse(source, text, input.ToJsonString(), GuideLanguage.English, profile)
            with { Coverage = [new(0, text.Length)] };
        var prepared = Parse(draft);
        var advice = prepared.Bosses[0].Phases[0].Mechanics[0].Advice!;
        Check(advice.CueScope == "shared" && advice.Triggers[0].CueScope == "complete", "Analysis dropped the short cue contract.");
        Check(advice.Cue == Detailed && advice.Description == Detailed && advice.Responses.SequenceEqual(Branches)
            && GuideShortCue.Instruction(prepared.Bosses[0].Phases[0].Mechanics[0], "") == "Avoid tornadoes",
            "Preparing a short cue erased detailed branch conditions or still displays their concatenation.");
        Check(GuidePageAnalysis.ValidPrepared(prepared, source, GuideLanguage.English, profile), "New short cue cache does not roundtrip.");
        foreach (var field in new[] { "cue", "cueScope" })
        {
            var invalid = draft.DeepClone().AsObject();
            invalid["bosses"]![0]!["mechanics"]![0]![field] = field == "cue" ? new string('x', 101) : "first-branch";
            try { Parse(invalid); throw new InvalidOperationException("Invalid mechanic short cue contract accepted."); }
            catch (InvalidDataException) { }
        }
        var badTrigger = draft.DeepClone().AsObject();
        badTrigger["bosses"]![0]!["mechanics"]![0]!["triggers"]![0]!["ShortCue"] = "Avoid...";
        try { Parse(badTrigger); throw new InvalidOperationException("Ellipsis in trigger short cue accepted."); }
        catch (InvalidDataException) { }
        var legacyDraft = draft.DeepClone().AsObject();
        legacyDraft["bosses"]![0]!["mechanics"]![0]!.AsObject().Remove("cueScope");
        legacyDraft["bosses"]![0]!["mechanics"]![0]!["triggers"] = new JsonArray();
        var legacy = Parse(legacyDraft);
        Check(legacy.Bosses[0].Phases[0].Mechanics[0].Advice!.CueScope == ""
            && GuidePageAnalysis.ValidPrepared(legacy, source, GuideLanguage.English, profile), "Legacy cache was promoted to trusted short cue or requires reanalysis.");
    }
}
