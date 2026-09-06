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

    public static string Preview(string text, int limit = 320)
    {
        if (text.Length <= limit) return text;
        var boundary = text.LastIndexOf(' ', Math.Max(1, limit - 1));
        if (boundary < limit / 2) return Fit(text, limit, value => value.Length);
        return text[..boundary] + "…";
    }
}
