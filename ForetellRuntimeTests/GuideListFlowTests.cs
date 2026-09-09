using BossMod;
using BossMod.Foretell;
using System.Numerics;

internal static class GuideListFlowTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static void Run()
    {
        GuideShortCueTests.Run();
        Selection();
        RoleVisibility();
        Actionability();
        ScrollFocus();
        foreach (var count in new[] { 0, 1, 23, 32, 64 })
        foreach (var viewport in new[] { new Vector2(1260, 600), new Vector2(1900, 920) })
        {
            var layout = GuideListFlow.Build(count, 380, 440, viewport, 1.2f,
                (index, width, scale, compact) => (compact ? 36 : index % 7 == 0 ? 110 : 46) * scale);
            Check(layout.Top.Length == count && layout.Column.Length == layout.Top.Length && layout.Heights.Length == layout.Top.Length, "Combat list omitted mechanics");
            Check(layout.Columns == 1 && layout.Width == 380 && layout.Height <= 440.1f, "Reminders spread across the screen or exceed the preferred height");
            Check(layout.Scale == 1.2f && !layout.Compact, "Combat list shrank the requested text or truncated titles");
            for (var index = 0; index < layout.Top.Length; ++index)
            {
                Check(layout.Column[index] >= 0 && layout.Column[index] < layout.Columns && layout.Top[index] >= 0, "List placement is invalid");
                Check(layout.Top[index] + layout.Heights[index] <= layout.ContentHeight + .1f, "Mechanic outside measured list bounds");
                if (index > 0 && layout.Column[index] == layout.Column[index - 1])
                    Check(layout.Top[index] >= layout.Top[index - 1] + layout.Heights[index - 1] - .1f, "Mechanics overlap");
            }
        }
        var repeated = GuideListFlow.Build(15, 380, 440, new(1260, 600), 1, (_, _, _, _) => 48);
        var again = GuideListFlow.Build(15, 380, 440, new(1260, 600), 1, (_, _, _, _) => 48);
        Check(repeated.Column.SequenceEqual(again.Column) && repeated.Top.SequenceEqual(again.Top), "Unchanged layout shifts between frames");
        var tall = GuideListFlow.Build(12, 300, 120, new(300, 120), 1, (_, _, _, _) => 50);
        Check(tall.Top.Length == 12 && tall.Height == 120 && tall.ContentHeight == 600 && tall.Scale == 1, "Short overlay omitted reminders instead of scrolling");
        foreach (var activeCount in new[] { 1, 7, 9, 20 })
        {
            var active = GuideListFlow.Build(23, 380, 120, new(1260, 600), 1.4f, (_, _, _, _) => 80);
            Check(active.Top.Length >= activeCount && active.Scale == 1.4f && !active.Compact, "Active mechanics were capped or shrunk");
            for (var index = 0; index < activeCount; ++index)
                Check(GuideListFlow.ScrollToRow(active, index, 0, active.Height) <= active.Top[index]
                    && GuideListFlow.ScrollToRow(active, index, 0, active.Height) + active.Height >= active.Top[index] + active.Heights[index],
                    "An active row cannot be brought into view");
        }
        var oversized = GuideListFlow.Build(7, 300, 100, new(300, 120), 2, (_, _, _, _) => 200);
        Check(oversized.Top.Length == 7 && oversized.Scale == 2, "A constrained viewport silently drops active mechanics or shrinks their text");
        Check(GuideRolePresentation.Icons(["tank", "TANK", "healer"]).SequenceEqual(new[] { "tank", "healer" }), "Role icon normalization disagrees with role tags");
        Check(GuideRolePresentation.Icons(["all"]).Length == 0, "Universal mechanics display redundant role icons");
        var ability = new GuideMechanic("Example", "", "") { Advice = new(GuideLanguage.English, "Example", "Ability cast: Mitigate", "", "cast", "Example", [])
            { Responses = [new("Ability cast", "Mitigate")], ShortCue = "Mitigate", CueScope = "complete" } };
        Check(GuideListFlow.Instruction(ability, "") == "Mitigate", "Redundant cast wording bloats the quick instruction");
        var conditional = new GuideMechanic("Conditional", "", "") { Advice = new(GuideLanguage.English, "Conditional", "Red: move out; Blue: move in", "", "manual", "", [])
            { ShortCue = "Move out", Responses = [new("Red", "move out"), new("Blue", "move in")] } };
        Check(GuideListFlow.Instruction(conditional, "").Contains("Blue: move in"), "Compact list discarded a conditional alternative");
        var marked = new GuideMechanic("Marked", "", "") { Advice = new(GuideLanguage.English, "Marked", "If marked: move away", "", "status", "Marked", [])
            { ShortCue = "Move away", Responses = [new("If marked", "Move away")] } };
        Check(GuideListFlow.Instruction(marked, "") == "If marked: move away", "Compact instruction lost a player-dependent condition");
        var legacy = new GuideMechanic("Legacy", "", "") { Advice = conditional.Advice! with { Responses = [] } };
        Check(GuideListFlow.Instruction(legacy, "") == conditional.Advice!.Cue, "Legacy short cue discarded a conditional alternative");
        var legacyCast = new GuideMechanic("Legacy cast", "", "") { Advice = ability.Advice! with { Responses = [], CueScope = "" } };
        Check(GuideListFlow.Instruction(legacyCast, "") == ability.Advice!.Cue, "Display rewrites legacy model text");
        Console.WriteLine("Guide list: complete scrollable reminders, active focus, fixed text size, role visibility and conditional instructions passed.");
    }


    private static void ScrollFocus()
    {
        var mechanics = Enumerable.Range(0, 12).Select(index => Mechanic("Mechanic " + index, "Move away")).ToArray();
        var phase = new GuidePhase("", "", mechanics);
        var boss = new GuideBoss("Fixture", "", [phase]);
        var frame = new GuideCombatFrame(boss, false, false, null, [], 0);
        var signal = new GuideSignal(boss, phase, mechanics[^1], GuideSignalKind.Cast, 1, 1, 1, 1, 0, DateTime.UtcNow.AddSeconds(4), GuidanceKind.None, "");
        var focus = new GuideListScrollFocus();
        Check(focus.Update(frame, []).Reset, "A new boss did not reset scroll");
        Check(focus.Update(frame, [signal]).Mechanic == mechanics[^1], "A newly active late row was not focused");
        Check(focus.Update(frame, [signal]).Mechanic == null && !focus.Update(frame, [signal]).Reset, "A live alert overrides manual scrolling every frame");
        Check(focus.Update(frame, [signal with { Until = signal.Until.AddMilliseconds(100) }]).Mechanic == null, "Cast sampling drift resets scrolling");
        var simultaneous = signal with { Mechanic = mechanics[0], ID = 2, Until = signal.Until.AddSeconds(2) };
        Check(focus.Update(frame, [signal, simultaneous]).Mechanic == mechanics[0], "An ongoing cast prevents a newly active mechanic from being focused");
        Check(focus.Update(frame, [simultaneous]).Mechanic == null, "An expiring cast overrides manual scrolling");
        Check(focus.Update(frame, [signal with { Until = signal.Until.AddSeconds(10) }]).Mechanic == mechanics[^1], "A repeated cast no longer brings its mechanic into view");
        Check(focus.Update(frame with { KnownPhase = new("next", "Next", []) }, []).Reset, "A phase change retained stale scroll");
        var layout = GuideListFlow.Build(12, 300, 120, new(500, 500), 1, (_, _, _, _) => 50, 14);
        Check(layout.ColumnWidth == 286 && layout.Width == 300 && layout.ContentHeight == 600, "Scrollbar space obscures row text");
        Check(GuideListFlow.ScrollToRow(layout, 11, 0, 120) == 480 && GuideListFlow.ScrollToRow(layout, 0, 480, 120) == 0, "First or last mechanic is unreachable");
        Check(GuideListFlow.ScrollToRow(layout, 2, 80, 120) == 80, "A visible active row causes unnecessary scrolling");
    }

    private static GuideMechanic Mechanic(string name, string cue, string[]? roles = null, GuideResponse[]? responses = null, string shortCue = "", bool contextOnly = false)
        => new(name, "", "") { Advice = new(GuideLanguage.English, name, cue, "", "manual", "", [])
            { Roles = roles ?? [], Responses = responses ?? [], ShortCue = shortCue, ContextOnly = contextOnly } };

    private static void Selection()
    {
        GuideMechanic[] mechanics = [Mechanic("Vulnerable", "Boss is vulnerable : DPS the boss"), Mechanic("Intermission", "Wait for Hydaelyn..."),
            Mechanic("Tank hit", "Mitigate", ["tank"]), .. Enumerable.Range(3, 20).Select(index => Mechanic("Mechanic " + index, "Move away"))];
        var first = new GuidePhaseDefinition("first", "First", []);
        var second = new GuidePhaseDefinition("second", "Second", []);
        var secondMechanic = new GuideMechanic("Other phase", "", "") { Advice = mechanics[^1].Advice! with { Roles = ["tank"] }, PhaseMemberships = [new("second", [])] };
        mechanics[^1] = secondMechanic;
        GuidePhase[] phases = [new("First", "", mechanics[..12]), new("Second", "", mechanics[12..])];
        var boss = new GuideBoss("Example", "", phases) { PhaseDefinitions = [first, second] };
        var document = new GuideDocument(1, new(1, 1, "Example"), "Example", 1, DateTime.UtcNow, "hash", [boss]);
        var frame = new GuideCombatFrame(boss, false, false, null, [], 0);
        var selection = GuideCombatListPresentation.Select(frame, [], Class.BLM, true);
        Check(selection.Length == 23, "A mechanic was hidden by count, role or actionability");
        Check(selection.Select(entry => entry.Mechanic).SequenceEqual(mechanics), "Equally relevant reminders lost their stable source order");
        Check(GuideCombatListPresentation.Select(frame, [], Class.None, true).Any(entry => entry.Mechanic == mechanics[2]), "Unknown player role hid useful advice");
        var invalidPhase = frame with { KnownPhase = new("missing", "Missing", []) };
        Check(GuideCombatListPresentation.Select(invalidPhase, [], Class.BLM, true).Length == 23, "An invalid phase hides mechanics");
        var knownPhase = frame with { KnownPhase = first };
        var live = new GuideSignal(boss, phases[1], secondMechanic, GuideSignalKind.Cast, 1, 1, 1, 1, 1, DateTime.UtcNow.AddSeconds(4), GuidanceKind.None, "");
        var withActive = GuideCombatListPresentation.Select(knownPhase, [live], Class.BLM, true);
        Check(withActive.Length == 23 && withActive[^1].Live == live, "A late active mechanic disappeared behind role, phase or row limits");
        var scripted = live with { Phase = phases[0], Mechanic = mechanics[0] };
        Check(GuideCombatListPresentation.Select(frame, [scripted], Class.BLM, true)[0].Live == scripted, "Active signals were silently filtered by reminder actionability");
        var manyActive = mechanics.TakeLast(9).Select(mechanic => live with { Mechanic = mechanic }).ToArray();
        var allActive = GuideCombatListPresentation.Select(frame, manyActive, Class.BLM, true);
        Check(allActive.Length == 23 && allActive.Count(entry => entry.Live != null) == 9 && manyActive.All(signal => allActive.Any(entry => entry.Live == signal)),
            "More than six simultaneous active mechanics were capped");
        var duplicate = boss with { Phases = [phases[0], phases[1], phases[0]] };
        Check(GuideCombatListPresentation.Select(frame with { Boss = duplicate }, [], Class.BLM, false).Select(entry => entry.Mechanic).Distinct().Count() == 23,
            "Repeated references duplicate reminders");
        Check(GuideCombatListPresentation.Select(frame, [live with { Boss = duplicate }], Class.BLM, true).All(entry => entry.Live == null), "Another boss's signal activates a row");
        Check(document.MechanicCount == 23 && document.Bosses[0].Phases.SelectMany(phase => phase.Mechanics).SequenceEqual(mechanics), "Combat selection changed the complete Sources catalogue");
    }

    private static void RoleVisibility()
    {
        var mechanics = new[]
        {
            Mechanic("Tank mechanic", "Swap tanks", ["tank"]),
            Mechanic("Bombs", "Move the bombs away", ["tank", "melee", "ranged"]),
            Mechanic("Marked player", "If marked: move away", ["melee", "ranged"]),
            Mechanic("Transformation", "Use the transformation", ["melee", "ranged"]),
            Mechanic("Special action", "Use the special action", ["melee", "ranged"]),
            Mechanic("Ground attack", "Avoid the ground attack", ["melee", "ranged"])
        };
        var phase = new GuidePhase("", "", mechanics);
        var boss = new GuideBoss("Role fixture", "", [phase]);
        var frame = new GuideCombatFrame(boss, true, false, null, [], 0);
        foreach (var player in new[] { Class.SCH, Class.SGE, Class.WHM, Class.AST })
        foreach (var upcoming in new[] { true, false })
        {
            var rows = GuideCombatListPresentation.Select(frame with { Upcoming = upcoming }, [], player, true);
            Check(rows.Length == 6 && rows.Select(row => row.Mechanic).SequenceEqual(mechanics), "Role tags erased an entire healer guide");
            Check(rows.All(row => row.Live == null), "Showing another role's reminder fabricated an active mechanic");
        }
        var healer = Mechanic("Party damage", "Heal and mitigate", ["healer"]);
        var everyone = Mechanic("Safe area", "Move to the safe area");
        var mixedPhase = phase with { Mechanics = [.. mechanics, healer, everyone] };
        var mixedBoss = boss with { Phases = [mixedPhase] };
        var mixedFrame = frame with { Boss = mixedBoss, Upcoming = false };
        var mixed = GuideCombatListPresentation.Select(mixedFrame, [], Class.SCH, true);
        Check(mixed.Length == 8 && mixed.Select(row => row.Mechanic).SequenceEqual(mixedPhase.Mechanics), "Role tags reordered or removed reminders");
        var live = new GuideSignal(mixedBoss, mixedPhase, mechanics[0], GuideSignalKind.Cast, 1, 1, 1, 1, 2, DateTime.UtcNow.AddSeconds(3), GuidanceKind.None, "fixture");
        Check(GuideCombatListPresentation.Select(mixedFrame, [live], Class.SCH, true)[0].Live == live, "Role ranking displaced an active mechanic");
        var later = new GuideMechanic("Later mechanic", "", "") { Advice = mechanics[0].Advice, PhaseMemberships = [new("later", [])] };
        var phased = boss with
        {
            Phases = [phase with { Mechanics = [later, mechanics[1]] }],
            PhaseDefinitions = [new("current", "Current", []), new("later", "Later", [])]
        };
        var filtered = GuideCombatListPresentation.Select(frame with { Boss = phased, Upcoming = false, KnownPhase = phased.PhaseDefinitions[0] }, [], Class.SCH, true);
        Check(filtered.Length == 1 && filtered[0].Mechanic == mechanics[1], "Role fallback exposed another phase's mechanics");
        Check(mechanics[0].Advice!.Roles.SequenceEqual(new[] { "tank" }), "Role ranking rewrote prepared advice");
    }

    private static void Actionability()
    {
        foreach (var cue in new[] { "Boss is vulnerable : DPS the boss", "Wait for Hydaelyn...", "Wait for Hydaelyn to free you", "Wait for Hydaelyn to remove Garuda", "Wait for the phase transition", "Keep attacking", "Continue dealing damage", "No action required", "Attends la fin de l’animation", "Continue d’attaquer" })
            Check(!GuideCombatRelevance.IsActionable(Mechanic("Script", cue)), "Routine cue reaches combat: " + cue);
        foreach (var cue in new[] { "Wait for Hydaelyn, then move out", "Wait for Hydaelyn to free you, then move out", "Wait for Hydaelyn to remove Garuda; dodge the circle", "Wait for the knockback, then move in", "Stop DPS and move away", "Kill the adds", "DPS the boss before the cast finishes", "If marked: move away; otherwise: stack", "Wait outside the circle" })
            Check(GuideCombatRelevance.IsActionable(Mechanic("Action", cue)), "A genuine response was suppressed: " + cue);
        var routine = Mechanic("Script", "Boss is vulnerable: DPS the boss; intermission: wait", responses: [new("Boss is vulnerable", "DPS the boss"), new("Intermission", "Wait for Hydaelyn")], shortCue: "Keep attacking");
        Check(!GuideCombatRelevance.IsActionable(routine), "Response conditions make purely scripted actions look actionable");
        var mixed = Mechanic("Mixed", "Wait", responses: [new("Intermission", "Wait"), new("If marked", "Move away")], shortCue: "DPS the boss");
        Check(GuideCombatRelevance.IsActionable(mixed), "Only the first response or short cue was checked");
        var radiantBlaze = Mechanic("Radiant Blaze", "Boss is vulnerable : DPS the boss", responses: [new("Boss is vulnerable", "DPS the boss")], shortCue: "Ultima Weapon casts Radiant Blaze");
        Check(!GuideCombatRelevance.IsActionable(radiantBlaze), "Standalone cast narration overrides the actual scripted response");
        Check(!GuideCombatRelevance.IsActionable(Mechanic("Script", "Damage boss"))
            && GuideCombatRelevance.IsActionable(Mechanic("Check", "Damage the boss before the cast finishes")), "Routine damage advice confused a timed damage check");
        foreach (var wait in new[] { "Wait for Hydaelyn to free you", "Wait for Hydaelyn to remove Garuda" })
            Check(!GuideCombatRelevance.IsActionable(Mechanic("Intermission", "Intermission: " + wait, responses: [new("Intermission", wait)], shortCue: "The boss becomes invulnerable")),
                "Legacy scripted response is treated as a central action: " + wait);
        Check(GuideCombatRelevance.IsActionable(Mechanic("Short", "", shortCue: "Move away")), "Short cue fallback was ignored when no responses exist");
        Check(!GuideCombatRelevance.IsActionable(Mechanic("Context", "Move away", contextOnly: true)), "Explicit context-only classification was ignored");
        Check(GuideCombatRelevance.IsActionable(new("Unknown", "", "")), "Unclassified mechanics were guessed to be scripted");
        var tank = Mechanic("Tank", "Mitigate", ["tank"]);
        Check(GuideCombatRelevance.Rank(tank, Class.PLD) > GuideCombatRelevance.Rank(tank, Class.BLM)
            && GuideCombatRelevance.Rank(routine, Class.PLD) == 0, "Relevance rank ignores role or scripted context");
    }
}
