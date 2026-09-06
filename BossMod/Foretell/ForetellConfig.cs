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
    [PropertyDisplay("Presentation mode", tooltip: "Foretell: standard display. Hybrid: also show BMR. Observe: only show BMR while learning. Legacy: BMR only.")]
    public ForetellMode Mode = ForetellMode.Foretell;

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

    [PropertyDisplay("Automatic guides", tooltip: "Prepare the instance guide when entering. Saved guides work offline.")]
    public bool EnableGuides = true;

    [PropertyDisplay("Boss mechanics", tooltip: "Show the current boss mechanics and highlight the active one.")]
    public bool GuideSidebar = true;
    public bool GuideEntryPopup = true;
    public bool GuideChecklistUnlocked;
    public bool GuideCentralAlerts = true;
    public bool GuideLocalSummaries = true;
    public bool GuideSummaryGpu = true;
    public string GuideModelID = GuideModelCatalog.DefaultID;
    public int GuideContextTokens = GuideModelLimits.DefaultContext;
    public int GuidePipelineVersion;
    public int GuideMemoryGiB = GuideModelLimits.DefaultMemoryGiB;
    public float GuidePositionX = -1;
    public float GuidePositionY = -1;
    public float GuideWidth = 380;
    public float GuideHeight = 520;
    public float GuideScale = 1;
    public float GuideAlertScale = 1.4f;
    public float CentralAlertWidth = 620;
    public uint CentralAlertColor = 0xFF47D4FF;
    public uint CentralDetailColor = 0xFFE6E6E6;
    public uint CentralBarColor = 0xFF47D4FF;
    public uint GuideBackgroundColor = 0xE61F1814;
    public uint GuideTextColor = 0xFFE6E6E6;
    public uint GuideActiveColor = 0xFF47D4FF;
    public uint GuideResolvedColor = 0xFF99C47A;
    public uint GuideUnresolvedColor = 0xFFAAAAAA;

    [PropertyDisplay("World-space overlay", tooltip: "Draw learned mechanic geometry directly in the game world when it passes the configured confidence threshold.")]
    public bool WorldOverlay = true;
    public bool WorldConfidenceColors = true;
    public uint WorldColor = 0xFF3C3CFF;
    public float WorldOpacity = 1;
    public float WorldLineScale = 1;
    public bool WorldLabels = true;
    public uint WorldLabelColor = 0xFF47D4FF;
    public float WorldLabelScale = 1;

    [PropertyDisplay("Foretell mini radar", tooltip: "Show Foretell's permanent local terrain radar. Learned/predicted mechanics are added only in Hybrid and Foretell presentation modes.")]
    public bool MiniRadar = true;

    [PropertyDisplay("Unlock radar position", tooltip: "Give the radar a draggable window. Lock it again after placing it.")]
    public bool RadarUnlocked;

    // Normalized top-left viewport position. Negative values select the default top-right placement.
    public float RadarPositionX = -1;
    public float RadarPositionY = -1;

    [PropertyDisplay("Radar arena frame", tooltip: "Auto follows the room. Circle and Square use a fixed frame.")]
    public ForetellRadarShape RadarShape = ForetellRadarShape.Auto;

    [PropertyDisplay("Radar zoom mode", tooltip: "Automatic fits compact observed rooms or focuses on the boss, party and attack sources. Open areas use the configured local radius. Manual always uses the selected visible radius.")]
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

    [PropertyDisplay("Extra readable recording (advanced)", tooltip: "Optional high-volume JSONL for advanced diagnostics, capped at 512 MiB per territory segment. Automatic compressed Analysis ZIP capture already runs independently of this switch (64 MiB per session, 256 MiB cache, 14 days). No automatic upload.")]
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

    [PropertyDisplay("Maximum simultaneous hazard groups", tooltip: "Limits distinct attacks in the radar and world overlay. Simultaneous sources of the same attack remain together, within a 64-shape rendering limit.")]
    [PropertySlider(1, 32, Speed = 1)]
    public int MaxRenderedMechanics = 12;

    [PropertyDisplay("Radar zoom / visible radius (yalms)", tooltip: "Distance from your character to the edge of the radar. Lower values zoom in; higher values show more of the arena.")]
    [PropertySlider(5, 120, Speed = 1)]
    public float RadarWorldRadius = 30;

    [PropertyDisplay("Open-world automatic radius (yalms)", tooltip: "Local radius in open areas. Compact rooms and boss combat can zoom closer automatically.")]
    [PropertySlider(10, 60, Speed = 1)]
    public float RadarAutoMinimumRadius = 30;

    [PropertyDisplay("Automatic zoom maximum radius (yalms)", tooltip: "Hard readability cap when fitting a closed arena; distant terrain stays intentionally outside the radar.")]
    [PropertySlider(20, 120, Speed = 1)]
    public float RadarAutoMaximumRadius = 65;

    [PropertyDisplay("Mini radar size (pixels)")]
    [PropertySlider(140, 600, Speed = 5)]
    public float RadarSize = 220;
}
