namespace BossMod.Foretell;

internal sealed record GuideDrawBounds(float X, float Y, float Width, float Height)
{
    public static GuideDrawBounds From(Vector2 start, Vector2 end) => new(start.X, start.Y, end.X - start.X, end.Y - start.Y);

    public string Visibility(GuideDrawBounds clip, uint color)
    {
        if (!float.IsFinite(X + Y + Width + Height + clip.X + clip.Y + clip.Width + clip.Height)
            || Width <= 0 || Height <= 0 || clip.Width <= 0 || clip.Height <= 0) return "InvalidBounds";
        if ((color >> 24) == 0) return "Transparent";
        if (X >= clip.X + clip.Width || Y >= clip.Y + clip.Height || X + Width <= clip.X || Y + Height <= clip.Y) return "Clipped";
        return X < clip.X - .5f || Y < clip.Y - .5f || X + Width > clip.X + clip.Width + .5f || Y + Height > clip.Y + clip.Height + .5f
            ? "PartiallyClipped" : "Submitted";
    }
}

internal sealed record GuideDrawnRow(string Boss, string Phase, string Mechanic, ulong SourceID, uint ActionID,
    bool Highlighted, string Outcome, GuideDrawBounds? Bounds, string Instruction);
internal sealed record GuideDrawnAlert(string Cue, string Label, bool FromGuide, string Outcome, GuideDrawBounds? Bounds);
internal sealed record GuidePresentationCapture(DateTime At, string SessionID, uint TerritoryID, uint ContentID, string ListState, string CentralState,
    GuideDrawnRow[] Rows, GuideDrawnAlert[] Alerts)
{
    public bool SameContent(GuidePresentationCapture? other) => other != null && SessionID == other.SessionID && TerritoryID == other.TerritoryID && ContentID == other.ContentID
        && ListState == other.ListState && CentralState == other.CentralState && Rows.SequenceEqual(other.Rows) && Alerts.SequenceEqual(other.Alerts);
}

public sealed partial class ForetellEngine
{
    private GuidePresentationCapture? _guidePresentation;
    private GuidePresentationCapture? _guideLastDrawCapture;

    private void BeginGuidePresentationCapture()
    {
        var mode = _cfg.Mode is ForetellMode.Hybrid or ForetellMode.Foretell;
        var list = !mode ? "ModeHidden" : !_cfg.EnableGuides ? "GuidesDisabled" : !_cfg.GuideSidebar && !_cfg.GuideChecklistUnlocked ? "Disabled" : "NotDrawn";
        var central = !mode ? "ModeHidden" : !_cfg.GuideCentralAlerts && !_cfg.TextHints && !_cfg.TextHintsUnlocked ? "Disabled" : "NotDrawn";
        _guidePresentation = new(DateTime.UtcNow, _captureSession?.ID ?? "", _ws.CurrentZone, (uint)_ws.CurrentCFCID, list, central, [], []);
    }

    private void EndGuidePresentationCapture()
    {
        if (_demoFrame != null) { _guidePresentation = null; return; }
        if (_guidePresentation == null || _guidePresentation.SameContent(_guideLastDrawCapture)) return;
        _guideLastDrawCapture = _guidePresentation;
        CaptureGuideDiagnostics(true);
    }

    private void GuideListDrawState(string state)
    {
        if (_guidePresentation != null) _guidePresentation = _guidePresentation with { ListState = state };
    }

    private void GuideCentralDrawState(string state)
    {
        if (_guidePresentation != null) _guidePresentation = _guidePresentation with { CentralState = state };
    }
}
