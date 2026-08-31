namespace BossMod;

[ConfigDisplay(Name = "Full duty automation", Order = 6)]
public sealed class ZoneModuleConfig : ConfigNode
{
    [PropertyDisplay("Required maturity for zone modules to be loaded")]
    public BossModuleInfo.Maturity MinMaturity = BossModuleInfo.Maturity.Contributed;

    [PropertyDisplay("Enable automatic execution of quest battles / solo duties")]
    public bool EnableQuestBattles = false;

    [PropertyDisplay("Draw waypoints in game world")]
    public bool ShowWaypoints = false;

    [PropertyDisplay("Use dash abilities for navigation (Smudge, Elusive Jump, etc)")]
    public bool UseDash = true;

    [PropertyDisplay("Lock zone module window movement and mouse interaction")]
    public bool Lock = false;

    [PropertyDisplay("Make zone module windows transparent", tooltip: "Removes the black window around zone module windows; this will not work if you move the radar to a different monitor")]
    public bool TransparentMode = false;
}
