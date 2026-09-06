using BossMod.Foretell;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class GuidePhaseTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly GuideActorState Actor = new(10, 20, 30, "Sentinel", false, true, 5);
    private static readonly string[] Paragraphs =
    [
        "Sentinel", "Opening phase", "Spark: Only during Opening phase, spread out.",
        "Intermission", "Pause: Only during Intermission, stay near the boss.",
        "Final phase", "Nova: Only during Final phase, share the hit.",
        "Pulse: Raidwide damage throughout the fight.",
        "Echo: During Opening phase and Final phase, spread out. It does not occur in Intermission.",
        "Burn: Only during Final phase, players receive Burn and must spread out."
    ];
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static GuideDocument Source() => new(GuideDocument.CurrentSchema, new(321, 654, "Synthetic phase trial"), "Synthetic phase trial", 1, Now,
        GuideNames.Hash(string.Join('\n', Paragraphs)), []) { Page = new("Fixture", "https://example.invalid/guide", string.Join('\n', Paragraphs), "") };
    private static string Draft()
    {
        object Mechanic(string name, int paragraph, string cue, string[] phases, string trigger = "cast") => new
        {
            name, displayName = name, cue, description = Paragraphs[paragraph], triggerKind = trigger, triggerName = name,
            evidence = new[] { Paragraphs[paragraph] }, responses = Array.Empty<GuideResponse>(),
            phaseMemberships = phases.Select(phase => new GuidePhaseMembership(phase, [Paragraphs[paragraph]])).ToArray()
        };
        return JsonSerializer.Serialize(new
        {
            summary = "Watch the boss.", bosses = new[] { new
            {
                name = "Sentinel", displayName = "Sentinel", summary = "Watch the boss.",
                phaseDefinitions = new GuidePhaseDefinition[]
                {
                    new("opening", "Opening phase", [Paragraphs[1]]), new("adds", "Intermission", [Paragraphs[3]]), new("final", "Final phase", [Paragraphs[5]])
                },
                mechanics = new[]
                {
                    Mechanic("Spark", 2, "Spread out", ["opening"]), Mechanic("Pause", 4, "Stay near the boss", ["adds"]),
                    Mechanic("Nova", 6, "Share the hit", ["final"]), Mechanic("Pulse", 7, "Heal the group", []),
                    Mechanic("Echo", 8, "Spread out", ["opening", "final"]), Mechanic("Burn", 9, "Spread out", ["final"], "status")
                }
            } }
        }, Json);
    }
    private static GuideDocument Prepared()
    {
        var source = Source();
        return GuidePageAnalysis.Parse(source, source.Page!.Text, Draft(), GuideLanguage.English, GuideModelCatalog.Get(GuideModelCatalog.DefaultID))
            with { Coverage = [new(0, source.Page.Text.Length)] };
    }
    private static GuideSignal Signal(GuideBoss boss, string name, double seconds, GuideSignalKind kind = GuideSignalKind.Cast)
    {
        var phase = boss.Phases.Single();
        var mechanic = phase.Mechanics.Single(mechanic => mechanic.Name == name);
        return new(boss, phase, mechanic, kind, Actor.ID, Actor.OID, Actor.NameID, (uint)(40 + Array.IndexOf(phase.Mechanics, mechanic)),
            kind == GuideSignalKind.Status ? 1ul : 0ul, Now.AddSeconds(seconds), GuidanceKind.None, "Source-grounded fixture");
    }
    public static void Run()
    {
        MetadataAndCache();
        GroundingRejections();
        ObservedPhases();
        Lifecycle();
    }

    private static void MetadataAndCache()
    {
        var prepared = Prepared();
        var boss = prepared.Bosses.Single();
        var profile = GuideModelCatalog.Get(GuideModelCatalog.DefaultID);
        Check(boss.PhaseDefinitions.Select(phase => phase.ID).SequenceEqual(["opening", "adds", "final"]), "Documented phases lost their source order");
        Check(boss.Phases.Length == 1 && prepared.MechanicCount == 6 && boss.Phases[0].Mechanics.Count(mechanic => mechanic.Name == "Echo") == 1,
            "Shared phases duplicated a canonical mechanic");
        Check(GuidePageAnalysis.ValidPrepared(prepared, Source(), GuideLanguage.English, profile), "Phase metadata failed prepared-cache validation");
        var reloaded = JsonSerializer.Deserialize<GuideDocument>(JsonSerializer.Serialize(prepared))!;
        Check(GuidePageAnalysis.ValidPrepared(reloaded, Source(), GuideLanguage.English, profile)
            && reloaded.Bosses[0].Phases[0].Mechanics[4].PhaseMemberships.Length == 2, "Cached phase memberships were lost or rejected");
        var corruptCue = JsonSerializer.SerializeToNode(prepared)!;
        corruptCue["Bosses"]![0]!["Phases"]![0]!["Mechanics"]![0]!["Advice"]!["ShortCue"] = "Move 999 yalms away";
        Check(!GuidePageAnalysis.ValidPrepared(corruptCue.Deserialize<GuideDocument>()!, Source(), GuideLanguage.English, profile), "An ungrounded cached short cue bypassed validation");
        var legacy = JsonSerializer.SerializeToNode(prepared)!;
        foreach (var cachedBoss in legacy["Bosses"]!.AsArray())
        {
            cachedBoss!.AsObject().Remove("PhaseDefinitions");
            foreach (var phase in cachedBoss["Phases"]!.AsArray())
                foreach (var mechanic in phase!["Mechanics"]!.AsArray())
                {
                    mechanic!.AsObject().Remove("PhaseMemberships");
                    mechanic["Advice"]!.AsObject().Remove("ShortCue");
                }
        }
        var old = legacy.Deserialize<GuideDocument>()!;
        Check(old.Bosses[0].PhaseDefinitions.Length == 0 && old.Bosses[0].Phases[0].Mechanics.All(mechanic => mechanic.PhaseMemberships.Length == 0 && mechanic.Advice!.ShortCue.Length == 0)
            && GuidePageAnalysis.ValidPrepared(old, Source(), GuideLanguage.English, profile), "New optional fields invalidated an existing prepared guide");
        Check(old.ModelRevision == prepared.ModelRevision && old.Schema == prepared.Schema, "Phase metadata changed cache revision or schema");

        var cited = JsonNode.Parse(Draft())!;
        var citedBoss = cited["bosses"]![0]!;
        IEnumerable<JsonNode> EvidenceArrays() => citedBoss["phaseDefinitions"]!.AsArray().Select(phase => phase!["evidence"]!)
            .Concat(citedBoss["mechanics"]!.AsArray().Select(mechanic => mechanic!["evidence"]!))
            .Concat(citedBoss["mechanics"]!.AsArray().SelectMany(mechanic => mechanic!["phaseMemberships"]!.AsArray().Select(membership => membership!["evidence"]!)));
        foreach (var evidence in EvidenceArrays())
            for (var index = 0; index < evidence.AsArray().Count; ++index)
                evidence[index] = (Array.IndexOf(Paragraphs, evidence[index]!.GetValue<string>()) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var resolved = GuidePageAnalysis.ResolveEvidence(cited.ToJsonString(), Paragraphs);
        var checkedDocument = GuidePageAnalysis.Parse(Source(), Source().Page!.Text, resolved, GuideLanguage.English, profile);
        Check(checkedDocument.Bosses[0].PhaseDefinitions[0].Evidence[0] == Paragraphs[1]
            && checkedDocument.Bosses[0].Phases[0].Mechanics[0].PhaseMemberships[0].Evidence[0] == Paragraphs[2], "Phase citation IDs were not restored to exact source evidence");
        citedBoss["phaseDefinitions"]![0]!["evidence"]![0] = "999";
        Reject(() => GuidePageAnalysis.ResolveEvidence(cited.ToJsonString(), Paragraphs), "Out-of-range phase citation accepted");
    }

    private static void GroundingRejections()
    {
        var changes = new Action<JsonNode>[]
        {
            boss => boss["phaseDefinitions"]![0]!["name"] = "Invented phase",
            boss => boss["phaseDefinitions"]![1]!["id"] = "opening",
            boss => boss["phaseDefinitions"]![0]!["evidence"]![0] = "Opening phase never appears in the supplied guide.",
            boss => boss["phaseDefinitions"] = null,
            boss => boss["mechanics"]![0]!["phaseMemberships"]![0]!["phaseID"] = "missing",
            boss => boss["mechanics"]![0]!["phaseMemberships"]![0]!["evidence"]![0] = Paragraphs[1],
            boss => boss["mechanics"]![0]!["phaseMemberships"]![0]!["evidence"]![0] = Paragraphs[7],
            boss => boss["mechanics"]![0]!["phaseMemberships"] = null,
            boss => boss["mechanics"]![0]!["phaseMemberships"]!.AsArray().Add(boss["mechanics"]![0]!["phaseMemberships"]![0]!.DeepClone())
        };
        foreach (var change in changes)
        {
            var draft = JsonNode.Parse(Draft())!;
            change(draft["bosses"]![0]!);
            Reject(() => GuidePageAnalysis.Parse(Source(), Source().Page!.Text, draft.ToJsonString(), GuideLanguage.English,
                GuideModelCatalog.Get(GuideModelCatalog.DefaultID)), "Invalid or ungrounded phase metadata accepted");
        }
        var corrupt = Prepared();
        corrupt = corrupt with { Bosses = [corrupt.Bosses[0] with { PhaseDefinitions = [new("opening", "Invented phase", [Paragraphs[1]])] }] };
        Check(!GuidePageAnalysis.ValidPrepared(corrupt, Source(), GuideLanguage.English, GuideModelCatalog.Get(GuideModelCatalog.DefaultID)), "Corrupt cached phase metadata bypassed source validation");
    }

    private static void ObservedPhases()
    {
        var document = Prepared();
        var boss = document.Bosses[0];
        var phase = boss.Phases[0];
        var tracker = new GuideEncounterTracker();
        tracker.Update(document, [Actor], false);
        Check(tracker.Frame.KnownPhase == null && phase.Mechanics.All(mechanic => GuidePhaseSelection.Visible(tracker.Frame, phase, mechanic)), "Engagement guessed an opening phase");
        foreach (var seconds in new[] { 1, 10, 120, 3600 })
        {
            tracker.Update(document, [Actor], false);
            tracker.Synchronize([Signal(boss, "Pulse", seconds), Signal(boss, "Echo", seconds)]);
            Check(tracker.Frame.KnownPhase == null, "Shared/repeated casts or elapsed time guessed a phase");
        }
        var opening = Signal(boss, "Spark", 5);
        tracker.Synchronize([opening]);
        Check(tracker.Frame.KnownPhase?.ID == "opening", "Exclusive opening evidence did not establish its phase");
        tracker.Resolve(opening);
        tracker.Update(document, [Actor], false);
        tracker.Synchronize([]);
        Check(tracker.Frame.KnownPhase?.ID == "opening", "The phase disappeared between casts");
        Check(!GuidePhaseSelection.Visible(tracker.Frame, phase, phase.Mechanics[2])
            && GuidePhaseSelection.Visible(tracker.Frame, phase, phase.Mechanics[3]) && GuidePhaseSelection.Visible(tracker.Frame, phase, phase.Mechanics[4]),
            "Phase filtering lost common/shared mechanics or retained another phase's exclusive mechanic");
        var match = GuideSynchronization.Match(document, new(document.Duty, Actor.ID, Actor.OID, Actor.NameID, boss.Name, 42, "Nova", Now.AddSeconds(10)), Now);
        Check(match?.Mechanic == phase.Mechanics[2], "Phase filtering prevented matching a transition mechanic");
        var final = Signal(boss, "Nova", 10);
        tracker.Synchronize([opening, final]);
        Check(tracker.Frame.KnownPhase?.ID == "final" && GuidePhaseSelection.Visible(tracker.Frame, phase, opening.Mechanic), "New phase evidence lost to a lingering old cast or hid an active mechanic");
        tracker.Synchronize([opening with { Until = opening.Until.AddMilliseconds(100), TargetID = 99 }]);
        Check(tracker.Frame.KnownPhase?.ID == "final", "Cast-finish sampling drift recounted an old observation");
        tracker.Synchronize([]);
        tracker.Synchronize([opening]);
        Check(tracker.Frame.KnownPhase?.ID == "final", "Replayed old evidence regressed the phase");
        tracker.Synchronize([opening with { Until = Now.AddSeconds(20) }]);
        Check(tracker.Frame.KnownPhase?.ID == "opening" && tracker.Frame.Active.Length == 1, "A new repeated cast was suppressed as a completed mechanic");
        tracker.Synchronize([Signal(boss, "Spark", 30), Signal(boss, "Nova", 30)]);
        Check(tracker.Frame.KnownPhase == null, "Conflicting fresh observations selected a phase");
        tracker.Synchronize([Signal(boss, "Nova", 30)]);
        Check(tracker.Frame.KnownPhase == null, "An already-conflicting observation later became new evidence");
        tracker.Synchronize([Signal(boss, "Pause", 40)]);
        Check(tracker.Frame.KnownPhase?.ID == "adds", "Observed intermission did not establish its phase");
        tracker.Synchronize([Signal(boss, "Echo", 50)]);
        Check(tracker.Frame.KnownPhase == null, "Shared evidence incompatible with the current phase guessed a destination");
        tracker.Synchronize([Signal(boss, "Burn", 60, GuideSignalKind.Status)]);
        Check(tracker.Frame.KnownPhase?.ID == "final", "Boss-owned exclusive status did not identify its phase");
        tracker.Synchronize([Signal(boss, "Spark", 70)]);
        tracker.Synchronize([Signal(boss, "Burn", 60, GuideSignalKind.Status)]);
        Check(tracker.Frame.KnownPhase?.ID == "opening", "A lingering status regressed the known phase");
    }

    private static void Lifecycle()
    {
        var document = Prepared();
        var boss = document.Bosses[0];
        var tracker = new GuideEncounterTracker();
        tracker.Update(document, [Actor], false);
        var signal = Signal(boss, "Nova", 5);
        foreach (var foreign in new[] { signal with { SourceID = 99 }, signal with { SourceOID = 99 }, signal with { OwnerID = 99 }, signal with { Kind = GuideSignalKind.Marker }, signal with { Boss = boss with { Name = "Other" } } })
        {
            tracker.Synchronize([foreign]);
            Check(tracker.Frame.KnownPhase == null, "Unowned, invalid or unrelated evidence established a phase");
        }
        tracker.Synchronize([signal with { SourceID = 12, SourceOID = 22, SourceNameID = 32, OwnerID = Actor.ID }]);
        Check(tracker.Frame.KnownPhase?.ID == "final", "An explicitly boss-owned helper could not establish a phase");
        tracker.Wipe();
        Check(tracker.Frame.KnownPhase == null, "Wipe retained a known phase");
        tracker.Update(document, [Actor], false);
        tracker.Synchronize([signal]);
        Check(tracker.Frame.KnownPhase == null && tracker.Frame.Upcoming, "Stale engaged actor rearmed the wiped phase");
        tracker.Update(document, [Actor with { Engaged = false }], false);
        tracker.Update(document, [Actor], false);
        tracker.Synchronize([signal]);
        tracker.ObserveDeath(Actor.ID, 99, Actor.NameID);
        Check(tracker.Frame.KnownPhase?.ID == "final", "An unrelated death notification reset the boss phase");
        tracker.ObserveDeath(Actor.ID, Actor.OID, Actor.NameID);
        Check(tracker.Frame.KnownPhase == null, "Confirmed boss death retained the phase");
        tracker.Update(document, [Actor with { Dead = true, Engaged = false }], false);
        Check(tracker.Frame.KnownPhase == null && tracker.Frame.CompletedBosses == 1, "Dead boss phase leaked into the next encounter");

        tracker.Reset();
        tracker.Update(document, [Actor], false);
        tracker.Synchronize([signal]);
        tracker.Update(document, [Actor with { Engaged = false }], false);
        Check(tracker.Frame.KnownPhase == null && tracker.Frame.Upcoming, "Combat reset retained a phase without a party-wipe notification");
        tracker.Update(document, [Actor], false);
        tracker.Synchronize([signal]);
        tracker.Update(document, [Actor with { ID = 90 }], false);
        Check(tracker.Frame.KnownPhase == null, "A replacement actor inherited the previous pull's phase");
        tracker.Reset();
        tracker.Update(document, [Actor], false);
        tracker.Synchronize([signal]);
        var other = boss with { Name = "Other" };
        var multi = document with { Bosses = [boss, other] };
        tracker.Update(multi, [Actor, Actor with { ID = 11, EnglishName = "Other" }], false);
        Check(tracker.Frame.Ambiguous && tracker.Frame.KnownPhase == null, "Simultaneous bosses retained a known phase");
        tracker.Update(multi, [Actor], false);
        tracker.Synchronize([signal]);
        Check(tracker.Frame.KnownPhase == null, "An old observation after boss ambiguity guessed the phase");
        tracker.Synchronize([Signal(boss, "Nova", 15)]);
        tracker.Update(multi, [Actor with { ID = 11, EnglishName = "Other" }], false);
        Check(tracker.Frame.Boss == other && tracker.Frame.KnownPhase == null, "Changing bosses retained the previous phase");
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }
}
