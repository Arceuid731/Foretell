using BossMod;
using BossMod.Foretell;
using System.Globalization;

internal static class GuideRoleTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static void Run()
    {
        NormalizedRoles();
        MultipleAndAllRoles();
        PlayerRelevance();
        NarrativeDoesNotAssignRoles();
        Console.WriteLine("Guide role tags, explicit role normalization, player relevance and narrative isolation passed.");
    }

    private static void NormalizedRoles()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            foreach (var (role, prefix) in new[] { (Role.Tank, "[Tank] "), (Role.Healer, "[Heal] "), (Role.Melee, "[Melee] "), (Role.Ranged, "[Ranged] ") })
                Check(GuideRolePresentation.Prefix([" \t" + role.ToString().ToUpperInvariant() + "\r\n"]) == prefix,
                    "Explicit role enum name was not normalized: " + role);
            Check(GuideRolePresentation.Prefix(["Tank", "tank", " TANK "]) == "[Tank] ", "Duplicate role tags were displayed");
            Check(GuideRolePresentation.Prefix(["unknown", "None", "1", "dps", "heal", "", " ", null!]) == "", "Unknown role token produced a tag");
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    private static void MultipleAndAllRoles()
    {
        string[] roles = ["ranged", "healer", "tank", "healer"];
        var original = roles.ToArray();
        Check(GuideRolePresentation.Prefix(roles) == "[Tank] [Heal] [Ranged] ", "Multiple roles are missing, duplicated or unordered");
        Check(roles.SequenceEqual(original), "Role presentation mutated source metadata");
        Check(GuideRolePresentation.Prefix(["unknown", "melee"]) == "[Melee] ", "Unknown token obscured an explicit role");
        foreach (var unrestricted in new string[][] { [], ["all"], ["tank", " ALL ", "healer"], ["all", "tank"], ["ranged", "melee", "healer", "tank"] })
        {
            Check(GuideRolePresentation.Prefix(unrestricted) == "", "Party-wide advice has a role restriction tag");
            foreach (var playerClass in Enum.GetValues<Class>())
                Check(GuideRolePresentation.Relevant(unrestricted, playerClass), "Party-wide advice excludes " + playerClass);
        }
    }

    private static void PlayerRelevance()
    {
        var groups = new (string Role, Class[] Classes)[]
        {
            ("tank", [Class.GLA, Class.MRD, Class.PLD, Class.WAR, Class.DRK, Class.GNB]),
            ("healer", [Class.CNJ, Class.WHM, Class.SCH, Class.AST, Class.SGE]),
            ("melee", [Class.PGL, Class.LNC, Class.ROG, Class.MNK, Class.DRG, Class.NIN, Class.SAM, Class.RPR, Class.VPR]),
            ("ranged", [Class.ARC, Class.THM, Class.ACN, Class.BRD, Class.BLM, Class.SMN, Class.MCH, Class.RDM, Class.BLU, Class.DNC, Class.PCT])
        };
        foreach (var group in groups)
            foreach (var playerClass in group.Classes)
                foreach (var candidate in groups)
                    Check(GuideRolePresentation.Relevant([candidate.Role], playerClass) == (candidate.Role == group.Role),
                        "Role relevance mismatch for " + playerClass + " and " + candidate.Role);
        Check(GuideRolePresentation.Relevant(["tank", "healer"], Class.SGE)
            && !GuideRolePresentation.Relevant(["tank", "healer"], Class.VPR), "Multiple-role advice does not use union semantics");
        Check(GuideRolePresentation.Relevant(["unknown"], Class.PLD), "Missing role information hid advice");
        foreach (var unknownClass in new[] { Class.None, Class.CRP, Class.FSH, (Class)byte.MaxValue })
            Check(GuideRolePresentation.Relevant(["tank"], unknownClass), "Unknown player role hid advice");
    }

    private static void NarrativeDoesNotAssignRoles()
    {
        foreach (var narrative in new[] { "Tankbuster", "Tank: mitigate the hit", "Healer, cleanse the tank", "Melee players move away", "Ranged players spread", "tank/healer", "[Tank]" })
            Check(GuideRolePresentation.Prefix([narrative]) == "", "A role was guessed from narrative text: " + narrative);
        var advice = new GuideAdvice(GuideLanguage.English, "Tankbuster", "Healers heal the tank", "Melee and ranged spread while the tank mitigates.", "cast", "Tankbuster", ["The tank takes heavy damage."]);
        Check(GuideRolePresentation.Prefix(advice.Roles) == "" && GuideRolePresentation.Relevant(advice.Roles, Class.PCT),
            "Narrative-only advice acquired a role restriction");
        var targeted = advice with { Roles = ["healer"] };
        Check(GuideRolePresentation.Prefix(targeted.Roles) == "[Heal] " && !GuideRolePresentation.Relevant(targeted.Roles, Class.PLD),
            "Narrative tank mentions overrode explicit healer metadata");
    }
}
