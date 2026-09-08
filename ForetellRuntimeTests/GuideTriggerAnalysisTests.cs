using BossMod.Foretell;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class GuideTriggerAnalysisTests
{
    private static readonly GuideModelProfile Profile = GuideModelCatalog.Get(GuideModelCatalog.DefaultID);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string[] Paragraphs =
    [
        "Sentinel", "Opening phase",
        "Meteor Ring: The cast targets a random player. If targeted and the weapon glows, move behind the boss; otherwise move away. Other players stay near the boss, avoiding dark tiles.",
        "Searing Brand is a debuff on the player. At 3 stacks of Searing Brand, move onto the glowing tile; if it is dark, wait outside.",
        "Sentinel once guarded the library. Keep attacking the boss.",
        "Keeper", "Wave: Everyone takes damage. Heal the group.", "FULL_PAGE_END"
    ];
    private const string SelfCue = "If the weapon glows, move behind the boss; otherwise move away.";
    private const string OtherCue = "Stay near the boss, avoiding dark tiles.";
    private const string StatusCue = "Move onto the glowing tile; if it is dark, wait outside.";
    private static GuideDocument Source(string[]? paragraphs = null)
    {
        var text = string.Join('\n', paragraphs ?? Paragraphs);
        return new(GuideDocument.CurrentSchema, new(951, 952, "Synthetic trigger trial"), "Synthetic trigger trial", 1,
            DateTime.UtcNow, GuideNames.Hash(text), []) { Page = new("Fixture", "https://example.invalid/triggers", text, "") };
    }

    private static GuideTrigger[] Triggers() =>
    [
        new("cast", "Meteor Ring", SelfCue, ["3"]) { Target = "self" },
        new("cast", "Meteor Ring", OtherCue, ["3"]) { Target = "other" },
        new("status", "Searing Brand", StatusCue, ["4"]) { Target = "self", MinimumStacks = 3 }
    ];

    private static JsonObject Draft(GuideTrigger[]? triggers = null) => JsonSerializer.SerializeToNode(new
    {
        summary = "Watch the weapon and tiles.", bosses = new[] { new
        {
            name = "Sentinel", displayName = "Sentinel", summary = "Watch the weapon and tiles.",
            phaseDefinitions = new[] { new GuidePhaseDefinition("opening", "Opening phase", ["2"]) },
            mechanics = new[] { new
            {
                name = "Meteor Ring", displayName = "Meteor Ring", cue = "Watch the weapon and tiles.",
                description = Paragraphs[2] + " " + Paragraphs[3], triggerKind = "cast", triggerName = "Meteor Ring",
                evidence = new[] { "3", "4" }, responses = Array.Empty<GuideResponse>(), roles = Array.Empty<string>(), conflict = "",
                phaseMemberships = new[] { new GuidePhaseMembership("opening", ["2", "3"]) }, contextOnly = false,
                triggers = triggers ?? Triggers()
            } }
        } }
    }, Json)!.AsObject();

    private static JsonNode Mechanic(JsonNode draft) => draft["bosses"]![0]!["mechanics"]![0]!;
    private static GuideAdvice Advice(GuideDocument document) => document.Bosses[0].Phases[0].Mechanics[0].Advice!;
    private static GuideDocument Parse(JsonNode draft, GuideDocument? source = null)
    {
        source ??= Source();
        return GuidePageAnalysis.Parse(source, source.Page!.Text,
            GuidePageAnalysis.ResolveEvidence(draft.ToJsonString(), GuidePageAnalysis.Paragraphs(source.Page.Text)), GuideLanguage.English, Profile)
            with { Coverage = [new(0, source.Page.Text.Length)] };
    }

    private static bool Valid(GuideDocument document, GuideDocument? source = null)
        => GuidePageAnalysis.ValidPrepared(document, source ?? Source(), GuideLanguage.English, Profile);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid automatic trigger was accepted.");
    }

    public static async Task Run()
    {
        VerifyRoundtrip();
        VerifyInvalidEvidence();
        VerifyTargetsAndStacks();
        VerifyConflictsAndLegacy();
        VerifyPhaseRepairs();
        VerifySharedPhaseEvents();
        VerifyTriggerArenaReference();
        await VerifyBatchRepairs();
        Console.WriteLine("Guide trigger analysis: grounded multiple cast/status cues, targets, stacks, legacy caches and repair preservation passed (stub model).");
    }

    private static void VerifyRoundtrip()
    {
        var prepared = Parse(Draft());
        var advice = Advice(prepared);
        Check(advice.Triggers.Select(trigger => trigger.Cue).SequenceEqual([SelfCue, OtherCue, StatusCue]),
            "Distinct event/target cues or their unobservable conditions were rewritten.");
        Check(advice.Triggers[0].Evidence.SequenceEqual([Paragraphs[2]]) && advice.Triggers[2].Evidence.SequenceEqual([Paragraphs[3]])
            && advice.Triggers[2] is { MinimumStacks: 3, Target: "self" }, "Trigger citation resolution lost event provenance or threshold.");
        var serialized = JsonSerializer.Serialize(prepared);
        var restored = JsonSerializer.Deserialize<GuideDocument>(serialized)!;
        Check(Valid(restored) && JsonSerializer.Serialize(Advice(restored).Triggers) == JsonSerializer.Serialize(advice.Triggers),
            "Prepared-cache validation or serialization changed grounded triggers.");
        foreach (var mutation in new Func<GuideTrigger, GuideTrigger>[]
        {
            trigger => trigger with { Evidence = ["Forged evidence"] }, trigger => trigger with { Name = "Invented Cast" },
            trigger => trigger with { Target = "unknown" }, trigger => trigger with { MinimumStacks = 3 }
        })
        {
            var corrupted = ReplaceAdvice(prepared, advice with { Triggers = [mutation(advice.Triggers[0])] });
            Check(!Valid(corrupted), "ValidPrepared skipped trigger validation during its roundtrip.");
        }
        Check(!Valid(ReplaceAdvice(prepared, advice with { Triggers = null! })), "Null cached triggers were accepted.");
    }

    private static GuideDocument ReplaceAdvice(GuideDocument document, GuideAdvice advice)
    {
        var boss = document.Bosses[0];
        var phase = boss.Phases[0];
        var mechanic = phase.Mechanics[0];
        return document with { Bosses = [boss with { Phases = [phase with { Mechanics =
            [new(mechanic.Name, mechanic.Text, mechanic.Anchor) { Advice = advice, PhaseMemberships = mechanic.PhaseMemberships }] }] }] };
    }

    private static void VerifyInvalidEvidence()
    {
        var trigger = Triggers()[0];
        foreach (var citation in new[] { "0", "9", "999999999999", "-1", "+1", "3.0", " 3", "3 ", "٣", "fabricated quote", Paragraphs[2] })
            Reject(() => Parse(Draft([trigger with { Evidence = [citation] }])));
        foreach (var name in new[] { "Invented Cast", "Meteor", "Ring", "meteor ring", "Anneau météorique", "Meteor Ring ", "Ringlet" })
            Reject(() => Parse(Draft([trigger with { Name = name }])));
        Reject(() => Parse(Draft([trigger with { Evidence = [] }])));
        Reject(() => Parse(Draft([trigger with { Evidence = null! }])));
        Reject(() => Parse(Draft([null!])));
        Reject(() => Parse(Draft([trigger with { Evidence = ["7"] }])));
        var story = Draft([trigger with { Evidence = ["5"], Cue = "Keep attacking the boss." }]);
        Mechanic(story)["evidence"] = new JsonArray("3", "4", "5");
        Reject(() => Parse(story));
        foreach (var cue in new[] { "Handle the mechanic", "Follow the strategy", "See above", "Watch the boss", "Keep attacking the boss", "Move behind 7 tiles", "Meteor Ring", "Tankbuster" })
            Reject(() => Parse(Draft([trigger with { Cue = cue }])));
    }

    private static void VerifyTargetsAndStacks()
    {
        var cast = Triggers()[0];
        var status = Triggers()[2];
        foreach (var target in new[] { "", "tank", "player", "Self", "self/other", null! })
            Reject(() => Parse(Draft([cast with { Target = target }])));
        Reject(() => Parse(Draft([status with { Target = "other" }])));
        Reject(() => Parse(Draft([cast, cast with { Target = "any" }])));
        Reject(() => Parse(Draft([cast, cast with { Cue = "Move away from the boss." }])));
        Reject(() => Parse(Draft([status, status with { Target = "any" }])));
        Reject(() => Parse(Draft([cast with { Kind = "manual" }])));
        foreach (var text in new[]
        {
            "Meteor Ring: Heavy damage. Move away.",
            "Meteor Ring: A marker targets a random player. Move away.",
            "Meteor Ring: Everyone takes damage. Another cast targets a random player.",
            "Meteor Ring: Everyone takes damage; another cast targets a random player.",
            "Meteor Ring: The cast targets the boss. Move away."
        })
        {
            var paragraphs = Paragraphs.ToArray();
            paragraphs[2] = text;
            Reject(() => Parse(Draft([cast]), Source(paragraphs)));
            Check(Advice(Parse(Draft([cast with { Target = "any", Cue = "If targeted, move away." }]), Source(paragraphs))).Triggers[0].Target == "any",
                "An unspecified target lost its conditional any-target cue.");
        }
        foreach (var count in new[] { -1, 1, 2, 4, 255, 256 })
            Reject(() => Parse(Draft([status with { MinimumStacks = count }])));
        Reject(() => Parse(Draft([cast with { MinimumStacks = 1 }])));
        foreach (var target in new[] { "any", "self" })
        {
            var cue = target == "any" ? "If affected, move onto the glowing tile; if it is dark, wait outside." : StatusCue;
            var prepared = Parse(Draft([status with { Target = target, MinimumStacks = 0, Cue = cue }]));
            Check(Valid(prepared) && Advice(prepared).Triggers[0].Cue == cue, "Status recipient scope or conditional any-recipient cue was lost.");
        }
        foreach (var text in new[]
        {
            "Searing Brand is a debuff lasting 3 seconds. Move onto the glowing tile.",
            "Searing Brand is a debuff. At 3 stacks of Another Brand, move onto the glowing tile.",
            "Searing Brand: At 3 seconds, move onto the glowing tile."
        })
        {
            var paragraphs = Paragraphs.ToArray();
            paragraphs[3] = text;
            Reject(() => Parse(Draft([status]), Source(paragraphs)));
        }
        foreach (var count in new[] { 1, 255 })
        {
            var paragraphs = Paragraphs.ToArray();
            paragraphs[3] += $" At {count} stacks of Searing Brand, move onto the glowing tile.";
            Check(Valid(Parse(Draft([status with { MinimumStacks = count }]), Source(paragraphs)), Source(paragraphs)),
                "An explicit representable status threshold was rejected.");
        }
    }

    private static void VerifyConflictsAndLegacy()
    {
        var draft = Draft();
        Mechanic(draft)["conflict"] = "Sources disagree about the safe tile.";
        var conflicting = Parse(draft);
        Check(Advice(conflicting) is { TriggerKind: "manual", TriggerName: "", Triggers.Length: 0 } && Valid(conflicting),
            "Conflicting evidence retained automatic triggers.");
        Check(!Valid(ReplaceAdvice(conflicting, Advice(conflicting) with { Triggers = Advice(Parse(Draft())).Triggers })),
            "Cached conflicts retained automatic triggers through validation.");
        draft = Draft();
        Mechanic(draft)["contextOnly"] = true;
        var context = Parse(draft);
        Check(Advice(context) is { ContextOnly: true, TriggerKind: "manual", Triggers.Length: 0 } && Valid(context),
            "Context-only notes retained automatic triggers.");
        draft = Draft();
        Mechanic(draft).AsObject().Remove("triggers");
        var legacy = Parse(draft);
        Check(Advice(legacy) is { TriggerKind: "cast", TriggerName: "Meteor Ring", Triggers.Length: 0 } && Valid(legacy),
            "Legacy stub response without triggers was invalidated.");
        var cache = JsonSerializer.SerializeToNode(legacy)!;
        cache["Bosses"]![0]!["Phases"]![0]!["Mechanics"]![0]!["Advice"]!.AsObject().Remove("Triggers");
        Check(Valid(cache.Deserialize<GuideDocument>()!), "Legacy cache without triggers requires reanalysis.");
        Check(Valid(Parse(Draft([]))), "Explicit empty triggers invalidate legacy advice.");
    }

    private static void VerifyPhaseRepairs()
    {
        var draft = Draft();
        Mechanic(draft)["phaseMemberships"]![0]!["phaseID"] = "missing";
        var warnings = new List<string>();
        var checkedOutput = GuidePageAnalysis.PreparePhaseMetadata(draft.ToJsonString(), Paragraphs, warnings.Add);
        var prepared = Parse(JsonNode.Parse(checkedOutput)!);
        Check(warnings.Count == 1 && prepared.Bosses[0].PhaseDefinitions.Length == 0 && Advice(prepared).Triggers.Length == 3,
            "Phase-metadata fallback dropped triggers.");
        var repaired = GuidePageAnalysis.ApplyPhasePlan(prepared, Source().Page!.Text,
            """{"phases":[{"name":"Opening phase","evidence":"2","mechanics":["1"]}]}""");
        Check(ReferenceEquals(Advice(prepared), Advice(repaired)) && Valid(repaired), "Phase-only repair rewrote triggers or failed cache validation.");
    }

    private static void VerifySharedPhaseEvents()
    {
        var paragraphs = Paragraphs.Concat(["Final phase", "Nova: During Final phase, Meteor Ring targets a random player. If targeted, move onto the glowing tile."]).ToArray();
        var source = Source(paragraphs);
        var draft = Draft([Triggers()[0]]);
        var bossNode = draft["bosses"]![0]!;
        bossNode["phaseDefinitions"]!.AsArray().Add(JsonSerializer.SerializeToNode(new GuidePhaseDefinition("final", "Final phase", ["9"]), Json));
        var second = Mechanic(draft).DeepClone();
        second["name"] = "Nova";
        second["displayName"] = "Nova";
        second["cue"] = "Move onto the glowing tile.";
        second["description"] = paragraphs[9];
        second["triggerKind"] = "manual";
        second["triggerName"] = "";
        second["evidence"] = new JsonArray("10");
        second["triggers"] = JsonSerializer.SerializeToNode(new[] { Triggers()[0] with { Cue = "Move onto the glowing tile.", Evidence = ["10"] } }, Json);
        second["phaseMemberships"] = JsonSerializer.SerializeToNode(new[] { new GuidePhaseMembership("final", ["9", "10"]) }, Json);
        bossNode["mechanics"]!.AsArray().Add(second);
        var prepared = Parse(draft, source);
        Check(Valid(prepared, source), "A shared event across distinct phase mechanics invalidated the analysis or cache.");
        var boss = prepared.Bosses[0];
        for (var index = 0; index < boss.PhaseDefinitions.Length; ++index)
        {
            var resolution = GuideEventMatching.Resolve(boss, boss.PhaseDefinitions[index], "cast", "Meteor Ring", 10, 10, true, 0);
            Check(ReferenceEquals(resolution.Mechanic, boss.Phases[0].Mechanics[index])
                && resolution.Trigger?.Cue == (index == 0 ? SelfCue : "Move onto the glowing tile."),
                "A shared event lost its phase-specific mechanic or cue.");
        }
        Check(GuideEventMatching.Resolve(boss, null, "cast", "Meteor Ring", 10, 10, true, 0).Reason == "AmbiguousTrigger",
            "A shared phase event guessed an instruction while its phase was unknown.");
    }

    private static void VerifyTriggerArenaReference()
    {
        const string cue = "If the weapon glows, leave the arena; otherwise move away.";
        var trigger = Triggers()[0] with { Cue = cue };
        var paragraphs = Paragraphs.ToArray();
        paragraphs[2] += " Leave the damaging field on the edge of the arena if the weapon glows.";
        var source = Source(paragraphs);
        var prepared = Parse(Draft([trigger]), source);
        var grounded = Advice(prepared).Triggers[0];
        Check(grounded.Cue == "If the weapon glows, Leave the damaging field; otherwise move away."
            && grounded.Target == trigger.Target && grounded.Evidence.SequenceEqual([paragraphs[2]]) && Valid(prepared, source),
            "Trigger arena correction lost its event evidence, target, conditions or cache roundtrip.");
        Reject(() => Parse(Draft([trigger])));
        paragraphs[2] = Paragraphs[2] + " If the weapon glows, leave the arena; otherwise move away.";
        source = Source(paragraphs);
        Check(Advice(Parse(Draft([trigger]), source)).Triggers[0].Cue == cue, "Explicitly documented arena exit was rewritten.");
        paragraphs[2] = Paragraphs[2];
        paragraphs[3] += " Leave the damaging field on the edge of the arena.";
        Reject(() => Parse(Draft([trigger]), Source(paragraphs)));
    }

    private static async Task VerifyBatchRepairs()
    {
        foreach (var repair in new[] { false, true })
        {
            using var model = new TriggerModel(repair);
            var partials = new List<GuideDocument>();
            var prepared = await GuidePageAnalysis.Compile(Source(), GuideLanguage.English, Profile, 32768, model,
                (_, _) => { }, CancellationToken.None, partials.Add);
            Check(model.Calls == (repair ? 3 : 2) && model.Reviews == (repair ? 1 : 0) && prepared.Bosses.Length == 2 && Valid(prepared),
                "Batch trigger validation repeated analysis or failed isolated repair.");
            Check(partials.Count == 2 && ReferenceEquals(Advice(partials[0]), Advice(prepared))
                && Advice(prepared).Triggers.Select(trigger => trigger.Cue).SequenceEqual([SelfCue, OtherCue, StatusCue]),
                "Batch splitting, repair or partial publication lost event-specific cues.");
        }
    }

    private sealed class TriggerModel(bool repair) : IGuideSummaryModel
    {
        public int Calls;
        public int Reviews;
        public GuideModelRuntime Runtime => new();
        public Task Start(Action<GuideModelProgress> progress, CancellationToken cancellation) => throw new InvalidOperationException("No inference allowed.");
        public Task<string> Summarize(string source, GuideLanguage language, CancellationToken cancellation) => throw new InvalidOperationException("No legacy source cutting allowed.");
        public Task<string> Analyze(string system, string source, object schema, int outputTokens, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            ++Calls;
            Check(source.Contains("<source>\n" + string.Join('\n', Paragraphs.Select((paragraph, index) => $"[{index + 1}] {paragraph}")) + "\n</source>", StringComparison.Ordinal),
                "Analysis or repair did not receive the untouched full page.");
            if (system.StartsWith("Read the ENTIRE", StringComparison.Ordinal))
                return Task.FromResult("""{"bosses":[{"name":"Sentinel","passages":["1","2","3","4"]},{"name":"Keeper","passages":["6","7"]}]}""");
            Check(system.Contains("minimumStacks", StringComparison.Ordinal) && system.Contains("unobservable conditions", StringComparison.Ordinal),
                "Draft or review prompt omitted the trigger grounding contract.");
            var schemaNode = JsonSerializer.SerializeToNode(schema)!;
            var mechanicSchema = schemaNode["properties"]!["bosses"]!["items"]!["properties"]!["mechanics"]!["items"]!;
            Check(mechanicSchema["required"]!.AsArray().Any(field => field!.GetValue<string>() == "triggers"), "New response schema omits triggers.");
            GuideBatchAnalysisTests.VerifyCompactCitation(mechanicSchema["properties"]!["triggers"]!["items"]!["properties"]!["evidence"]!["items"]!);
            var response = Draft();
            if (system.StartsWith("Audit this", StringComparison.Ordinal))
                ++Reviews;
            else
            {
                if (repair) Mechanic(response)["triggers"]![2]!["evidence"] = new JsonArray("999");
                response["bosses"]!.AsArray().Add(JsonSerializer.SerializeToNode(new
                {
                    name = "Keeper", displayName = "Keeper", summary = "Prepare healing.", mechanics = new[] { new
                    {
                        name = "Wave", displayName = "Wave", cue = "Heal the group", description = "Everyone takes damage.",
                        triggerKind = "cast", triggerName = "Wave", evidence = new[] { "7" }, responses = Array.Empty<GuideResponse>()
                    } }
                }));
            }
            return Task.FromResult(response.ToJsonString());
        }
        public void Dispose() { }
    }
}
