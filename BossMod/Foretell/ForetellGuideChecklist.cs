using Dalamud.Bindings.ImGui;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private bool _guideChecklistWasUnlocked;
    private bool _guideChecklistDirty;
    private GuideBoss? _guideChecklistBoss;
    private int _guideChecklistPage;

    private void DrawGuideChecklist()
    {
        if (_guideDuty == null && !_cfg.GuideChecklistUnlocked) return;
        var boss = _guideFrame.Boss;
        if (!ReferenceEquals(boss, _guideChecklistBoss)) { _guideChecklistBoss = boss; _guideChecklistPage = 0; }
        var active = LiveGuideSignals().OrderByDescending(signal => signal.TargetID == _ws.Party[PartyState.PlayerSlot]?.InstanceID).ThenBy(signal => signal.Until).ToArray();
        var rows = boss?.Phases.SelectMany(phase => phase.Mechanics.Select(mechanic => (Phase: phase, Mechanic: mechanic,
            Live: active.FirstOrDefault(signal => ReferenceEquals(signal.Mechanic, mechanic) && ReferenceEquals(signal.Phase, phase))))).ToArray() ?? [];
        var viewport = ImGui.GetMainViewport();
        var size = FiniteViewport(viewport.Size) ? viewport.Size : new Vector2(1920, 1080);
        var minimumWidth = Math.Min(260, size.X);
        var width = Math.Clamp(Math.Max(_cfg.GuideWidth, minimumWidth), minimumWidth, Math.Max(minimumWidth, size.X));
        var lineHeight = ImGui.GetTextLineHeight() * _cfg.GuideScale;
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        var padding = ImGui.GetStyle().WindowPadding;
        var heightLimit = Math.Min(_cfg.GuideHeight, size.Y);
        var chrome = _cfg.GuideChecklistUnlocked ? ImGui.GetFrameHeight() : 0;
        var contentWidth = Math.Max(1, width - padding.X * 2);
        var labels = rows.Select(row => GuideChecklistPresentation.Row(row.Live != null ? "▶ " + GuideMechanicName(boss!, row.Mechanic) : "• " + GuideMechanicName(boss!, row.Mechanic),
            GuideRolePresentation.Prefix(row.Mechanic.Advice?.Roles ?? []) + GuideChecklistInstruction(boss!, row.Phase, row.Mechanic, row.Live) + (row.Live == null ? "" : $" · {Math.Max(0, (row.Live.Until - _ws.CurrentTime).TotalSeconds):F1}s"),
            contentWidth / _cfg.GuideScale, text => ImGui.CalcTextSize(text).X)).ToArray();
        var heights = labels.Select(text => ImGui.CalcTextSize(text, false, contentWidth / _cfg.GuideScale).Y * _cfg.GuideScale + spacing).ToArray();
        var page = GuideChecklistPresentation.HeightPage(heights, Math.Max(lineHeight, heightLimit - padding.Y * 2 - chrome - 2 * (lineHeight + spacing)),
            _guideChecklistPage, Array.FindIndex(rows, row => row.Live != null));
        _guideChecklistPage = page.Page;
        var height = _cfg.GuideChecklistUnlocked ? heightLimit : Math.Min(size.Y, padding.Y * 2 + (1 + (page.Count == 0 ? 3 : 0) + (page.Pages > 1 ? 1 : 0)) * (lineHeight + spacing)
            + heights.Skip(page.Start).Take(page.Count).Sum());
        var position = viewport.Pos + new Vector2(_cfg.GuidePositionX < 0 ? 20 : _cfg.GuidePositionX * size.X, _cfg.GuidePositionY < 0 ? size.Y * .2f : _cfg.GuidePositionY * size.Y);
        position = Vector2.Clamp(position, viewport.Pos, viewport.Pos + Vector2.Max(Vector2.Zero, size - new Vector2(width, height)));
        if (!_cfg.GuideChecklistUnlocked || !_guideChecklistWasUnlocked)
        { ImGui.SetNextWindowPos(position, ImGuiCond.Always); ImGui.SetNextWindowSize(new(width, height), ImGuiCond.Always); }
        ImGui.SetNextWindowSizeConstraints(new(minimumWidth, _cfg.GuideChecklistUnlocked ? 180 : 1), Vector2.Min(new(1000, 1000), Vector2.Max(new(minimumWidth, 180), size)));
        ImGui.PushStyleColor(ImGuiCol.Text, GuideColor(_cfg.GuideTextColor));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, GuideColor(_cfg.GuideBackgroundColor));
        var flags = GuideChecklistFlags(_cfg.GuideChecklistUnlocked);
        var visible = ImGui.Begin(GuideText("Foretell · drag / resize, then lock", "Foretell · déplacer / redimensionner, puis verrouiller", "Foretell · bewegen / skalieren, dann sperren", "Foretell・移動・サイズ変更後にロック") + "###ForetellGuideChecklist", flags);
        try
        {
            ImGui.SetWindowFontScale(_cfg.GuideScale);
            if (_cfg.GuideChecklistUnlocked)
            {
                var actual = (ImGui.GetWindowPos() - viewport.Pos) / size;
                var dimensions = ImGui.GetWindowSize();
                if (Math.Abs(_cfg.GuidePositionX - actual.X) > .0001f || Math.Abs(_cfg.GuidePositionY - actual.Y) > .0001f
                    || Math.Abs(_cfg.GuideWidth - dimensions.X) > .5f || Math.Abs(_cfg.GuideHeight - dimensions.Y) > .5f)
                {
                    _cfg.GuidePositionX = Math.Clamp(actual.X, 0, 1); _cfg.GuidePositionY = Math.Clamp(actual.Y, 0, 1);
                    _cfg.GuideWidth = dimensions.X; _cfg.GuideHeight = dimensions.Y; _guideChecklistDirty = true;
                }
            }
            if (_guideChecklistDirty && (!ImGui.IsMouseDown(ImGuiMouseButton.Left) || !_cfg.GuideChecklistUnlocked))
            { _guideChecklistDirty = false; _cfg.Modified.Fire(); }
            _guideChecklistWasUnlocked = _cfg.GuideChecklistUnlocked;
            if (!visible) return;
            if (_liveGuide == null)
            {
                if (_guideSummaries?.Snapshot is { Stage: not "Ready" })
                {
                    DrawGuideSummaryProgress();
                    return;
                }
                var failed = _guides?.Snapshot.State == GuideState.Failed;
                DrawGuideOverlayLine(_guideDuty == null ? GuideText("Foretell · boss mechanics", "Foretell · mécaniques du boss", "Foretell · Bossmechaniken", "Foretell・ボスギミック")
                    : failed ? GuideText("Guide unavailable", "Guide indisponible", "Anleitung nicht verfügbar", "攻略取得不可")
                    : GuideText("Foretell · preparing guide…", "Foretell · préparation du guide…", "Foretell · Anleitung wird vorbereitet…", "Foretell・攻略を準備中…"), _cfg.GuideTextColor);
                if (ImGui.IsItemHovered()) DrawGuideHeaderTooltip(null);
                return;
            }
            if (boss == null)
            {
                DrawGuideOverlayLine(_guideFrame.Ambiguous
                    ? GuideText("Boss identification pending", "Boss à identifier", "Boss-Erkennung ausstehend", "ボス特定待ち")
                    : GuideText("Bosses completed", "Boss terminés", "Bosse abgeschlossen", "ボス撃破済み"), _cfg.GuideUnresolvedColor);
                if (ImGui.IsItemHovered()) DrawGuideHeaderTooltip(null);
                return;
            }
            DrawGuideOverlayLine((_guideFrame.Upcoming ? GuideText("Upcoming: ", "À venir : ", "Als Nächstes: ", "次：") : "") + GuideBossName(boss), _cfg.GuideTextColor);
            var headerClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
            if (ImGui.IsItemHovered()) DrawGuideHeaderTooltip(boss);
            if (headerClicked) _guideEntryDismissed = false;
            if (rows.Length == 0)
                DrawGuideOverlayLine(GuideText("Preparing mechanics…", "Préparation des mécaniques…", "Mechaniken werden vorbereitet…", "ギミックを準備中…"), _cfg.GuideTextColor);
            for (var index = page.Start; index < page.Start + page.Count; ++index)
            {
                var row = rows[index];
                var color = row.Live != null ? _cfg.GuideActiveColor : _cfg.GuideTextColor;
                var instruction = GuideChecklistInstruction(boss, row.Phase, row.Mechanic, row.Live);
                ImGui.PushStyleColor(ImGuiCol.Text, GuideColor(color));
                ImGui.TextWrapped(labels[index]);
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) DrawGuideMechanicTooltip(boss, row.Phase, row.Mechanic, instruction);
            }
            if (page.Pages > 1)
            {
                if (ImGui.SmallButton("<###GuidePrevious")) _guideChecklistPage = (page.Page + page.Pages - 1) % page.Pages;
                ImGui.SameLine(); ImGui.TextUnformatted($"{page.Page + 1}/{page.Pages}"); ImGui.SameLine();
                if (ImGui.SmallButton(">###GuideNext")) _guideChecklistPage = (page.Page + 1) % page.Pages;
            }
        }
        finally { ImGui.End(); ImGui.PopStyleColor(2); }
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

    private void DrawGuideHeaderTooltip(GuideBoss? boss)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28);
        if (_guideDuty != null) ImGui.TextWrapped(GuideDutyName(_guideDuty));
        if (boss != null && boss.Summary.Length > 0) ImGui.TextWrapped(boss.Summary);
        ImGui.TextDisabled(GuideText("Click for the instance overview.", "Clique pour le résumé de l’instance.", "Für Instanzübersicht anklicken.", "クリックでコンテンツ概要。"));
        ImGui.PopTextWrapPos(); ImGui.EndTooltip();
    }

    private void DrawGuideMechanicTooltip(GuideBoss boss, GuidePhase phase, GuideMechanic mechanic, string instruction)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28);
        ImGui.TextColored(GuideColor(_cfg.GuideActiveColor), GuideMechanicName(boss, mechanic));
        ImGui.TextWrapped(mechanic.Advice?.Description ?? GuideSummaryFor(boss, phase, mechanic) ?? mechanic.Text);
        if (mechanic.Advice is { Conflict.Length: > 0 } advice) ImGui.TextWrapped(advice.Conflict);
        foreach (var source in GuideSourceAssembly.EvidenceSources(_liveGuide, mechanic.Advice)) ImGui.TextDisabled(source.Provider);
        if (mechanic.Advice?.TriggerKind == "manual")
            ImGui.TextDisabled(GuideText("Watch for this mechanic during the fight.", "Repère cette mécanique pendant le combat.", "Im Kampf auf diese Mechanik achten.", "戦闘中にこのギミックを確認。"));
        ImGui.PopTextWrapPos(); ImGui.EndTooltip();
    }
}
