using BossMod.Foretell;
using System.Numerics;

internal static class GuideListFlowTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static void Run()
    {
        foreach (var count in new[] { 0, 1, 15, 32, 64 })
        foreach (var viewport in new[] { new Vector2(1260, 600), new Vector2(1900, 920) })
        {
            var layout = GuideListFlow.Build(count, 380, 440, viewport, 1.2f,
                (index, width, scale, compact) => (compact ? 36 : index % 7 == 0 ? 110 : 46) * scale);
            Check(layout.Column.Length == count && layout.Top.Length == count && layout.Heights.Length == count, "List lost mechanics while arranging columns");
            Check(layout.Width <= viewport.X + .1f && layout.Height <= viewport.Y + .1f, "List requires scrolling in a normal viewport");
            for (var index = 0; index < count; ++index)
            {
                Check(layout.Column[index] >= 0 && layout.Column[index] < layout.Columns && layout.Top[index] >= 0, "List placement is invalid");
                Check(layout.Top[index] + layout.Heights[index] <= layout.Height + .1f, "Mechanic outside measured list bounds");
                if (index > 0 && layout.Column[index] == layout.Column[index - 1])
                    Check(layout.Top[index] >= layout.Top[index - 1] + layout.Heights[index - 1] - .1f, "Mechanics overlap");
            }
        }
        var repeated = GuideListFlow.Build(15, 380, 440, new(1260, 600), 1, (_, _, _, _) => 48);
        var again = GuideListFlow.Build(15, 380, 440, new(1260, 600), 1, (_, _, _, _) => 48);
        Check(repeated.Column.SequenceEqual(again.Column) && repeated.Top.SequenceEqual(again.Top), "Unchanged layout shifts between frames");
        var tall = GuideListFlow.Build(12, 300, 120, new(300, 120), 1, (_, _, _, _) => 50);
        var maximum = tall.Height - 120;
        Check(GuideListFlow.Offset(tall, 120, 0) == 0 && Math.Abs(GuideListFlow.Offset(tall, 120, 3 + maximum / 12) - maximum) < .1f,
            "Overflow content cannot be read without mouse navigation");
        var lastOffset = GuideListFlow.Offset(tall, 120, 0, 11);
        Check(tall.Top[11] + tall.Heights[11] - lastOffset <= 120 && tall.Top[11] - lastOffset >= 0, "Last active row remains clipped in a short overlay");
        Check(GuideRolePresentation.Icons(["tank", "TANK", "healer"]).SequenceEqual(new[] { "tank", "healer" }), "Role icon normalization disagrees with role tags");
        Check(GuideRolePresentation.Icons(["all"]).Length == 0, "Universal mechanics display redundant role icons");
        var ability = new GuideMechanic("Example", "", "") { Advice = new(GuideLanguage.English, "Example", "Ability cast: Mitigate", "", "cast", "Example", [])
            { Responses = [new("Ability cast", "Mitigate")] } };
        Check(GuideListFlow.Instruction(ability, "") == "Mitigate", "Redundant cast wording bloats the quick instruction");
        var conditional = new GuideMechanic("Conditional", "", "") { Advice = new(GuideLanguage.English, "Conditional", "Red: move out; Blue: move in", "", "manual", "", [])
            { ShortCue = "Move out", Responses = [new("Red", "move out"), new("Blue", "move in")] } };
        Check(GuideListFlow.Instruction(conditional, "").Contains("Blue: move in"), "Compact list discarded a conditional alternative");
        Console.WriteLine("Controller-friendly guide list: all rows, adaptive columns, role icons, stable layout and conditional instructions passed.");
    }
}
