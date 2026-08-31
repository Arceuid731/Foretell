namespace BossMod.Foretell;

public enum ForetellMode
{
    Legacy,
    Observe,
    Compare,
    Hybrid,
    Foretell
}

[ConfigDisplay(Name = "Foretell", Order = 0)]
public sealed class ForetellConfig : ConfigNode
{
    [PropertyDisplay("Mode", tooltip: "Legacy: original BMR only. Observe: learn silently. Compare: BMR + Foretell. Hybrid: Foretell guidance with BMR retained. Foretell: adaptive presentation.")]
    public ForetellMode Mode = ForetellMode.Observe;

    [PropertyDisplay("Enable adaptive learning")]
    public bool EnableLearning = true;

    [PropertyDisplay("Enable local ML classifier")]
    public bool EnableML = true;

    [PropertyDisplay("Enable world-space overlay")]
    public bool WorldOverlay = true;

    [PropertyDisplay("Enable Foretell mini radar")]
    public bool MiniRadar = true;

    [PropertyDisplay("Enable text hints")]
    public bool TextHints = true;

    [PropertyDisplay("Enable safe-position suggestions", tooltip: "Suggestion only. Foretell never moves your character.")]
    public bool SafePositionSuggestions = true;

    [PropertyDisplay("Record compact local replay/event stream")]
    public bool RecordReplay = true;

    [PropertyDisplay("Minimum confidence to visualize (%)")]
    [PropertySlider(50, 100, Speed = 1)]
    public float VisualConfidence = 75;

    [PropertyDisplay("Minimum confidence to show warning guidance (%)")]
    [PropertySlider(50, 100, Speed = 1)]
    public float WarningConfidence = 95;

    [PropertyDisplay("Minimum confidence for safe guidance (%)", tooltip: "Never Guess Lethal threshold.")]
    [PropertySlider(50, 100, Speed = 1)]
    public float SafeConfidence = 99;

    [PropertyDisplay("Maximum learned AOEs rendered simultaneously")]
    [PropertySlider(1, 32, Speed = 1)]
    public int MaxRenderedMechanics = 12;

    [PropertyDisplay("Mini radar radius (yalms)")]
    [PropertySlider(10, 80, Speed = 1)]
    public float RadarWorldRadius = 30;

    [PropertyDisplay("Mini radar size (pixels)")]
    [PropertySlider(100, 500, Speed = 5)]
    public float RadarSize = 220;
}
