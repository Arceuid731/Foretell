using BossMod.Foretell;
using System.Numerics;
using System.Text.Json;

internal static class OverlayDemoTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static void Run()
    {
        var now = new DateTime(2026, 9, 6, 20, 0, 0, DateTimeKind.Utc);
        var demo = new ForetellDemo(now, 10, 123, 42);
        var names = new HashSet<string>();
        for (var index = 0; index < 8; ++index)
        {
            var frame = demo.Frame(now.AddSeconds(index * 6 + .5), 10, 123, new(20, 30), .5f, false, false)!;
            Check(frame != null && frame.Remaining is > 5 and < 6, "Demo countdown did not advance");
            var hazard = frame!.Frame.Hazards.Single();
            Check(ForetellDecisionCore.Valid(hazard.Prediction), "Demo supplied invalid geometry");
            Check(hazard.Prediction.Label.StartsWith("DEMO") && hazard.AdvisoryOnly && !frame.Frame.EvidenceComplete && !frame.Frame.TerrainFresh,
                "Demo can be mistaken for assessed live evidence");
            Check(frame.Guide.Active.Length == 1 && frame.Guide.Boss!.Phases[0].Mechanics.Length == 8, "Demo list not synchronized");
            Check(frame.Guide.KnownPhase != null && frame.Guide.Boss!.Phases[0].Mechanics.Count(mechanic => GuidePhaseSelection.Visible(frame.Guide, frame.Guide.Boss.Phases[0], mechanic)) == 4, "Demo phase filter is not reflected in the mechanic list");
            Check(names.Add(frame.Guide.Active[0].Mechanic.Name), "Demo did not cover all examples before repeating");
            Check(!ForetellDecisionCore.AssessRoute(frame.Frame, Vector2.Zero, Vector2.One, (_, _) => true).Eligible, "Demo enabled a real movement recommendation");
        }
        var repeated = demo.Frame(now.AddSeconds(48.5), 10, 123, Vector2.Zero, 0, false, false)!;
        Check(repeated.Guide.Active[0].Mechanic.Name == demo.Frame(now.AddSeconds(.5), 10, 123, Vector2.Zero, 0, false, false)!.Guide.Active[0].Mechanic.Name, "Demo mechanics do not repeat");
        demo.Next(now.AddSeconds(50));
        Check(demo.Frame(now.AddSeconds(50.5), 10, 123, Vector2.Zero, 0, false, false)!.Remaining == 5.5f, "Next mechanic did not reset countdown");
        var advancing = new ForetellDemo(now, 10, 123, 42);
        var expectedNext = advancing.Frame(now.AddSeconds(12.5), 10, 123, Vector2.Zero, 0, false, false)!.Cue;
        advancing.Next(now.AddSeconds(6.5));
        Check(advancing.Frame(now.AddSeconds(7), 10, 123, Vector2.Zero, 0, false, false)!.Cue == expectedNext, "Next repeated a mechanic after automatic advance");
        Check(demo.Frame(now, 10, 123, Vector2.Zero, 0, true, false) == null, "Demo continues in combat");
        Check(demo.Frame(now, 11, 123, Vector2.Zero, 0, false, false) == null, "Demo crosses territories");
        Check(demo.Frame(now, 10, 456, Vector2.Zero, 0, false, false) == null, "Demo crosses characters");
        Check(demo.Frame(now, 10, 123, Vector2.Zero, 0, false, true) == null, "Demo continues after death");
        Check(demo.Frame(now.AddSeconds(120), 10, 123, Vector2.Zero, 0, false, false) == null, "Demo does not expire");
        Check(demo.Frame(now, 10, 123, new(float.NaN, 0), 0, false, false) == null, "Demo accepts invalid positions");
        Check(ForetellEngine.OverlayScale(float.NaN) == 1 && ForetellEngine.OverlayScale(10) == 4, "Overlay scale is not bounded");
        Check(ForetellEngine.OverlayOpacity(0x80ABCDEF, .5f) == 0x40ABCDEF && ForetellEngine.OverlayOpacity(0x80ABCDEF, float.NaN) == 0x80ABCDEF, "Opacity lost the selected color or alpha");
        var config = new ForetellConfig { GuideAlertScale = 2.2f, CentralAlertColor = 0xFF123456, WorldLabelScale = 1.8f, WorldColor = 0xFF654321 };
        var options = new JsonSerializerOptions { IncludeFields = true, IgnoreReadOnlyProperties = true };
        var saved = JsonSerializer.Serialize(config, options);
        var restored = JsonSerializer.Deserialize<ForetellConfig>(saved, options)!;
        Check(restored.GuideAlertScale == 2.2f && restored.CentralAlertColor == 0xFF123456 && restored.WorldLabelScale == 1.8f && restored.WorldColor == 0xFF654321, "Overlay appearance did not persist");
        Check(!saved.Contains("Demo"), "Demo state is persisted");
        Console.WriteLine("Overlay demo: eight shapes/cues, repeated highlights, combat/zone/expiry guards and appearance persistence passed.");
    }
}
