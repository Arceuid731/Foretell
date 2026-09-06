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

    private bool GuideAnalysisPaused => _guideCombat && _cfg.GuidePauseInCombat;

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
        DrawGuideJournalWindow();
        var runtime = _guideSummaries?.Runtime ?? new();
        var active = runtime.Stage is not (GuideModelStage.Unloaded or GuideModelStage.Failed);
        if (!details && !active && runtime.ModelID != GuideModelCatalog.Get(_cfg.GuideModelID).ID)
            runtime = new(ModelID: GuideModelCatalog.Get(_cfg.GuideModelID).ID);
        var profile = GuideModelCatalog.Get(active || details ? runtime.ModelID : _cfg.GuideModelID);
        ImGui.TextDisabled(profile.Name + " · " + GuideModelStageLabel(runtime.Stage, GuideClientLanguage));
        if (runtime.ModelID == profile.ID)
        {
            if (runtime.Backend.Length > 0)
                ImGui.TextDisabled((runtime.ProcessID != null
                    ? GuideText("Backend", "Moteur", "Backend", "バックエンド")
                    : GuideText("Last backend", "Dernier moteur", "Letztes Backend", "前回のバックエンド")) + ": " + runtime.Backend);
            if (runtime.ContextTokens > 0)
            {
                var prompt = runtime.PromptTokens?.ToString() ?? "—";
                var label = runtime.ProcessID != null
                    ? GuideText("Prompt tokens", "Tokens du prompt", "Prompt-Tokens", "プロンプトトークン")
                    : GuideText("Last prompt tokens", "Tokens du dernier prompt", "Letzte Prompt-Tokens", "前回のプロンプトトークン");
                ImGui.TextDisabled(label + ": " + prompt + " · " + GuideText("Context", "Contexte", "Kontext", "コンテキスト") + $": {runtime.ContextTokens}");
            }
        }
        if (!details) return;
        ImGui.TextWrapped($"Process: {runtime.ProcessID?.ToString() ?? "—"}");
        ImGui.TextWrapped(profile.Revision);
        if (_guideSummaries?.Snapshot?.LastIssue is { } issue) ImGui.TextWrapped(issue);
    }

    internal static string GuideModelFileLabel(GuideModelFileStatus file, GuideLanguage language)
    {
        var state = file.State switch
        {
            GuideModelFileState.OnDisk => GuidePreparation.Local(language, "On disk", "Sur le disque", "Auf Datenträger", "保存済み"),
            GuideModelFileState.Partial => GuidePreparation.Local(language, "Download saved", "Téléchargement conservé", "Download gespeichert", "ダウンロード保存済み"),
            GuideModelFileState.Missing => GuidePreparation.Local(language, "Download required", "À télécharger", "Download erforderlich", "ダウンロードが必要"),
            GuideModelFileState.InvalidSize => GuidePreparation.Local(language, "Download again", "À retélécharger", "Erneut herunterladen", "再ダウンロードが必要"),
            _ => GuidePreparation.Local(language, "File status unavailable", "État des fichiers indisponible", "Dateistatus nicht verfügbar", "ファイル状態を取得できません")
        };
        var megabytes = GuidePreparation.Local(language, "MB", "Mo", "MB", "MB");
        var size = file.State == GuideModelFileState.Partial ? $"{file.Bytes / 1e6:F1} / {file.Total / 1e6:F1} {megabytes}"
            : file.Total >= 1e9 ? $"{file.Total / 1e9:F2} " + GuidePreparation.Local(language, "GB", "Go", "GB", "GB")
            : $"{file.Total / 1e6:F1} {megabytes}";
        return state + " · " + size;
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
        if (_guideSummaries?.Storage(selected.ID, _cfg.GuideSummaryGpu) is { } storage)
        {
            ImGui.TextDisabled(GuideText("Model", "Modèle", "Modell", "モデル") + ": " + GuideModelFileLabel(storage.Model, GuideClientLanguage));
            ImGui.TextDisabled(GuideText("Engine", "Moteur", "Laufzeit", "エンジン") + $" ({(_cfg.GuideSummaryGpu ? "Vulkan" : "CPU")}): " + GuideModelFileLabel(storage.Engine, GuideClientLanguage));
        }
        DrawGuideModelStatus(false);
        DrawGuideSummaryProgress(_guides?.Snapshot.Document);
        if (_guideSummaries?.Snapshot is { } snapshot && snapshot.ModelID == selected.ID && snapshot.SourceHash == _guides?.Snapshot.Document?.SourceHash
            && snapshot.Language == GuideContentLanguage && (snapshot.AnalysisTiming.StartedAt != null || snapshot.AnalysisTiming.Seconds > 0))
        {
            var elapsed = snapshot.AnalysisTiming.Elapsed(System.Diagnostics.Stopwatch.GetTimestamp());
            ImGui.TextDisabled(GuideText($"Analysis time: {elapsed:F0}s", $"Temps d’analyse : {elapsed:F0}s", $"Analysezeit: {elapsed:F0}s", $"解析時間：{elapsed:F0}秒"));
        }
        changed |= ImGui.Checkbox(GuideText("Prepare guides automatically", "Préparer les guides automatiquement", "Anleitungen automatisch vorbereiten", "攻略を自動準備"), ref _cfg.GuideLocalSummaries);
        changed |= ImGui.Checkbox(GuideText("Pause analysis during combat", "Mettre l’analyse en pause en combat", "Analyse im Kampf pausieren", "戦闘中は解析を一時停止"), ref _cfg.GuidePauseInCombat);
        changed |= ImGui.Checkbox(GuideText("Use graphics card", "Utiliser la carte graphique", "Grafikkarte verwenden", "GPUを使用"), ref _cfg.GuideSummaryGpu);
        ImGui.BeginDisabled(GuideAnalysisPaused || _guides?.Snapshot.Document == null);
        if (ImGui.Button(GuideText("Analyze again", "Relancer l’analyse", "Erneut analysieren", "再解析"))) _guideSummaries?.Retry(true);
        ImGui.EndDisabled();
        if (GuideAnalysisPaused) ImGui.TextDisabled(GuideText("Analysis resumes after combat.", "L’analyse reprend après le combat.", "Analyse wird nach dem Kampf fortgesetzt.", "戦闘後に解析を再開。"));
        DrawGuideAnalysisLogSettings();
        if (ImGui.CollapsingHeader(GuideText("Performance", "Performances", "Leistung", "性能")))
        {
            ImGui.BeginDisabled(GuideAnalysisPaused);
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
