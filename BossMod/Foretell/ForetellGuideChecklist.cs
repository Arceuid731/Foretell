using Dalamud.Bindings.ImGui;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private bool _guideChecklistWasUnlocked;
    private bool _guideChecklistDirty;
    private Vector2 _guideListEditSize;
    private GuideListLayout? _guideListLayout;
    private int _guideListLayoutKey;
    private DateTime _guideListMeasuredAt;
    private sealed record GuideListRow(GuidePhase Phase, GuideMechanic Mechanic, GuideSignal? Live, string Name, string Instruction, string[] Roles);

    private void DrawGuideChecklist()
    {
        if (_demoFrame == null && _guideDuty == null && !_cfg.GuideChecklistUnlocked) { GuideListDrawState("NoDuty"); return; }
        var frame = _demoFrame?.Guide ?? _guideFrame;
        var boss = frame.Boss;
        var active = (_demoFrame?.Guide.Active ?? LiveGuideSignals()).ToArray();
        var rows = GuideCombatListPresentation.Select(frame, active, _ws.Party[PartyState.PlayerSlot]?.Class ?? Class.None, _cfg.GuideCurrentPhaseOnly)
            .Select(entry => new GuideListRow(entry.Phase, entry.Mechanic, entry.Live, GuideMechanicName(boss!, entry.Mechanic),
                GuideListFlow.Instruction(entry.Mechanic, GuideChecklistInstruction(boss!, entry.Phase, entry.Mechanic)),
                _cfg.GuideRoleIcons ? GuideRolePresentation.Icons(entry.Mechanic.Advice?.Roles ?? []) : [])).ToArray();
        var activeCount = rows.Count(row => row.Live != null);
        var viewport = ImGui.GetMainViewport();
        var size = FiniteViewport(viewport.Size) ? viewport.Size : new Vector2(1920, 1080);
        var padding = ImGui.GetStyle().WindowPadding;
        var heading = ImGui.GetTextLineHeight() * OverlayScale(_cfg.GuideScale) * 2 + ImGui.GetStyle().ItemSpacing.Y * 2 + 6;
        var chrome = _cfg.GuideChecklistUnlocked ? ImGui.GetFrameHeight() : 0;
        var available = size - padding * 2 - new Vector2(16, 16 + heading + chrome);
        if (activeCount == 0 && _cfg.GuideChecklistUnlocked && _guideChecklistWasUnlocked && _guideListEditSize.X > 0)
            available.X = Math.Min(available.X, _guideListEditSize.X - padding.X * 2);
        available = Vector2.Max(Vector2.One, available);
        var hash = new HashCode();
        hash.Add(available); hash.Add(_cfg.GuideWidth); hash.Add(_cfg.GuideHeight); hash.Add(_cfg.GuideScale); hash.Add(_cfg.GuideRowSpacing); hash.Add(ImGui.GetFontSize()); hash.Add(ImGui.GetFont().GetHashCode());
        foreach (var row in rows) { hash.Add(row.Mechanic); hash.Add(row.Name); hash.Add(row.Instruction); hash.Add(row.Roles.Length); hash.Add(row.Live != null); }
        var key = hash.ToHashCode();
        if (_guideListLayout == null || key != _guideListLayoutKey || (_ws.CurrentTime - _guideListMeasuredAt).TotalSeconds >= 2)
        {
            _guideListLayout = GuideListFlow.Build(rows.Length, _cfg.GuideWidth, _cfg.GuideHeight - heading - chrome - padding.Y * 2, available, _cfg.GuideScale,
                (index, width, scale, compact) => MeasureGuideListRow(rows[index].Name, rows[index].Instruction, rows[index].Roles.Length, width, scale, compact, _cfg.GuideRowSpacing), activeCount);
            _guideListLayoutKey = key;
            _guideListMeasuredAt = _ws.CurrentTime;
        }
        var layout = _guideListLayout;
        List<GuideDrawnRow> drawn = [];
        foreach (var signal in active.Where(signal => !rows.Any(row => ReferenceEquals(row.Mechanic, signal.Mechanic))))
            drawn.Add(new(signal.Boss.Name, signal.Phase.Name, signal.Mechanic.Name, signal.SourceID, signal.ID, false, "NotSelected", null, signal.Mechanic.Advice?.Cue ?? ""));
        foreach (var row in rows.Skip(layout.Heights.Length))
            drawn.Add(new(boss!.Name, row.Phase.Name, row.Mechanic.Name, row.Live?.SourceID ?? 0, row.Live?.ID ?? 0, false, "LayoutOmitted", null, row.Instruction));
        rows = rows[..layout.Heights.Length];
        var dimensions = Vector2.Min(size - new Vector2(8), new Vector2(layout.Width, Math.Max(rows.Length == 0 ? 60 : 1, layout.Height) + heading + chrome) + padding * 2);
        if (_cfg.GuideChecklistUnlocked) dimensions.Y = Math.Max(dimensions.Y, Math.Clamp(_cfg.GuideHeight, Math.Min(180, size.Y), size.Y));
        var position = viewport.Pos + new Vector2(_cfg.GuidePositionX < 0 ? 20 : _cfg.GuidePositionX * size.X,
            _cfg.GuidePositionY < 0 ? size.Y * .2f : _cfg.GuidePositionY * size.Y);
        position = Vector2.Clamp(position, viewport.Pos, viewport.Pos + Vector2.Max(Vector2.Zero, size - dimensions));
        if (!_cfg.GuideChecklistUnlocked || !_guideChecklistWasUnlocked)
        { ImGui.SetNextWindowPos(position, ImGuiCond.Always); ImGui.SetNextWindowSize(dimensions, ImGuiCond.Always); }
        else if (activeCount > 0 && (_guideListEditSize.Y < dimensions.Y || _guideListEditSize.X < dimensions.X))
        {
            var expanded = Vector2.Min(size, Vector2.Max(_guideListEditSize, dimensions));
            ImGui.SetNextWindowSize(expanded, ImGuiCond.Always);
            ImGui.SetNextWindowPos(Vector2.Clamp(position, viewport.Pos, viewport.Pos + Vector2.Max(Vector2.Zero, size - expanded)), ImGuiCond.Always);
        }
        ImGui.SetNextWindowSizeConstraints(new(Math.Min(260, size.X), _cfg.GuideChecklistUnlocked ? 180 : 1), size);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, GuideColor(_cfg.GuideBackgroundColor));
        var visible = ImGui.Begin(GuideText("Foretell · drag / resize, then lock", "Foretell · déplacer / redimensionner, puis verrouiller", "Foretell · bewegen / skalieren, dann sperren", "Foretell・移動・サイズ変更後にロック") + "###ForetellGuideChecklist", GuideChecklistFlags(_cfg.GuideChecklistUnlocked));
        try
        {
            if (_cfg.GuideChecklistUnlocked)
            {
                var actual = (ImGui.GetWindowPos() - viewport.Pos) / size;
                var edited = ImGui.GetWindowSize();
                if (_guideChecklistWasUnlocked && (Vector2.Distance(edited, _guideListEditSize) > .5f
                    || Math.Abs(_cfg.GuidePositionX - actual.X) > .0001f || Math.Abs(_cfg.GuidePositionY - actual.Y) > .0001f))
                {
                    _cfg.GuidePositionX = Math.Clamp(actual.X, 0, 1); _cfg.GuidePositionY = Math.Clamp(actual.Y, 0, 1);
                    _cfg.GuideWidth = Math.Max(240, (edited.X - padding.X * 2 - GuideListFlow.Gap * (layout.Columns - 1)) / layout.Columns);
                    _cfg.GuideHeight = edited.Y; _guideChecklistDirty = true;
                }
                _guideListEditSize = edited;
            }
            if (_guideChecklistDirty && (!ImGui.IsMouseDown(ImGuiMouseButton.Left) || !_cfg.GuideChecklistUnlocked))
            { _guideChecklistDirty = false; _cfg.Modified.Fire(); }
            _guideChecklistWasUnlocked = _cfg.GuideChecklistUnlocked;
            if (!visible) { GuideListDrawState("WindowHidden"); return; }
            if (!_cfg.GuideChecklistUnlocked && _cfg.GuidePanelOpacity > 0)
                ImGui.GetWindowDrawList().AddRectFilled(ImGui.GetWindowPos(), ImGui.GetWindowPos() + ImGui.GetWindowSize(), OverlayOpacity(_cfg.GuideBackgroundColor, _cfg.GuidePanelOpacity), 8);
            if (_demoFrame == null && _liveGuide == null)
            {
                GuideListDrawState("PreparingGuide");
                if (_guideSummaries?.Snapshot is { Stage: not "Ready" }) { DrawGuideSummaryProgress(); return; }
                DrawGuideOverlayLine(_guideDuty == null ? GuideText("Foretell · boss mechanics", "Foretell · mécaniques du boss", "Foretell · Bossmechaniken", "Foretell・ボスギミック")
                    : GuideText("Preparing guide…", "Préparation du guide…", "Anleitung vorbereiten…", "攻略準備中…"), _cfg.GuideTextColor);
                return;
            }
            if (boss == null)
            {
                GuideListDrawState(frame.Ambiguous ? "BossAmbiguous" : "NoBoss");
                DrawGuideOverlayLine(frame.Ambiguous ? GuideText("Boss identification pending", "Boss à identifier", "Boss-Erkennung ausstehend", "ボス特定待ち")
                    : GuideText("Bosses completed", "Bosse terminés", "Bosse abgeschlossen", "ボス撃破済み"), _cfg.GuideUnresolvedColor);
                return;
            }
            ImGui.SetWindowFontScale(OverlayScale(_cfg.GuideScale));
            DrawGuideOverlayLine((frame.Upcoming ? GuideText("Upcoming: ", "À venir : ", "Als Nächstes: ", "次：") : "") + GuideBossName(boss), _cfg.GuideHeaderColor);
            if (ImGui.IsItemHovered()) DrawGuideHeaderTooltip(boss);
            if (ImGui.IsItemClicked() && _demoFrame == null) _guideEntryDismissed = false;
            if (_demoFrame == null && boss.Phases.Length == 0 && _guideSummaries?.Snapshot is { Stage: not "Ready" })
            {
                GuideListDrawState("PreparingBoss");
                DrawGuideSummaryProgress();
                return;
            }
            var phaseName = frame.KnownPhase?.Name ?? "";
            DrawGuideOverlayLine((phaseName.Length > 0 ? phaseName + " · " : "") + GuideText($"{rows.Length} mechanics", $"{rows.Length} mécaniques", $"{rows.Length} Mechaniken", $"{rows.Length}ギミック"), _cfg.GuideTextColor);
            ImGui.SetWindowFontScale(1);
            var origin = ImGui.GetCursorScreenPos() + new Vector2(0, 6);
            var visibleHeight = Math.Max(1, ImGui.GetWindowPos().Y + ImGui.GetWindowSize().Y - padding.Y - origin.Y);
            var clipEnd = origin + new Vector2(layout.Width, visibleHeight);
            ImGui.GetWindowDrawList().PushClipRect(origin, clipEnd, true);
            try
            {
                for (var index = 0; index < rows.Length; ++index)
                {
                    var row = rows[index];
                    var rowPosition = origin + new Vector2(layout.Column[index] * (layout.ColumnWidth + GuideListFlow.Gap), layout.Top[index]);
                    DrawGuideListRow(_cfg, rowPosition, layout.ColumnWidth, layout.Heights[index], layout.Scale, layout.Compact,
                        row.Name, row.Instruction, row.Roles, row.Live != null, row.Live == null ? null : Math.Max(0, (row.Live.Until - _ws.CurrentTime).TotalSeconds));
                    var draw = ImGui.GetWindowDrawList();
                    var bounds = GuideDrawBounds.From(rowPosition, rowPosition + new Vector2(layout.ColumnWidth, layout.Heights[index]));
                    var clip = GuideDrawBounds.From(Vector2.Max(draw.GetClipRectMin(), viewport.Pos), Vector2.Min(draw.GetClipRectMax(), viewport.Pos + size));
                    drawn.Add(new(boss.Name, row.Phase.Name, row.Mechanic.Name, row.Live?.SourceID ?? 0, row.Live?.ID ?? 0, row.Live != null,
                        bounds.Visibility(clip, row.Live != null ? _cfg.GuideActiveColor : _cfg.GuideTextColor), bounds, row.Instruction));
                    ImGui.SetCursorScreenPos(rowPosition);
                    ImGui.Dummy(new(layout.ColumnWidth, layout.Heights[index]));
                    if (ImGui.IsItemHovered() && ImGui.IsMouseHoveringRect(origin, clipEnd)) DrawGuideMechanicTooltip(boss, row.Phase, row.Mechanic);
                }
            }
            finally { ImGui.GetWindowDrawList().PopClipRect(); }
            if (_guidePresentation != null) _guidePresentation = _guidePresentation with { ListState = rows.Length == 0 ? "NoMechanics" : "Drawn", Rows = drawn.ToArray() };
            ImGui.SetScrollY(0);
        }
        finally { ImGui.SetWindowFontScale(1); ImGui.End(); ImGui.PopStyleColor(); }
    }

    internal static ImGuiWindowFlags GuideChecklistFlags(bool unlocked)
    {
        var flags = ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBringToFrontOnFocus;
        return unlocked ? flags : flags | ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove;
    }

    internal static void DrawGuideOverlayLine(string text, uint color)
    {
        var width = ImGui.GetContentRegionAvail().X;
        text = GuideChecklistPresentation.Fit(text, width, value => ImGui.CalcTextSize(value).X);
        var position = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddText(position + Vector2.One, 0xDD000000, text);
        draw.AddText(position, color, text);
        ImGui.Dummy(new(Math.Max(1, width), ImGui.GetTextLineHeight()));
    }

    private void DrawGuideHeaderTooltip(GuideBoss boss)
    {
        ImGui.BeginTooltip(); ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28);
        if (_demoFrame == null && _guideDuty != null) ImGui.TextWrapped(GuideDutyName(_guideDuty));
        if (boss.Summary.Length > 0) ImGui.TextWrapped(boss.Summary);
        ImGui.PopTextWrapPos(); ImGui.EndTooltip();
    }

    private void DrawGuideMechanicTooltip(GuideBoss boss, GuidePhase phase, GuideMechanic mechanic, string? instruction = null)
    {
        ImGui.BeginTooltip(); ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28);
        ImGui.TextColored(GuideColor(_cfg.GuideActiveColor), GuideMechanicName(boss, mechanic));
        if (mechanic.Advice is { } advice)
        {
            ImGui.TextWrapped(advice.Cue);
            ImGui.TextWrapped(advice.Description);
            var roles = GuideRolePresentation.Prefix(advice.Roles);
            if (roles.Length > 0) ImGui.TextUnformatted(roles);
            if (advice.Conflict.Length > 0) ImGui.TextWrapped(advice.Conflict);
        }
        else ImGui.TextWrapped(GuideSummaryFor(boss, phase, mechanic) ?? mechanic.Text);
        if (_demoFrame == null)
            foreach (var source in GuideSourceAssembly.EvidenceSources(_liveGuide, mechanic.Advice)) ImGui.TextDisabled(source.Provider);
        ImGui.PopTextWrapPos(); ImGui.EndTooltip();
    }
}
