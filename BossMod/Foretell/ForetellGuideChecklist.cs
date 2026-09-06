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
        var minimumWidth = rows.Length == 0 ? 260 : Math.Max(260, 110 + rows.Max(row => ImGui.CalcTextSize(" — " + GuideChecklistInstruction(boss!, row.Phase, row.Mechanic, row.Live) + " · 999.9s").X) * _cfg.GuideScale);
        minimumWidth = Math.Min(minimumWidth, Math.Min(1000, size.X));
        var width = Math.Clamp(Math.Max(_cfg.GuideWidth, minimumWidth), minimumWidth, Math.Max(minimumWidth, size.X));
        var lineHeight = ImGui.GetTextLineHeight() * _cfg.GuideScale;
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        var padding = ImGui.GetStyle().WindowPadding;
        var heightLimit = Math.Min(_cfg.GuideHeight, size.Y);
        var chrome = _cfg.GuideChecklistUnlocked ? ImGui.GetFrameHeight() : 0;
        var capacity = Math.Max(1, (int)((heightLimit - padding.Y * 2 - chrome) / (lineHeight + spacing)) - 2);
        var page = GuideChecklistPresentation.Page(rows.Length, capacity, _guideChecklistPage, Array.FindIndex(rows, row => row.Live != null));
        _guideChecklistPage = page.Page;
        var height = _cfg.GuideChecklistUnlocked ? heightLimit : Math.Min(size.Y, padding.Y * 2 + (1 + Math.Max(1, page.Count) + (page.Pages > 1 ? 1 : 0)) * (lineHeight + spacing));
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
                var failed = _guides?.Snapshot.State == GuideState.Failed;
                DrawGuideOverlayLine(_guideDuty == null ? GuideText("Foretell · checklist position", "Foretell · position de la checklist", "Foretell · Checklistenposition", "Foretell・チェックリストの位置")
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
                DrawGuideOverlayLine(GuideText("Narrative guide · hover boss", "Guide narratif · survole le boss", "Textanleitung · Boss berühren", "文章形式の攻略・ボスにカーソル"), _cfg.GuideUnresolvedColor);
            foreach (var row in rows.Skip(page.Start).Take(page.Count))
            {
                var resolved = _guideEncounter.Resolved(boss, row.Phase, row.Mechanic);
                var prepared = GuideRules.LiveGuidance(row.Mechanic, row.Phase, boss) != GuidanceKind.None;
                var color = row.Live != null ? _cfg.GuideActiveColor : resolved ? _cfg.GuideResolvedColor : prepared ? _cfg.GuideTextColor : _cfg.GuideUnresolvedColor;
                var instruction = GuideChecklistInstruction(boss, row.Phase, row.Mechanic, row.Live);
                var timer = row.Live == null ? "" : $" · {Math.Max(0, (row.Live.Until - _ws.CurrentTime).TotalSeconds):F1}s";
                var prefix = row.Live != null ? "▶ " : resolved ? "✓ " : "• ";
                var available = ImGui.GetContentRegionAvail().X;
                var suffix = " — " + instruction + timer;
                var name = GuideChecklistPresentation.Fit(prefix + GuideMechanicName(boss, row.Mechanic), Math.Max(35, available - ImGui.CalcTextSize(suffix).X), text => ImGui.CalcTextSize(text).X);
                DrawGuideOverlayLine(name + suffix, color);
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
        if (boss != null)
        {
            ImGui.TextWrapped(GuideBossName(boss));
            if (_liveGuide is { MechanicCount: > 0 } && boss.Phases.All(phase => phase.Mechanics.Length == 0)) ImGui.TextWrapped(GuideNarrativeNotice());
            foreach (var phase in boss.Phases.Where(phase => phase.Context.Length > 0))
                ImGui.TextWrapped(GuideContextSummary(boss, phase) ?? phase.Context);
        }
        if (_guides != null) DrawGuideState(_guides.Snapshot);
        DrawGuideSummaryProgress();
        ImGui.TextWrapped(GuideText("Hover a mechanic for details. Click the boss to reopen the entry panel. Unlock in /foretell → Guides.",
            "Survole une mécanique pour ses détails. Clique sur le boss pour revoir le panneau d’entrée. Déverrouillage : /foretell → Guides.",
            "Mechanik berühren für Details. Boss anklicken für die Übersicht. Entsperren: /foretell → Guides.",
            "ギミックにカーソルで詳細。ボスをクリックで入場案内。ロック解除：/foretell → Guides。"));
        ImGui.TextWrapped(GuideText("✓ means resolved this pull, not successful execution or no repeats.", "✓ = résolue sur ce pull, pas forcément réussie ni définitivement terminée.",
            "✓ = in diesem Versuch aufgelöst; kein Erfolgsnachweis oder Ausschluss einer Wiederholung.", "✓＝この戦闘で解決済み。成功や再発しないことを示しません。"));
        ImGui.PopTextWrapPos(); ImGui.EndTooltip();
    }

    private void DrawGuideMechanicTooltip(GuideBoss boss, GuidePhase phase, GuideMechanic mechanic, string instruction)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28);
        ImGui.TextColored(GuideColor(_cfg.GuideActiveColor), GuideMechanicName(boss, mechanic));
        ImGui.TextWrapped(instruction);
        if (phase.Name.Length > 0) ImGui.TextDisabled(GuidePhaseName(phase));
        if (GuideSummaryFor(boss, phase, mechanic) is { } summary)
        {
            ImGui.TextWrapped(GuideChecklistPresentation.Preview(summary));
            ImGui.TextDisabled(GuideText("Automatic summary · check source", "Résumé auto · vérifier la source", "Automatisch · Quelle prüfen", "自動要約・原文を確認"));
        }
        else ImGui.TextWrapped(GuideChecklistPresentation.Preview(mechanic.Text));
        if (GuideRules.LiveGuidance(mechanic, phase, boss) == GuidanceKind.None)
            ImGui.TextWrapped(GuideText("Conditional or unverified response; no unconditional instruction is inferred.", "Consigne conditionnelle ou non vérifiée : pas de consigne inconditionnelle déduite.",
                "Bedingte oder ungeprüfte Reaktion; keine unbedingte Anweisung abgeleitet.", "条件付き・未確定の対処。無条件の指示は出しません。"));
        if (phase.Context.Length > 0) ImGui.TextWrapped(GuideChecklistPresentation.Preview(GuideContextSummary(boss, phase) ?? phase.Context, 200));
        DrawGuideStatusNames(mechanic);
        ImGui.TextDisabled(GuideText("Hold Shift for full English source", "Maintiens Maj pour la source anglaise complète", "Umschalt halten für die englische Quelle", "Shiftで英語原文を表示"));
        if (ImGui.GetIO().KeyShift)
        {
            ImGui.Separator(); ImGui.TextWrapped(mechanic.Text);
            foreach (var context in boss.Phases.Where(context => context == phase || context.Name.Length == 0))
                if (context.Context.Length > 0) ImGui.TextWrapped(context.Context);
            if (_liveGuide != null) ImGui.TextWrapped(_liveGuide.SourceUrl);
        }
        ImGui.PopTextWrapPos(); ImGui.EndTooltip();
    }
}
