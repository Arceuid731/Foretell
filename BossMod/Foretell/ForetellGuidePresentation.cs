namespace BossMod.Foretell;

internal static class GuideChecklistPresentation
{
    public static string Instruction(GuidanceKind guidance, GuideLanguage language, bool confirmed = true)
    {
        string Local(string english, string french, string german, string japanese) => GuidePreparation.Local(language, english, french, german, japanese);
        if (!confirmed)
            return guidance switch
            {
                GuidanceKind.Avoid => Local("WATCH AOE", "SURVEILLE LA ZONE", "FLÄCHE BEACHTEN", "範囲に注意"),
                GuidanceKind.Stack or GuidanceKind.Soak => Local("CHECK GROUP TARGET", "CIBLE À CONFIRMER", "GRUPPENZIEL PRÜFEN", "集合対象を確認"),
                GuidanceKind.Tankbuster => Local("TANK: MITIGATE", "TANK : MITIGATION", "TANK: ABWEHR", "タンク：軽減"),
                _ => Local("CHECK RESPONSE", "À VÉRIFIER", "REAKTION PRÜFEN", "対処を確認")
            };
        return guidance switch
        {
            GuidanceKind.Avoid => Local("DODGE AOE", "ÉVITE LA ZONE", "FLÄCHE MEIDEN", "範囲を避ける"),
            GuidanceKind.Stack => Local("STACK", "REGROUPE-TOI", "SAMMELN", "頭割り"),
            GuidanceKind.Spread => Local("SPREAD", "ÉCARTE-TOI", "VERTEILEN", "散開"),
            GuidanceKind.Soak => Local("SOAK TOWER", "PRENDS LA TOUR", "TURM BESCHREITEN", "塔に入る"),
            GuidanceKind.LookAway => Local("LOOK AWAY", "DÉTOURNE LE REGARD", "WEGSEHEN", "視線を外す"),
            GuidanceKind.Knockback => Local("PREPARE FOR KNOCKBACK", "ANTICIPE LE RECUL", "RÜCKSTOSS BEACHTEN", "ノックバックに備える"),
            GuidanceKind.Raidwide => Local("MITIGATE / HEAL", "MITIGATION / SOINS", "ABWEHR / HEILUNG", "軽減・回復"),
            GuidanceKind.Tankbuster => Local("TANK: MITIGATE", "TANK : MITIGATION", "TANK: ABWEHR", "タンク：軽減"),
            GuidanceKind.Cleanse => Local("CLEANSE", "DISSIPE", "ENTFERNEN", "解除"),
            _ => Local("CHECK RESPONSE", "À VÉRIFIER", "REAKTION PRÜFEN", "対処を確認")
        };
    }

    public static (int Page, int Pages, int Start, int Count) Page(int count, int capacity, int requested, int activeIndex = -1)
    {
        capacity = Math.Max(1, capacity);
        count = Math.Max(0, count);
        var pages = Math.Max(1, (count + capacity - 1) / capacity);
        var page = activeIndex >= 0 && activeIndex < count ? activeIndex / capacity : Math.Clamp(requested, 0, pages - 1);
        var start = page * capacity;
        return (page, pages, start, Math.Min(capacity, count - start));
    }

    public static string Fit(string text, float width, Func<string, float> measure)
    {
        if (measure(text) <= width) return text;
        if (measure("…") > width) return "";
        var boundaries = System.Globalization.StringInfo.ParseCombiningCharacters(text);
        var lower = 0;
        var upper = boundaries.Length;
        while (lower < upper)
        {
            var middle = (lower + upper + 1) / 2;
            var end = middle == boundaries.Length ? text.Length : boundaries[middle];
            if (measure(text[..end] + "…") <= width) lower = middle;
            else upper = middle - 1;
        }
        return text[..(lower == boundaries.Length ? text.Length : boundaries[lower])] + "…";
    }

    public static (int Page, int Pages, int Start, int Count) HeightPage(float[] heights, float budget, int requested, int activeIndex = -1)
    {
        var starts = new List<int> { 0 };
        var used = 0f;
        for (var index = 0; index < heights.Length; ++index)
        {
            if (used > 0 && used + heights[index] > budget) { starts.Add(index); used = 0; }
            used += Math.Max(1, heights[index]);
        }
        var page = activeIndex >= 0 && activeIndex < heights.Length ? starts.FindLastIndex(start => start <= activeIndex) : Math.Clamp(requested, 0, starts.Count - 1);
        return (page, starts.Count, starts[page], (page + 1 < starts.Count ? starts[page + 1] : heights.Length) - starts[page]);
    }

    public static string Row(string name, string instruction, float width, Func<string, float> measure)
        => Fit(name, Math.Max(width * .25f, width - measure(" — " + instruction)), measure) + " — " + instruction;

    public static string Preview(string text, int limit = 320)
    {
        if (text.Length <= limit) return text;
        var boundary = text.LastIndexOf(' ', Math.Max(1, limit - 1));
        if (boundary < limit / 2) return Fit(text, limit, value => value.Length);
        return text[..boundary] + "…";
    }
}
