using Dalamud.Bindings.ImGui;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private void DrawGuideProviderStatus(GuideDocument? document)
    {
        if (document?.Providers is not { Length: > 0 })
        {
            ImGui.TextWrapped("Community workbook · Raven’s Reminders · Console Games Wiki · Gamer Escape");
            return;
        }
        foreach (var provider in document.Providers)
        {
            var state = provider.Status switch
            {
                "Ready" => GuideText("Up to date", "À jour", "Aktuell", "最新"),
                "Cached" => GuideText("Saved copy", "Copie enregistrée", "Gespeicherte Kopie", "保存済み"),
                "Missing" => GuideText("No guide", "Pas de guide", "Keine Anleitung", "攻略なし"),
                _ => GuideText("Unavailable", "Indisponible", "Nicht verfügbar", "取得不可")
            };
            ImGui.TextUnformatted(provider.Provider + " · " + state);
            var page = document.Sources.FirstOrDefault(source => source.Provider == provider.Provider);
            if (page != null)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton(GuideText("Open", "Ouvrir", "Öffnen", "開く") + "###Source" + provider.Provider)) Dalamud.Utility.Util.OpenLink(page.Url);
            }
        }
    }

    private GuideDocument? PreparedGuide => _guideSummaries?.Snapshot is { Prepared: { } document } snapshot
        && snapshot.SourceHash == _guides?.Snapshot.Document?.SourceHash && snapshot.Language == GuideContentLanguage
        && snapshot.ModelID == GuideModelCatalog.Get(_cfg.GuideModelID).ID ? document : null;

    internal static string GuideModelStageLabel(GuideModelStage stage, GuideLanguage language) => GuidePreparation.Local(language,
        stage switch
        {
            GuideModelStage.Downloading => "Downloading", GuideModelStage.Verifying => "Checking download",
            GuideModelStage.Loading => "Starting", GuideModelStage.Loaded => "Ready",
            GuideModelStage.Tokenizing or GuideModelStage.Generating => "Analyzing guide",
            GuideModelStage.Stopping => "Stopping", GuideModelStage.Failed => "Stopped", _ => "At rest"
        },
        stage switch
        {
            GuideModelStage.Downloading => "Téléchargement", GuideModelStage.Verifying => "Vérification du téléchargement",
            GuideModelStage.Loading => "Démarrage", GuideModelStage.Loaded => "Prêt",
            GuideModelStage.Tokenizing or GuideModelStage.Generating => "Analyse du guide",
            GuideModelStage.Stopping => "Arrêt en cours", GuideModelStage.Failed => "Arrêté", _ => "Au repos"
        },
        stage switch
        {
            GuideModelStage.Downloading => "Download", GuideModelStage.Verifying => "Download prüfen",
            GuideModelStage.Loading => "Startet", GuideModelStage.Loaded => "Bereit",
            GuideModelStage.Tokenizing or GuideModelStage.Generating => "Anleitung analysieren",
            GuideModelStage.Stopping => "Wird beendet", GuideModelStage.Failed => "Beendet", _ => "Inaktiv"
        },
        stage switch
        {
            GuideModelStage.Downloading => "ダウンロード中", GuideModelStage.Verifying => "検証中",
            GuideModelStage.Loading => "起動中", GuideModelStage.Loaded => "準備完了",
            GuideModelStage.Tokenizing or GuideModelStage.Generating => "攻略を解析中",
            GuideModelStage.Stopping => "停止中", GuideModelStage.Failed => "停止", _ => "休止中"
        });

    private void DrawGuideModelStatus(bool details)
    {
        var runtime = _guideSummaries?.Runtime ?? new();
        var profile = GuideModelCatalog.Get(runtime.ProcessID != null ? runtime.ModelID : _cfg.GuideModelID);
        ImGui.TextDisabled(profile.Name + " · " + GuideModelStageLabel(runtime.Stage, GuideClientLanguage));
        if (!details) return;
        ImGui.TextWrapped($"Process: {runtime.ProcessID?.ToString() ?? "—"} · {runtime.Backend} · context {runtime.ContextTokens} · prompt {runtime.PromptTokens?.ToString() ?? "—"}");
        ImGui.TextWrapped(profile.Revision);
        if (_guideSummaries?.Snapshot?.LastIssue is { } issue) ImGui.TextWrapped(issue);
    }

    private void DrawGuideModelManager()
    {
        var selected = GuideModelCatalog.Get(_cfg.GuideModelID);
        var changed = false;
        ImGui.SetNextItemWidth(300);
        if (ImGui.BeginCombo(GuideText("Model", "Modèle", "Modell", "モデル"), selected.Name))
        {
            foreach (var profile in GuideModelCatalog.Profiles)
                if (ImGui.Selectable(profile.Name, profile.ID == selected.ID)) { _cfg.GuideModelID = profile.ID; changed = true; }
            ImGui.EndCombo();
        }
        selected = GuideModelCatalog.Get(_cfg.GuideModelID);
        ImGui.TextDisabled(GuideText($"First download: {selected.Asset.Bytes / 1e9:F2} GB", $"Premier téléchargement : {selected.Asset.Bytes / 1e9:F2} Go",
            $"Erster Download: {selected.Asset.Bytes / 1e9:F2} GB", $"初回ダウンロード：{selected.Asset.Bytes / 1e9:F2} GB"));
        DrawGuideModelStatus(false);
        DrawGuideSummaryProgress(_guides?.Snapshot.Document);
        changed |= ImGui.Checkbox(GuideText("Prepare guides automatically", "Préparer les guides automatiquement", "Anleitungen automatisch vorbereiten", "攻略を自動準備"), ref _cfg.GuideLocalSummaries);
        changed |= ImGui.Checkbox(GuideText("Use graphics card", "Utiliser la carte graphique", "Grafikkarte verwenden", "GPUを使用"), ref _cfg.GuideSummaryGpu);
        ImGui.BeginDisabled(_guideCombat || _guides?.Snapshot.Document == null);
        if (ImGui.Button(GuideText("Analyze again", "Relancer l’analyse", "Erneut analysieren", "再解析"))) _guideSummaries?.Retry(true);
        ImGui.EndDisabled();
        if (_guideCombat) ImGui.TextDisabled(GuideText("Analysis resumes after combat.", "L’analyse reprend après le combat.", "Analyse wird nach dem Kampf fortgesetzt.", "戦闘後に解析を再開。"));
        if (ImGui.CollapsingHeader(GuideText("Performance", "Performances", "Leistung", "性能")))
        {
            ImGui.BeginDisabled(_guideCombat);
            changed |= ImGui.SliderInt(GuideText("Maximum RAM (GiB)", "RAM maximale (Gio)", "Maximaler RAM (GiB)", "最大RAM（GiB）"), ref _cfg.GuideMemoryGiB, 4, 12);
            if (ImGui.BeginCombo(GuideText("Maximum context", "Contexte maximal", "Maximaler Kontext", "最大コンテキスト"), $"{_cfg.GuideContextTokens / 1024}K"))
            {
                foreach (var size in new[] { 8192, 16384, 32768, 65536, 131072 })
                    if (ImGui.Selectable($"{size / 1024}K", size == _cfg.GuideContextTokens)) { _cfg.GuideContextTokens = size; changed = true; }
                ImGui.EndCombo();
            }
            ImGui.TextWrapped(GuideText("Reduce these settings if the game slows down.", "Réduis ces réglages si le jeu ralentit.", "Bei Spielruckeln diese Werte reduzieren.", "ゲームが重い場合は設定を下げてください。"));
            ImGui.EndDisabled();
        }
        if (changed) _cfg.Modified.Fire();
    }

    private void DrawInstanceDashboard()
    {
        if (_guideDuty == null)
        {
            ImGui.TextWrapped(GuideText("Enter an instance or choose a guide in Sources and guides.", "Entre dans une instance ou choisis un guide dans Sources et guides.",
                "Instanz betreten oder unter Quellen eine Anleitung wählen.", "コンテンツに入場するか原典から攻略を選択。"));
            return;
        }
        if (!_cfg.EnableGuides)
        {
            if (ImGui.Button(GuideText("Enable guides", "Activer les guides", "Anleitungen aktivieren", "攻略を有効化"))) { _cfg.EnableGuides = true; _cfg.Modified.Fire(); }
            return;
        }
        DrawGuideSummaryProgress();
        if (_cfg.Mode is ForetellMode.Observe or ForetellMode.Legacy)
            if (ImGui.Button(GuideText("Enable Foretell alerts", "Activer les alertes Foretell", "Foretell-Warnungen aktivieren", "Foretell警告を有効化"))) SetMode(ForetellMode.Foretell);
        if (_guideFrame.Boss is { } boss)
        {
            ImGui.TextColored(ProductAccent, (_guideFrame.Upcoming ? GuideText("Next: ", "À venir : ", "Als Nächstes: ", "次：") : "") + GuideBossName(boss));
            if (boss.Summary.Length > 0) ImGui.TextWrapped(boss.Summary);
            ImGui.Separator();
            var signals = LiveGuideSignals().ToArray();
            foreach (var phase in boss.Phases)
                foreach (var mechanic in phase.Mechanics)
                {
                    var live = signals.FirstOrDefault(signal => signal.Boss == boss && signal.Phase == phase && signal.Mechanic == mechanic);
                    var instruction = GuideChecklistInstruction(boss, phase, mechanic, live);
                    ImGui.TextColored(GuideColor(live != null ? _cfg.GuideActiveColor : _cfg.GuideTextColor), GuideMechanicName(boss, mechanic) + " — " + GuideRolePresentation.Prefix(mechanic.Advice?.Roles ?? []) + instruction);
                    if (ImGui.IsItemHovered()) DrawGuideMechanicTooltip(boss, phase, mechanic, instruction);
                }
        }
        else if (_liveGuide == null && _guides?.Snapshot is { } snapshot) DrawGuideState(snapshot);
        else ImGui.TextWrapped(_guideFrame.Ambiguous
            ? GuideText("Waiting to identify the boss.", "Identification du boss en cours.", "Boss wird erkannt.", "ボス特定中。")
            : GuideText("No upcoming boss.", "Aucun boss à venir.", "Kein weiterer Boss.", "次のボスはいません。"));
        if (ImGui.Button(GuideText("Instance overview", "Résumé de l’instance", "Instanzübersicht", "コンテンツ概要"))) _guideEntryDismissed = false;
    }

    private void DrawGuideAdvanced()
    {
        if (ImGui.CollapsingHeader(GuideText("Presentation mode", "Mode d’affichage", "Anzeigemodus", "表示モード")))
        {
            foreach (var mode in Enum.GetValues<ForetellMode>())
            {
                DrawModeButton(mode);
                ImGui.SameLine();
                ImGui.TextWrapped(ModeDescription(mode));
            }
        }
        DrawObservedContentSelector();
        if (!ImGui.BeginTabBar("ForetellAdvancedTabs")) return;
        try
        {
            DrawInspectorTab(GuideText("Observed memory", "Mémoire observée", "Beobachtungen", "観測メモリ"), DrawKnowledgeExplorer);
            DrawInspectorTab(GuideText("Calibration", "Calibration", "Kalibrierung", "較正"), DrawDashboard);
            DrawInspectorTab("Timeline", DrawInspectorTimeline);
            DrawInspectorTab(GuideText("Analysis / recordings", "Analysis / enregistrements", "Analyse / Aufnahmen", "解析・記録"), DrawInspectorReplay);
            DrawInspectorTab(GuideText("Learning / storage", "Observation / stockage", "Lernen / Speicher", "学習・保存"), DrawLearningSettings);
            DrawInspectorTab(GuideText("Diagnostics", "Diagnostics", "Diagnose", "診断"), () => { DrawGuideModelStatus(true); DrawDiagnostics(); });
        }
        finally { ImGui.EndTabBar(); }
    }
}
