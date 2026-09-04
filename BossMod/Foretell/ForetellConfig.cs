using System.IO;
using System.Text.Json;

namespace BossMod.Foretell;

public enum ForetellMode
{
    Legacy = 0,
    Observe = 1,
    Hybrid = 2,
    Foretell = 4
}

public enum ForetellRadarShape
{
    Auto,
    Circle,
    Square
}

public enum ForetellRadarZoom
{
    Automatic,
    Manual
}

public enum ForetellRadarTerrainStyle
{
    Outline,
    Filled
}

[ConfigDisplay(Name = "Foretell", Order = 0)]
public sealed class ForetellConfig : ConfigNode
{
    [PropertyDisplay("Presentation mode", tooltip: "Recommended path: Observe -> Hybrid. Legacy shows BMR only. Observe learns silently while BMR guides you. Hybrid shows the complete BMR and Foretell presentations together. Foretell hides legacy encounter presentation and shows the adaptive layer only. Use /foretell for the guided dashboard.")]
    public ForetellMode Mode = ForetellMode.Observe;

    public override void Deserialize(JsonElement json, JsonSerializerOptions options)
    {
        // v0.8.1 serialized the combined presentation as "Compare". Rewrite that one legacy value before the
        // shared enum converter sees it; the old "Hybrid" name already maps to the new combined mode.
        if (json.TryGetProperty(nameof(Mode), out var mode) && mode.ValueKind == JsonValueKind.String
            && string.Equals(mode.GetString(), "Compare", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in json.EnumerateObject())
                {
                    if (property.NameEquals(nameof(Mode))) writer.WriteString(nameof(Mode), nameof(ForetellMode.Hybrid));
                    else property.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            using var migrated = JsonDocument.Parse(stream.ToArray());
            base.Deserialize(migrated.RootElement, options);
            return;
        }
        base.Deserialize(json, options);
    }

    [PropertyDisplay("Adaptive learning", tooltip: "When ON, Foretell updates persistent mechanics, sources, timelines and the local ML model from new evidence. When OFF, those learned data are read-only; live observation and guidance from existing memory can continue.")]
    public bool EnableLearning = true;

    [PropertyDisplay("Local ML classifier", tooltip: "Small dependency-free local classifier used only as an additional signal for ambiguous mechanic types. No cloud or remote inference is used.")]
    public bool EnableML = true;

    [PropertyDisplay("World-space overlay", tooltip: "Draw learned mechanic geometry directly in the game world when it passes the configured confidence threshold.")]
    public bool WorldOverlay = true;

    [PropertyDisplay("Foretell mini radar", tooltip: "Show Foretell's permanent local terrain radar. Learned/predicted mechanics are added only in Hybrid and Foretell presentation modes.")]
    public bool MiniRadar = true;

    [PropertyDisplay("Unlock radar position", tooltip: "Give the radar a draggable window. Lock it again after placing it.")]
    public bool RadarUnlocked;

    // Normalized top-left viewport position. Negative values select the default top-right placement.
    public float RadarPositionX = -1;
    public float RadarPositionY = -1;

    [PropertyDisplay("Radar arena frame", tooltip: "Auto continuously rasterizes nearby collision floors and barriers into a local walkable mesh, including barriers that close at pull start. The computation is player-centred, bounded by the visible radius and independent from authored BMR arena data. Circle and Square force the presentation frame without changing learned mechanics.")]
    public ForetellRadarShape RadarShape = ForetellRadarShape.Auto;

    [PropertyDisplay("Radar zoom mode", tooltip: "Automatic fits a detected closed room or arena, but stays within the configured readable limits in open areas and corridors. Manual always uses the selected visible radius.")]
    public ForetellRadarZoom RadarZoom = ForetellRadarZoom.Automatic;

    [PropertyDisplay("Radar terrain style", tooltip: "Outline draws only observed walls, drops and closed-arena edges. Filled also shades the connected walkable surface.")]
    public ForetellRadarTerrainStyle RadarTerrainStyle = ForetellRadarTerrainStyle.Outline;

    // ImGui ABGR packed colour, including opacity. Kept as a scalar for stable JSON migration.
    public uint RadarTerrainColor = 0xEBAFDC50;

    [PropertyDisplay("Text hints", tooltip: "Show adaptive mechanic, countdown, confidence and likely-next information.")]
    public bool TextHints = true;

    [PropertyDisplay("Unlock text hints", tooltip: "Give the combat guidance text a draggable window. Lock it again after placing it.")]
    public bool TextHintsUnlocked;

    // Normalized top-left viewport position. Negative values select the default top-center placement.
    public float TextPositionX = -1;
    public float TextPositionY = -1;

    [PropertyDisplay("Safe-position suggestions", tooltip: "Draw a suggested safe destination only for predictions above the strict safe-guidance confidence threshold. Suggestion only: Foretell never moves your character.")]
    public bool SafePositionSuggestions = true;

    [PropertyDisplay("Record local Replay Lab stream", tooltip: "Optional high-volume, human-readable stream of normalized encounter observations. It is written on a background thread, hard-limited to 512 MiB per territory segment, and never uploaded automatically. Exact packets remain in the separate compact raw journal.")]
    public bool RecordReplay;

    [PropertyDisplay("Automatically prune old recordings", tooltip: "Optional. When enabled, Foretell removes only inactive files from its own raw/replay folders, outside combat and on a background worker. Active recordings and learned memory are always protected.")]
    public bool AutomaticStorageMaintenance;

    [PropertyDisplay("Recording retention (days)", tooltip: "Inactive raw/replay recordings older than this are eligible for automatic cleanup. Set automatic pruning above to ON to apply it.")]
    [PropertySlider(1, 365, Speed = 1)]
    public int RecordingRetentionDays = 30;

    [PropertyDisplay("Maximum recording storage (GiB)", tooltip: "After retention cleanup, the oldest inactive recordings are removed until Foretell recordings fit this quota. Learned memory is not counted or deleted.")]
    [PropertySlider(1, 100, Speed = 1)]
    public int MaximumRecordingStorageGiB = 20;

    // Serialized migration marker; deliberately not shown in the configuration UI.
    public int ReplayPerformancePolicyVersion;

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

    [PropertyDisplay("Radar zoom / visible radius (yalms)", tooltip: "Distance from your character to the edge of the radar. Lower values zoom in; higher values show more of the arena.")]
    [PropertySlider(5, 120, Speed = 1)]
    public float RadarWorldRadius = 30;

    [PropertyDisplay("Automatic zoom minimum radius (yalms)", tooltip: "Local radius used when no complete closed boundary can be proved.")]
    [PropertySlider(10, 60, Speed = 1)]
    public float RadarAutoMinimumRadius = 30;

    [PropertyDisplay("Automatic zoom maximum radius (yalms)", tooltip: "Hard readability cap when fitting a closed arena; distant terrain stays intentionally outside the radar.")]
    [PropertySlider(20, 120, Speed = 1)]
    public float RadarAutoMaximumRadius = 65;

    [PropertyDisplay("Mini radar size (pixels)")]
    [PropertySlider(140, 600, Speed = 5)]
    public float RadarSize = 220;
}
