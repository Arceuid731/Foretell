using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace BossMod.Foretell;

internal sealed record GuideDuty(uint ContentID, uint TerritoryID, string EnglishName)
{
    public bool Valid => ContentID != 0 && TerritoryID != 0 && EnglishName.Length is > 0 and <= 200;
    public string Key => $"C{ContentID}-T{TerritoryID}";
}

internal sealed class GuideMechanic(string name, string text, string anchor)
{
    public string Name { get; } = name;
    public string Text { get; } = text;
    public string Anchor { get; } = anchor;
    [System.Text.Json.Serialization.JsonIgnore]
    public GuideInstruction Prepared { get; } = GuidePreparation.Parse(text);
    [System.Text.Json.Serialization.JsonIgnore]
    public GuideRule[] Rules { get; } = GuideRules.Prepare(text);
    [System.Text.Json.Serialization.JsonIgnore]
    public string TextHash { get; } = GuideNames.Hash(text);
    [System.Text.Json.Serialization.JsonIgnore]
    public GuideStatusRule? StatusRule { get; } = GuideRules.PrepareStatus(text);
}
internal sealed record GuidePhase(string Name, string Context, GuideMechanic[] Mechanics)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string ContextHash { get; } = GuideNames.Hash(Context);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Conditional { get; } = GuideRules.HasConditions(Context);
}
internal sealed record GuideBoss(string Name, string Anchor, GuidePhase[] Phases);
internal sealed record GuideDocument(int Schema, GuideDuty Duty, string Title, long Revision, DateTime RetrievedAt,
    string SourceHash, GuideBoss[] Bosses)
{
    public const int CurrentSchema = 1;
    public string SourceUrl => $"https://ffxiv.consolegameswiki.com/mediawiki/index.php?title={Uri.EscapeDataString(Title)}&oldid={Revision}";
    public int MechanicCount => Bosses.Sum(boss => boss.Phases.Sum(phase => phase.Mechanics.Length));
}

internal static class GuideNames
{
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static string Normalize(string value) => Spaces.Replace(value.Normalize(NormalizationForm.FormKC)
        .Replace('’', '\'').Replace('_', ' ').Trim(), " ").ToLowerInvariant();
    public static string Boss(string value)
    {
        var name = Normalize(value);
        return name.StartsWith("the ", StringComparison.Ordinal) ? name[4..] : name;
    }
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

internal enum GuideInstruction { Unprepared, Tankbuster, Raidwide, Spread, Stack }
internal enum GuideLanguage { English, French, German, Japanese }

internal static class GuidePreparation
{
    public static GuideInstruction Instruction(GuideMechanic mechanic) => mechanic.Prepared;
    public static GuideInstruction Parse(string text) => GuideNames.Normalize(text).TrimEnd('.') switch
    {
        "tankbuster" or "a tankbuster" or "tank buster" or "high-damage tank buster" => GuideInstruction.Tankbuster,
        "raidwide damage" or "unavoidable raidwide damage that must be healed through" or "unavoidable raid-wide damage" => GuideInstruction.Raidwide,
        "a stack marker on a random player" or "stack marker on a random player" => GuideInstruction.Stack,
        "circle aoes will be placed on every player, requiring them to spread out" => GuideInstruction.Spread,
        _ => GuideInstruction.Unprepared
    };

    public static string Text(GuideInstruction instruction, GuideLanguage language) => instruction switch
    {
        GuideInstruction.Tankbuster => Local(language, "If targeted: mitigate the heavy hit.", "Si tu es ciblé : réduis les dégâts du coup puissant.", "Wenn du das Ziel bist: den schweren Treffer mindern.", "対象なら：強攻撃に防御スキルを使う。"),
        GuideInstruction.Raidwide => Local(language, "Party-wide damage: prepare mitigation and healing.", "Dégâts de groupe : prépare réduction de dégâts et soins.", "Gruppenschaden: Schadensminderung und Heilung vorbereiten.", "全体攻撃：軽減と回復を準備。"),
        GuideInstruction.Stack => Local(language, "Share the hit on the marked player.", "Partage le coup sur le joueur marqué.", "Den Treffer beim markierten Spieler teilen.", "マーカー対象者に集合して頭割り。"),
        GuideInstruction.Spread => Local(language, "Spread out: keep your circle off other players.", "Écarte-toi : ne superpose pas ton cercle aux autres.", "Verteilen: deinen Kreis nicht mit anderen überlappen.", "散開：自分の円を他の人に重ねない。"),
        _ => Local(language, "Response not prepared — see source text.", "Consigne à préparer — voir le texte source.", "Anweisung noch nicht aufbereitet — Quelltext ansehen.", "指示は未作成 — 原文を確認。")
    };

    public static string Local(GuideLanguage language, string english, string french, string german, string japanese) => language switch
    {
        GuideLanguage.French => french,
        GuideLanguage.German => german,
        GuideLanguage.Japanese => japanese,
        _ => english
    };
}

internal sealed record GuideCast(GuideDuty Duty, ulong ActorID, uint ObjectID, uint NameID, string BossName,
    uint ActionID, string ActionName, DateTime FinishAt);
internal sealed record GuideMatch(GuideBoss Boss, GuidePhase Phase, GuideMechanic Mechanic, GuideCast Cast);

internal static class GuideSynchronization
{
    public static GuideMatch? Match(GuideDocument document, GuideCast cast, DateTime now)
    {
        if (document.Duty != cast.Duty || cast.ActorID == 0 || cast.ObjectID == 0 || cast.NameID == 0 || cast.ActionID == 0
            || cast.FinishAt <= now || cast.FinishAt > now.AddMinutes(2)) return null;
        var bosses = document.Bosses.Where(boss => GuideNames.Boss(boss.Name) == GuideNames.Boss(cast.BossName)).ToArray();
        if (bosses.Length != 1) return null;
        var candidates = bosses[0].Phases.SelectMany(phase => phase.Mechanics.Select(mechanic => (phase, mechanic)))
            .Where(candidate => GuideNames.Normalize(candidate.mechanic.Name) == GuideNames.Normalize(cast.ActionName)).ToArray();
        return candidates.Length == 1 ? new(bosses[0], candidates[0].phase, candidates[0].mechanic, cast) : null;
    }
}
