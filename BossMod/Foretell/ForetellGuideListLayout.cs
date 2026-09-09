namespace BossMod.Foretell;

internal sealed record GuideListLayout(float Scale, float ColumnWidth, float Width, float Height, float ContentHeight, int Columns, int[] Column, float[] Top, float[] Heights, bool Compact);

internal static class GuideListFlow
{
    internal const float Gap = 16;

    internal static GuideListLayout Build(int count, float preferredWidth, float preferredHeight, Vector2 available, float requestedScale,
        Func<int, float, float, bool, float> measure, float scrollbarWidth = 0)
    {
        count = Math.Clamp(count, 0, 512);
        available = new(float.IsFinite(available.X) ? Math.Max(1, available.X) : 800, float.IsFinite(available.Y) ? Math.Max(1, available.Y) : 600);
        preferredWidth = float.IsFinite(preferredWidth) ? Math.Clamp(preferredWidth, Math.Min(240, available.X), available.X) : Math.Min(380, available.X);
        preferredHeight = float.IsFinite(preferredHeight) ? Math.Clamp(preferredHeight, 1, available.Y) : available.Y;
        requestedScale = float.IsFinite(requestedScale) ? Math.Clamp(requestedScale, .7f, 2) : 1;
        var width = preferredWidth;
        float[] Measure() => Enumerable.Range(0, count).Select(index => Math.Max(1, measure(index, width, requestedScale, false))).ToArray();
        var heights = Measure();
        if (heights.Sum() > preferredHeight && float.IsFinite(scrollbarWidth) && scrollbarWidth > 0)
        {
            width = Math.Max(1, width - scrollbarWidth);
            heights = Measure();
        }
        var top = new float[count];
        var total = 0f;
        for (var index = 0; index < count; ++index)
        {
            top[index] = total;
            total += heights[index];
        }
        return new(requestedScale, width, preferredWidth, Math.Max(1, Math.Min(MathF.Ceiling(total), preferredHeight)), Math.Max(1, MathF.Ceiling(total)), 1, new int[count], top, heights, false);
    }

    internal static float ScrollToRow(GuideListLayout layout, int index, float scroll, float height)
    {
        if (index < 0 || index >= layout.Top.Length) return scroll;
        var top = layout.Top[index];
        var bottom = top + layout.Heights[index];
        var target = top < scroll || layout.Heights[index] > height ? top : bottom > scroll + height ? bottom - height : scroll;
        return Math.Clamp(MathF.Ceiling(target), 0, Math.Max(0, layout.ContentHeight - height));
    }

    internal static string Instruction(GuideMechanic mechanic, string fallback)
        => GuideShortCue.Instruction(mechanic, fallback);
}

internal sealed class GuideListScrollFocus
{
    private GuideBoss? _boss;
    private string? _phase;
    private readonly record struct Occurrence(GuideMechanic Mechanic, ulong Source, uint ID, GuideSignalKind Kind);
    private readonly Dictionary<Occurrence, DateTime> _seen = [];

    internal (bool Reset, GuideMechanic? Mechanic) Update(GuideCombatFrame frame, IEnumerable<GuideSignal> signals)
    {
        var reset = !ReferenceEquals(frame.Boss, _boss) || frame.KnownPhase?.ID != _phase;
        if (reset) { _boss = frame.Boss; _phase = frame.KnownPhase?.ID; _seen.Clear(); }
        HashSet<Occurrence> active = [];
        GuideMechanic? focus = null;
        foreach (var signal in signals.Where(signal => ReferenceEquals(signal.Boss, frame.Boss)).OrderBy(signal => signal.Until))
        {
            var key = new Occurrence(signal.Mechanic, signal.SourceID, signal.ID, signal.Kind);
            active.Add(key);
            if (!_seen.TryGetValue(key, out var until) || Math.Abs((signal.Until - until).TotalSeconds) >= .4)
            {
                _seen[key] = signal.Until;
                focus ??= signal.Mechanic;
            }
        }
        foreach (var key in _seen.Keys.Where(key => !active.Contains(key)).ToArray()) _seen.Remove(key);
        return (reset, focus);
    }
}
