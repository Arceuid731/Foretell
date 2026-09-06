namespace BossMod.Foretell;

internal static class GuideRolePresentation
{
    [Flags]
    private enum RoleSet
    {
        None = 0,
        Tank = 1,
        Healer = 2,
        Melee = 4,
        Ranged = 8,
        All = Tank | Healer | Melee | Ranged
    }

    public static string Prefix(IEnumerable<string> roles)
    {
        var selected = Read(roles);
        if (selected is RoleSet.None or RoleSet.All) return "";
        return ((selected & RoleSet.Tank) != 0 ? "[Tank] " : "")
            + ((selected & RoleSet.Healer) != 0 ? "[Heal] " : "")
            + ((selected & RoleSet.Melee) != 0 ? "[Melee] " : "")
            + ((selected & RoleSet.Ranged) != 0 ? "[Ranged] " : "");
    }

    public static bool Relevant(IEnumerable<string> roles, Class playerClass)
    {
        var selected = Read(roles);
        var playerRole = playerClass.GetRole() switch
        {
            Role.Tank => RoleSet.Tank,
            Role.Healer => RoleSet.Healer,
            Role.Melee => RoleSet.Melee,
            Role.Ranged => RoleSet.Ranged,
            _ => RoleSet.None
        };
        return selected is RoleSet.None or RoleSet.All || playerRole == RoleSet.None || (selected & playerRole) != 0;
    }

    private static RoleSet Read(IEnumerable<string> roles)
    {
        var selected = RoleSet.None;
        foreach (var role in roles)
        {
            switch (role?.Trim().ToLowerInvariant())
            {
                case "all": return RoleSet.All;
                case "tank": selected |= RoleSet.Tank; break;
                case "healer": selected |= RoleSet.Healer; break;
                case "melee": selected |= RoleSet.Melee; break;
                case "ranged": selected |= RoleSet.Ranged; break;
            }
        }
        return selected;
    }
}
