namespace BossMod.Foretell;

internal sealed record GuideListLayout(float Scale, float ColumnWidth, float Width, float Height, int Columns, int[] Column, float[] Top, float[] Heights, bool Compact);

internal static class GuideListFlow
{
    internal const float Gap = 16;

    internal static GuideListLayout Build(int count, float preferredWidth, float preferredHeight, Vector2 available, float requestedScale,
        Func<int, float, float, bool, float> measure, int activeCount = 0)
    {
        count = Math.Clamp(count, 0, 512);
        available = new(float.IsFinite(available.X) ? Math.Max(1, available.X) : 800, float.IsFinite(available.Y) ? Math.Max(1, available.Y) : 600);
        preferredWidth = float.IsFinite(preferredWidth) ? Math.Clamp(preferredWidth, Math.Min(240, available.X), available.X) : Math.Min(380, available.X);
        preferredHeight = float.IsFinite(preferredHeight) ? Math.Clamp(preferredHeight, 1, available.Y) : available.Y;
        requestedScale = float.IsFinite(requestedScale) ? Math.Clamp(requestedScale, .7f, 2) : 1;
        activeCount = Math.Clamp(activeCount, 0, count);
        var heights = Enumerable.Range(0, count).Select(index => Math.Max(1, measure(index, preferredWidth, requestedScale, false))).ToArray();
        var visibleCount = Math.Min(count, Math.Max(GuideCombatListPresentation.PreferredCount, activeCount));
        var totalHeight = heights.Take(visibleCount).Sum();
        while (visibleCount > Math.Max(1, activeCount) && totalHeight > preferredHeight)
            totalHeight -= heights[--visibleCount];
        heights = heights[..visibleCount];
        count = visibleCount;
        GuideListLayout? best = null;
        var maxColumns = activeCount > 1 ? Math.Clamp((int)((available.X + Gap) / (preferredWidth + Gap)), 1, 4) : 1;
        for (var columns = 1; columns <= maxColumns; ++columns)
        {
            var width = preferredWidth;
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
            var layout = new GuideListLayout(requestedScale, width, width * (current + 1) + Gap * current, Math.Max(1, maxHeight), current + 1, column, top, heights, false);
            if (best == null || layout.Height < best.Height) best = layout;
            if (layout.Height <= available.Y + .1f) return layout;
        }
        return best!;
    }

    internal static string Instruction(GuideMechanic mechanic, string fallback)
    {
        if (mechanic.Advice is not { } advice) return fallback;
        if (advice.Responses is [{ } response] && CastCondition(response.When)) return response.Instruction;
        if (advice.Responses.Length == 0 && advice.Cue.IndexOf(':') is > 0 and var separator && CastCondition(advice.Cue[..separator]))
            return advice.Cue[(separator + 1)..].Trim();
        return advice.Cue.Length > 0 ? advice.Cue : advice.ShortCue.Length > 0 ? advice.ShortCue : fallback;
    }

    private static bool CastCondition(string condition) => GuideNames.Normalize(condition).TrimEnd(':', '.') is
        "cast" or "on cast" or "when cast" or "ability cast" or "telegraphed" or "telegraph" or "damage occurs";
}
