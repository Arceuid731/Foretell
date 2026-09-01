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
    [PropertyDisplay("Presentation mode", tooltip: "Recommended path: Observe -> Compare -> Hybrid. Legacy shows BMR only. Observe learns silently while BMR guides you. Compare shows BMR and Foretell together. Hybrid uses Foretell guidance while retaining BMR as a safety net. Foretell hides legacy encounter presentation and shows the adaptive layer only. Use /foretell for the guided dashboard.")]
    public ForetellMode Mode = ForetellMode.Observe;

    [PropertyDisplay("Adaptive learning", tooltip: "When ON, Foretell updates persistent mechanics, sources, timelines and the local ML model from new evidence. When OFF, those learned data are read-only; live observation and guidance from existing memory can continue.")]
    public bool EnableLearning = true;

    [PropertyDisplay("Local ML classifier", tooltip: "Small dependency-free local classifier used only as an additional signal for ambiguous mechanic types. No cloud or remote inference is used.")]
    public bool EnableML = true;

    [PropertyDisplay("World-space overlay", tooltip: "Draw learned mechanic geometry directly in the game world when it passes the configured confidence threshold.")]
    public bool WorldOverlay = true;

    [PropertyDisplay("Foretell mini radar", tooltip: "Show Foretell's compact encounter radar for learned/predicted mechanics.")]
    public bool MiniRadar = true;

    [PropertyDisplay("Unlock radar position", tooltip: "Give the radar a draggable window. Lock it again after placing it.")]
    public bool RadarUnlocked;

    // Normalized top-left viewport position. Negative values select the default top-right placement.
    public float RadarPositionX = -1;
    public float RadarPositionY = -1;

    [PropertyDisplay("Text hints", tooltip: "Show adaptive mechanic, countdown, confidence and likely-next information.")]
    public bool TextHints = true;

    [PropertyDisplay("Safe-position suggestions", tooltip: "Draw a suggested safe destination only for predictions above the strict safe-guidance confidence threshold. Suggestion only: Foretell never moves your character.")]
    public bool SafePositionSuggestions = true;

    [PropertyDisplay("Record local Replay Lab stream", tooltip: "Record compact normalized encounter observations locally. Replay Lab can re-run them through the learner in an isolated sandbox for debugging/regression; this is not a video replay and is never uploaded automatically.")]
    public bool RecordReplay = true;

    [PropertyDisplay("Visual hypothesis threshold (%)", tooltip: "Below this confidence, a learned hypothesis stays hidden from Foretell's combat presentation and remains learning/debug data only.")]
    [PropertySlider(50, 100, Speed = 1)]
    public float VisualConfidence = 75;

    [PropertyDisplay("Warning-grade threshold (%)", tooltip: "Minimum confidence before Foretell treats an inference as strong enough for warning-grade guidance.")]
    [PropertySlider(50, 100, Speed = 1)]
    public float WarningConfidence = 95;

    [PropertyDisplay("Safe-guidance threshold (%)", tooltip: "Never Guess Lethal threshold. Safe-position guidance is only eligible at or above this confidence.")]
    [PropertySlider(50, 100, Speed = 1)]
    public float SafeConfidence = 99;

    [PropertyDisplay("Maximum learned AOEs rendered simultaneously", tooltip: "Caps adaptive world-overlay clutter when several learned mechanics are active at once.")]
    [PropertySlider(1, 32, Speed = 1)]
    public int MaxRenderedMechanics = 12;

    [PropertyDisplay("Mini radar radius (yalms)", tooltip: "World distance represented by the mini radar radius.")]
    [PropertySlider(10, 80, Speed = 1)]
    public float RadarWorldRadius = 30;

    [PropertyDisplay("Mini radar size (pixels)")]
    [PropertySlider(100, 500, Speed = 5)]
    public float RadarSize = 220;
}
