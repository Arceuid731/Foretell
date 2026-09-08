using BossMod;
using BossMod.Foretell;
using System.Numerics;

internal static class GuidePulseLinkTests
{
    public static void Run()
    {
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        var now = new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);
        var mechanic = new GuideMechanic("Moving field", "", "")
        { Advice = new(GuideLanguage.English, "Moving field", "Keep clear", "", "cast", "Moving field", ["Moving field: keep clear."]) };
        var phase = new GuidePhase("", "", [mechanic]);
        var boss = new GuideBoss("Sentinel", "", [phase]);
        var document = GuideIdPlanTests.Document(boss);
        var catalog = new GuideIdCatalog([new("boss", 10, "Sentinel", []), new("cast", 20, "Moving field", []), new("cast", 21, "Moving field", [])]);
        var plan = new GuideIdPlan(document, catalog);
        var frame = new GuideCombatFrame(boss, false, false, phase, [], 0);
        var owner = new Actor(100, 1000, 0, 0, "", 10, ActorType.Enemy, Class.None, 1, Vector4.Zero, resolveGameMetadata: false);
        var emitter = new Actor(200, 2000, 0, 0, "", 11, ActorType.Enemy, Class.None, 1, new(5, 0, 0, 0), targetable: false, resolveGameMetadata: false);
        Actor? Find(ulong identifier) => identifier == owner.InstanceID ? owner : null;
        var seed = new GuideSignal(boss, phase, mechanic, GuideSignalKind.Cast, owner.InstanceID, owner.OID, owner.NameID, 20, owner.InstanceID,
            now.AddSeconds(3), GuidanceKind.None, "Fixture boss cast");
        var links = new GuidePulseLinks();
        links.Update(document, frame, [], now);
        Check(links.Resolve(plan, frame, emitter, Find, 21, now.AddSeconds(1), now) == null, "Unowned emitter guessed a boss without a matching cast.");
        links.Update(document, frame, [seed], now);
        var signal = links.Resolve(plan, frame, emitter, Find, 21, now.AddSeconds(1), now);
        Check(signal is { Kind: GuideSignalKind.Pulse, RelatedBossID: 100, RelatedCastID: 20 } && signal.Mechanic == mechanic,
            "Repeated official ID did not preserve its recent boss-cast provenance.");
        Check(links.Resolve(plan, frame, emitter, Find, 99, now.AddSeconds(1), now) == null, "Unknown emitter action was guessed.");
        Check(links.Resolve(plan, frame with { Ambiguous = true }, emitter, Find, 21, now.AddSeconds(1), now) == null, "Ambiguous encounter retained a pulse link.");
        Check(links.Resolve(plan, frame, emitter, _ => null, 21, now.AddSeconds(1), now) == null, "Missing boss retained an unowned pulse link.");
        Check(links.Resolve(plan, frame, emitter, Find, 21, now.AddSeconds(4), now) == null, "Unbounded lifetime was accepted.");
        for (var index = 1; index <= 40; ++index)
        {
            var at = now.AddSeconds(index * .6);
            links.Update(document, frame, [], at);
            Check(links.Resolve(plan, frame, emitter, Find, 21, at.AddSeconds(1), at) != null, "Continuous observed pulses lost their provenance.");
        }
        var stopped = now.AddSeconds(30);
        links.Update(document, frame, [], stopped);
        Check(links.Resolve(plan, frame, emitter, Find, 21, stopped.AddSeconds(1), stopped) == null, "Stopped pulses revived an expired boss-cast association.");
        links.Update(document, frame, [seed], now);
        links.Update(document, frame with { Upcoming = true }, [], now);
        Check(links.Resolve(plan, frame, emitter, Find, 21, now.AddSeconds(1), now) == null, "Encounter reset retained old pulse links.");
        Check(GuideCentralPresentation.Select([signal!, signal! with { SourceID = 201 }], 1).Length == 1, "Simultaneous emitters duplicated the central instruction.");
        var prediction = new ActivePrediction(emitter.InstanceID, 21, GeometryKind.Circle, MechanicKind.Unknown, new(5, 0), new(7, 0), 0, 3, 0,
            now.AddSeconds(1), .8f, "Repeated impacts") { CreatedAt = now, MotionUntil = now.AddSeconds(1), Velocity = new(2, 0) };
        var hazard = new DecisionHazard(-100, prediction, now.AddSeconds(1), true, true, "Repeated impacts");
        var display = ForetellDecisionCore.SelectForDisplay(new(now.AddSeconds(.5), [hazard], false, false), .75f, 1);
        Check(display is [{ Origin.X: 6 }], "Radar/3D display did not advance a moving hazard to the frame time.");
        var linked = GuideDecisionBridge.Associate(hazard, signal!, "Moving field", "fixture");
        Check(linked.Prediction.GuideLinked && linked.AdvisoryOnly && linked.Prediction.Guidance == GuidanceKind.None,
            "Pulse linkage lost the guide label or invented spatial guidance.");
        Console.WriteLine("Guide pulse links: official candidates, recent boss provenance, bounded continuation, expiry, reset and central deduplication passed.");
    }
}
