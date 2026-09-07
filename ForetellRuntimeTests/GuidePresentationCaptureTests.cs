using BossMod.Foretell;

internal static class GuidePresentationCaptureTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static void Run()
    {
        var viewport = new GuideDrawBounds(100, 100, 800, 600);
        var row = new GuideDrawBounds(120, 120, 380, 80);
        Check(row.Visibility(viewport, 0xFFFFFFFF) == "Submitted", "Visible row reported hidden");
        Check(row.Visibility(viewport, 0x00FFFFFF) == "Transparent", "Invisible highlight counted as visible");
        Check((row with { Y = 720 }).Visibility(viewport, 0xFFFFFFFF) == "Clipped", "Off-screen row counted as visible");
        Check((row with { Y = 680 }).Visibility(viewport, 0xFFFFFFFF) == "PartiallyClipped", "Partial clipping hidden in diagnostics");
        Check((row with { X = float.NaN }).Visibility(viewport, 0xFFFFFFFF) == "InvalidBounds", "Invalid position counted as visible");
        var at = new DateTime(2026, 9, 7, 20, 0, 0, DateTimeKind.Utc);
        var drawn = new GuideDrawnRow("Boss", "Phase", "Storm", 10, 20, true, "Submitted", row, "Move away");
        var first = new GuidePresentationCapture(at, "run", 1, 2, "Drawn", "NoAlerts", [drawn], []);
        Check(first.SameContent(first with { At = at.AddSeconds(1), Rows = [drawn with { }] }), "Steady frames flood the capture");
        Check(!first.SameContent(first with { Rows = [drawn with { Highlighted = false }] }), "Highlight expiration not recorded");
        Check(!first.SameContent(first with { Rows = [drawn with { Outcome = "Clipped" }] }), "Clipping transition not recorded");
        Check(!first.SameContent(first with { ListState = "WindowHidden" }), "Hidden window not recorded");
        Check(!first.SameContent(first with { TerritoryID = 3 }), "Different duty inherited presentation");
        Check(!first.SameContent(first with { SessionID = "next-run" }), "Another run inherited presentation");
        Check(!first.SameContent(first with { Rows = [drawn with { SourceID = 11 }] }), "Repeated occurrence from another caster lost");
        Check(!first.SameContent(first with { CentralState = "Drawn", Alerts = [new("Move away", "Storm", true, "Submitted", row)] }), "Central alert transition not recorded");
        Console.WriteLine("Guide presentation diagnostics: submitted, clipped, transparent, hidden and repeated highlight transitions passed.");
    }
}
