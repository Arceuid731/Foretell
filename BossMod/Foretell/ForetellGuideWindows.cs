using Dalamud.Bindings.ImGui;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private bool _guideEntryDismissed;
    private static Vector4 GuideColor(uint packed) => new((packed & 255) / 255f, ((packed >> 8) & 255) / 255f, ((packed >> 16) & 255) / 255f, (packed >> 24) / 255f);
    internal static ImGuiWindowFlags GuideEntryFlags => ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNavFocus;

    internal static void SetGuideEntryLayout(Vector2 viewportPosition, Vector2 viewportSize)
    {
        ImGui.SetNextWindowPos(viewportPosition + new Vector2(Math.Max(8, viewportSize.X * .5f - 250), Math.Max(8, viewportSize.Y * .12f)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new(500, Math.Min(480, viewportSize.Y * .75f)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new(300, 180), Vector2.Max(new(300, 180), viewportSize - new Vector2(16)));
    }

    private void DrawGuideProgress(GuideSnapshot snapshot)
    {
        if (snapshot.State is not (GuideState.ReadingCache or GuideState.Downloading or GuideState.Preparing)) return;
        if (snapshot.TotalBytes is > 0) ImGui.ProgressBar(Math.Clamp((float)snapshot.ReceivedBytes / snapshot.TotalBytes.Value, 0, 1), new(-1, 0));
        if (snapshot.EstimatedSeconds is { } seconds)
        {
            var remaining = Math.Max(0, seconds - (DateTime.UtcNow - snapshot.StartedAt).TotalSeconds);
            if (remaining > 1) ImGui.TextDisabled(GuideText($"About {remaining:F0}s remaining", $"Encore environ {remaining:F0}s", $"Noch etwa {remaining:F0}s", $"残り約{remaining:F0}秒"));
        }
    }

    private void DrawGuideEntry()
    {
        if (_guideDuty == null || _guideEntryDismissed || _guideCombat || _guides?.Snapshot is not { } snapshot || snapshot.Duty != _guideDuty) return;
        var viewport = ImGui.GetMainViewport();
        SetGuideEntryLayout(viewport.Pos, viewport.Size);
        var open = true;
        if (ImGui.Begin(GuideText("Foretell · instance overview", "Foretell · résumé de l’instance", "Foretell · Instanzübersicht", "Foretell・コンテンツ概要") + "###ForetellGuideEntry", ref open, GuideEntryFlags))
        {
            ImGui.TextColored(GuideColor(_cfg.GuideActiveColor), GuideDutyName(_guideDuty));
            if (PreparedGuide is { } document)
            {
                if (_guideSummaries?.Snapshot?.Stage != "Ready") DrawGuideSummaryProgress(snapshot.Document);
                ImGui.TextDisabled(document.Page?.Provider ?? "");
                if (document.Summary.Length > 0) ImGui.TextWrapped(document.Summary);
                foreach (var boss in document.Bosses)
                {
                    ImGui.Separator();
                    ImGui.TextColored(GuideColor(_cfg.GuideActiveColor), GuideBossName(boss));
                    DrawGuideBossSummary(boss);
                }
                if (ImGui.Button(GuideText("Show mechanics", "Afficher les mécaniques", "Mechaniken anzeigen", "ギミックを表示")))
                { _cfg.GuideSidebar = true; _cfg.Modified.Fire(); open = false; }
            }
            else
            {
                DrawGuideState(snapshot);
                DrawGuideSummaryProgress(snapshot.Document);
            }
            if (ImGui.Button(GuideText("Close", "Fermer", "Schließen", "閉じる"))) open = false;
        }
        ImGui.End();
        if (!open) _guideEntryDismissed = true;
    }

    private void DrawGuideBossSummary(GuideBoss boss)
    {
        if (boss.Summary.Length > 0) ImGui.TextWrapped(boss.Summary);
    }

    private void DrawGuideSettings()
    {
        if (!ImGui.CollapsingHeader(GuideText("Mechanic list", "Liste des mécaniques", "Mechanikliste", "ギミック一覧"), ImGuiTreeNodeFlags.DefaultOpen)) return;
        var changed = ImGui.Checkbox(GuideText("Mechanic list overlay", "Liste des mécaniques en surimpression", "Mechaniklisten-Overlay", "ギミック一覧表示"), ref _cfg.GuideSidebar);
        changed |= ImGui.Checkbox(GuideText("Entry popup", "Panneau à l’entrée", "Fenster beim Betreten", "入場時の案内"), ref _cfg.GuideEntryPopup);
        changed |= ImGui.Checkbox(GuideText("Unlock mechanic list (drag / resize)", "Déverrouiller la liste (déplacer / redimensionner)", "Mechanikliste entsperren (bewegen / skalieren)", "ギミック一覧の移動・サイズ変更を許可"), ref _cfg.GuideChecklistUnlocked);
        changed |= ImGui.Checkbox(GuideText("Follow the current phase", "Suivre la phase actuelle", "Aktueller Phase folgen", "現在のフェーズに追従"), ref _cfg.GuideCurrentPhaseOnly);
        changed |= ImGui.Checkbox(GuideText("Role icons", "Icônes de rôle", "Rollensymbole", "ロールアイコン"), ref _cfg.GuideRoleIcons);
        ImGui.TextDisabled(GuideText("Full guide in Sources and guides", "Guide complet dans Sources et guides", "Vollständige Anleitung unter Quellen und Anleitungen", "攻略の全項目は原典と攻略に表示"));
        changed |= ImGui.SliderFloat(GuideText("List width", "Largeur de la liste", "Listenbreite", "一覧の幅"), ref _cfg.GuideWidth, 260, 1000, "%.0f");
        changed |= ImGui.SliderFloat(GuideText("Preferred height", "Hauteur souhaitée", "Bevorzugte Höhe", "一覧の高さ"), ref _cfg.GuideHeight, 180, 1000, "%.0f");
        changed |= ImGui.SliderFloat(GuideText("Text size", "Taille du texte", "Textgröße", "文字倍率"), ref _cfg.GuideScale, .7f, 2, "%.2f");
        changed |= ImGui.SliderFloat(GuideText("Row spacing", "Espacement des lignes", "Zeilenabstand", "行間"), ref _cfg.GuideRowSpacing, 0, 18, "%.0f");
        changed |= ImGui.SliderFloat(GuideText("Background opacity", "Opacité du fond", "Hintergrunddeckkraft", "背景の不透明度"), ref _cfg.GuidePanelOpacity, 0, 1, "%.2f");
        changed |= EditGuideColor(GuideText("Background", "Fond", "Hintergrund", "背景"), ref _cfg.GuideBackgroundColor);
        changed |= EditGuideColor(GuideText("Boss / phase heading", "Titre du boss / phase", "Boss- / Phasentitel", "ボス・フェーズ見出し"), ref _cfg.GuideHeaderColor);
        changed |= EditGuideColor(GuideText("Mechanic names", "Noms des mécaniques", "Mechaniknamen", "ギミック名"), ref _cfg.GuideTextColor);
        changed |= EditGuideColor(GuideText("Instructions", "Consignes", "Anweisungen", "指示"), ref _cfg.GuideInstructionColor);
        changed |= EditGuideColor(GuideText("Active mechanic", "Mécanique active", "Aktive Mechanik", "発動中のギミック"), ref _cfg.GuideActiveColor);
        if (ImGui.Button(GuideText("Reset layout", "Réinitialiser la disposition", "Layout zurücksetzen", "配置をリセット")))
        { _cfg.GuidePositionX = _cfg.GuidePositionY = -1; _cfg.GuideWidth = 380; _cfg.GuideHeight = 520; _cfg.GuideScale = 1; _cfg.GuideRowSpacing = 6; _guideChecklistWasUnlocked = false; changed = true; }
        if (_guideDuty != null && ImGui.Button(GuideText("Show entry summary again", "Revoir le résumé d’entrée", "Zusammenfassung erneut anzeigen", "入場時の要約を再表示")))
        { _guideEntryDismissed = false; }
        if (changed) { _guideChecklistWasUnlocked = false; _cfg.Modified.Fire(); }
    }


    private string? GuideSummaryFor(GuideBoss boss, GuidePhase phase, GuideMechanic mechanic, GuideDocument? document = null)
        => mechanic.Advice?.Language == GuideContentLanguage ? mechanic.Advice.Description : null;

    private string? GuideContextSummary(GuideBoss boss, GuidePhase phase, GuideDocument? document = null)
        => boss.Summary.Length > 0 ? boss.Summary : null;

    private void DrawGuideSummaryProgress(GuideDocument? document = null)
    {
        document ??= _guides?.Snapshot.Document;
        if (document == null || _guideSummaries?.Snapshot is not { } snapshot || snapshot.SourceHash != document.SourceHash || snapshot.Language != GuideContentLanguage) return;
        var report = _guideSummaries.Diagnostics.Reports.FirstOrDefault(candidate => candidate.Duty == document.Duty
            && candidate.ModelID == snapshot.ModelID && candidate.SourceHash == snapshot.SourceHash);
        var failed = snapshot.Stage is "Failed" or "CacheWriteFailed" || snapshot.Stage.StartsWith("Unavailable", StringComparison.Ordinal);
        var stage = snapshot.Stage switch
        {
            "Ready" => GuideText("Guide ready", "Guide prêt", "Anleitung bereit", "攻略準備完了"),
            "ReadyWithUnresolved" => GuideText("Guide partly ready", "Guide partiellement prêt", "Anleitung teilweise bereit", "攻略の一部が準備完了"),
            "PausedInCombat" => GuideText("Analysis paused during combat", "Analyse en pause pendant le combat", "Analyse im Kampf pausiert", "戦闘中は解析を一時停止"),
            "InstallingOrStarting" => GuideModelStageLabel(_guideSummaries.Runtime.Stage, GuideClientLanguage),
            "PreparationDisabled" => GuideText("Enable preparation in Local AI to read this guide.", "Active la préparation dans IA locale pour lire ce guide.", "Vorbereitung unter Lokale KI aktivieren.", "ローカルAIで準備を有効にしてください。"),
            _ when failed => GuideText("Analysis failed. Retry in Local AI.", "L’analyse a échoué. Relance-la dans IA locale.", "Analyse fehlgeschlagen. Unter Lokale KI erneut versuchen.", "解析に失敗。ローカルAIで再試行。"),
            "Analyzing" or "Summarizing" when report is { FinishedAt: null, Stage: not "Interrupted" } => GuideAnalysisStepLabel(report.Stage)
                + (report.Boss.Length > 0 ? " · " + report.Boss : "") + (report.Attempt > 1 ? " · " + GuideAnalysisAttemptLabel(report.Attempt) : ""),
            _ => GuideAnalysisStepLabel(snapshot.Stage)
        };
        ImGui.TextWrapped(stage);
        if (failed && snapshot.LastIssue is { Length: > 0 } issue)
            DrawGuideJournalText(GuideJournalShortText(issue, 200));
        if (failed && report != null && ImGui.SmallButton(GuideText("View analysis log", "Voir le journal d’analyse", "Analyseprotokoll anzeigen", "解析ログを表示") + "###FailedGuideJournal"))
        {
            _guideJournalReportID = report.ID;
            _guideJournalCallIndex = -1;
            _guideJournalOpen = true;
        }
        if (snapshot.Stage == "Analyzing" && snapshot.Total > 0) ImGui.ProgressBar((float)snapshot.Completed / snapshot.Total, new(-1, 0));
        if (snapshot.RemainingSeconds is { } remaining && remaining > 1) ImGui.TextDisabled(GuideText($"About {remaining:F0}s remaining", $"Encore environ {remaining:F0}s", $"Noch etwa {remaining:F0}s", $"残り約{remaining:F0}秒"));
        if (snapshot.Transfer is { Total: > 0 } transfer)
        {
            ImGui.ProgressBar(Math.Clamp((float)transfer.Received / transfer.Total, 0, 1), new(-1, 0), $"{transfer.Received / 1e6:F0} / {transfer.Total / 1e6:F0} Mo");
            if (transfer.RemainingSeconds is { } seconds) ImGui.TextDisabled($"~{TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 86400)):g}");
        }
    }

    private static bool EditGuideColor(string label, ref uint packed)
    {
        var color = GuideColor(packed);
        if (!ImGui.ColorEdit4(label, ref color, ImGuiColorEditFlags.AlphaBar)) return false;
        packed = ImGui.ColorConvertFloat4ToU32(color);
        return true;
    }
}
