namespace BossMod.Foretell;

internal sealed record GuideListLayout(float Scale, float ColumnWidth, float Width, float Height, int Columns, int[] Column, float[] Top, float[] Heights, bool Compact);

internal static class GuideListFlow
{
    internal const float Gap = 16;

    internal static GuideListLayout Build(int count, float preferredWidth, float preferredHeight, Vector2 available, float requestedScale,
        Func<int, float, float, bool, float> measure)
    {
        count = Math.Clamp(count, 0, 512);
        available = new(float.IsFinite(available.X) ? Math.Max(1, available.X) : 800, float.IsFinite(available.Y) ? Math.Max(1, available.Y) : 600);
        preferredWidth = float.IsFinite(preferredWidth) ? Math.Clamp(preferredWidth, Math.Min(240, available.X), available.X) : Math.Min(380, available.X);
        preferredHeight = float.IsFinite(preferredHeight) ? Math.Clamp(preferredHeight, 1, available.Y) : available.Y;
        requestedScale = float.IsFinite(requestedScale) ? Math.Clamp(requestedScale, .7f, 2) : 1;
        GuideListLayout? best = null;
        foreach (var compact in new[] { false, true })
        foreach (var scale in new[] { requestedScale, Math.Min(requestedScale, .85f), Math.Min(requestedScale, .7f) }.Distinct())
        foreach (var limit in new[] { preferredHeight, available.Y }.Distinct())
        {
            var maxColumns = Math.Clamp((int)((available.X + Gap) / (220 * scale + Gap)), 1, 4);
            for (var columns = 1; columns <= maxColumns; ++columns)
            {
                var width = Math.Min(preferredWidth, (available.X - Gap * (columns - 1)) / columns);
                var heights = Enumerable.Range(0, count).Select(index => Math.Max(1, measure(index, width, scale, compact))).ToArray();
                var lower = heights.Length == 0 ? 1 : heights.Max();
                var upper = Math.Max(lower, heights.Sum());
                int Required(float height)
                {
                    var result = 1;
                    var used = 0f;
                    foreach (var row in heights)
                    {
                        if (used > 0 && used + row > height + .01f) { ++result; used = 0; }
                        used += row;
                    }
                    return result;
                }
                for (var step = 0; step < 18; ++step)
                {
                    var middle = (lower + upper) * .5f;
                    if (Required(middle) <= columns) upper = middle;
                    else lower = middle;
                }
                var column = new int[count];
                var top = new float[count];
                var current = 0;
                var usedHeight = 0f;
                var maxHeight = 0f;
                for (var index = 0; index < count; ++index)
                {
                    if (usedHeight > 0 && usedHeight + heights[index] > upper + .02f && current + 1 < columns) { ++current; usedHeight = 0; }
                    column[index] = current;
                    top[index] = usedHeight;
                    usedHeight += heights[index];
                    maxHeight = Math.Max(maxHeight, usedHeight);
                }
                var layout = new GuideListLayout(scale, width, width * (current + 1) + Gap * current, Math.Max(1, maxHeight), current + 1, column, top, heights, compact);
                if (best == null || layout.Height < best.Height) best = layout;
                if (layout.Height <= limit + .1f) return layout;
            }
        }
        return best!;
    }

    internal static string Instruction(GuideMechanic mechanic, string fallback)
    {
        if (mechanic.Advice is not { } advice) return fallback;
        if (advice.Responses is [{ } response] && GuideNames.Normalize(response.When).TrimEnd(':', '.') is
            "cast" or "on cast" or "when cast" or "ability cast" or "telegraphed" or "telegraph" or "damage occurs") return response.Instruction;
        return advice.Responses.Length == 0 && advice.ShortCue.Length > 0 ? advice.ShortCue : advice.Cue;
    }

    internal static float Offset(GuideListLayout layout, float visibleHeight, double seconds, int activeIndex = -1)
    {
        visibleHeight = float.IsFinite(visibleHeight) ? Math.Max(1, visibleHeight) : 1;
        var overflow = Math.Max(0, layout.Height - visibleHeight);
        if (overflow == 0) return 0;
        if (activeIndex >= 0 && activeIndex < layout.Top.Length && layout.Heights[activeIndex] <= visibleHeight)
            return Math.Clamp(layout.Top[activeIndex] - (visibleHeight - layout.Heights[activeIndex]) * .5f, 0, overflow);
        var travel = overflow / 12d;
        var moment = (double.IsFinite(seconds) ? Math.Max(0, seconds) : 0) % (2 * travel + 6);
        return (float)(moment < 3 ? 0 : moment < 3 + travel ? (moment - 3) * 12
            : moment < 6 + travel ? overflow : overflow - (moment - 6 - travel) * 12);
    }
}
