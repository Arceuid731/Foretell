using Dalamud.Bindings.ImGui;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    internal static string GuideModelStageLabel(GuideModelStage stage, GuideLanguage language) => GuidePreparation.Local(language,
        stage switch
        {
            GuideModelStage.Verifying => "Checking files", GuideModelStage.Downloading => "Downloading",
            GuideModelStage.Loading => "Loading model", GuideModelStage.Loaded => "Loaded · idle",
            GuideModelStage.Tokenizing => "Counting tokens", GuideModelStage.Generating => "Inference active",
            GuideModelStage.Stopping => "Unloading", GuideModelStage.Failed => "Process exited unexpectedly", _ => "Unloaded"
        },
        stage switch
        {
            GuideModelStage.Verifying => "Vérification des fichiers", GuideModelStage.Downloading => "Téléchargement",
            GuideModelStage.Loading => "Chargement du modèle", GuideModelStage.Loaded => "Chargé · en attente",
            GuideModelStage.Tokenizing => "Comptage des tokens", GuideModelStage.Generating => "Inférence en cours",
            GuideModelStage.Stopping => "Déchargement", GuideModelStage.Failed => "Arrêt inattendu du processus", _ => "Déchargé"
        },
        stage switch
        {
            GuideModelStage.Verifying => "Dateien prüfen", GuideModelStage.Downloading => "Download",
            GuideModelStage.Loading => "Modell laden", GuideModelStage.Loaded => "Geladen · bereit",
            GuideModelStage.Tokenizing => "Token zählen", GuideModelStage.Generating => "Inferenz aktiv",
            GuideModelStage.Stopping => "Entladen", GuideModelStage.Failed => "Prozess unerwartet beendet", _ => "Nicht geladen"
        },
        stage switch
        {
            GuideModelStage.Verifying => "ファイル検証中", GuideModelStage.Downloading => "ダウンロード中",
            GuideModelStage.Loading => "モデル読込中", GuideModelStage.Loaded => "読込済み・待機中",
            GuideModelStage.Tokenizing => "トークン計数中", GuideModelStage.Generating => "推論中",
            GuideModelStage.Stopping => "解放中", GuideModelStage.Failed => "プロセスの予期せぬ終了", _ => "未読込"
        });

    private void DrawGuideModelStatus(bool details)
    {
        var runtime = _guideSummaries?.Runtime ?? new();
        ImGui.TextColored(runtime.ProcessID != null ? ProductAccent : GuideColor(_cfg.GuideTextColor),
            "IA · " + GuideModelStageLabel(runtime.Stage, GuideClientLanguage)
            + (runtime.ProcessID != null ? $" · {runtime.Backend} · PID {runtime.ProcessID}" : ""));
        if (!details) return;
        ImGui.TextWrapped(GuideText("Active profile: Qwen3-1.7B · Q8_0 · llama.cpp b10809", "Profil actuel : Qwen3-1.7B · Q8_0 · llama.cpp b10809", "Aktives Profil: Qwen3-1.7B · Q8_0 · llama.cpp b10809", "現在のプロファイル：Qwen3-1.7B・Q8_0・llama.cpp b10809"));
        ImGui.TextWrapped(runtime.VerifiedThisSession
            ? GuideText("Model file SHA256 verified during this plugin session. Installed on disk does not mean loaded in memory.", "Fichier du modèle vérifié par SHA256 pendant cette session du plugin. Installé sur disque ne signifie pas chargé en mémoire.", "Modelldatei in dieser Sitzung per SHA256 geprüft. Auf der Festplatte bedeutet nicht im Speicher geladen.", "このセッションでモデルのSHA256検証済み。ディスク上の保存とメモリ読込は別です。")
            : GuideText("Disk files have not been verified in this plugin session; cache-only use does not start or verify the model.", "Fichiers disque non vérifiés pendant cette session du plugin ; consulter le cache ne lance ni ne vérifie le modèle.", "Dateien in dieser Sitzung noch nicht geprüft; Cache-Nutzung startet oder prüft das Modell nicht.", "このセッションではファイル未検証。キャッシュ参照だけではモデルを起動・検証しません。"));
        if (runtime.ContextTokens > 0)
            ImGui.TextWrapped(GuideText($"Last/current process: context {runtime.ContextTokens:N0} tokens · last prompt {runtime.PromptTokens?.ToString("N0") ?? "—"}",
                $"Dernier processus / en cours : contexte {runtime.ContextTokens:N0} tokens · dernier prompt {runtime.PromptTokens?.ToString("N0") ?? "—"}",
                $"Letzter/aktueller Prozess: Kontext {runtime.ContextTokens:N0} Token · Prompt {runtime.PromptTokens?.ToString("N0") ?? "—"}",
                $"直近のプロセス：コンテキスト{runtime.ContextTokens:N0}・プロンプト{runtime.PromptTokens?.ToString("N0") ?? "—"}トークン"));
    }

    private void DrawGuideModelManager()
    {
        DrawGuideModelStatus(true);
        ImGui.Separator();
        var changed = ImGui.Checkbox(GuideText("Enable local AI preparation", "Activer la préparation IA locale", "Lokale KI-Vorbereitung aktivieren", "ローカルAI準備を有効化"), ref _cfg.GuideLocalSummaries);
        ImGui.TextWrapped(GuideText("Downloads missing model/runtime files automatically when an uncached guide excerpt needs preparation outside combat. No cloud inference. Disabling unloads the process; files are retained.",
            "Télécharge automatiquement les fichiers manquants lorsqu’un passage de guide non préparé en cache doit être traité hors combat. Aucune inférence cloud. Désactiver décharge le processus, sans supprimer les fichiers.",
            "Fehlende Dateien werden bei ungecachten Abschnitten außerhalb des Kampfes geladen. Keine Cloud-Inferenz. Deaktivieren entlädt den Prozess, behält Dateien.",
            "非戦闘中に未準備の攻略が必要な場合のみ不足ファイルを自動取得。クラウド推論なし。無効化はプロセスを停止しファイルは保持。"));
        ImGui.BeginDisabled(_guideCombat);
        changed |= ImGui.Checkbox(GuideText("Prefer Vulkan GPU (automatic CPU fallback)", "Préférer le GPU Vulkan (repli CPU automatique)", "Vulkan bevorzugen (CPU-Fallback)", "Vulkan優先（CPUへ自動切替）"), ref _cfg.GuideSummaryGpu);
        if (ImGui.BeginCombo(GuideText("Context budget", "Budget de contexte", "Kontextbudget", "コンテキスト上限"), $"{_cfg.GuideContextTokens:N0} tokens"))
        {
            foreach (var size in new[] { 4096, 8192, 16384, 32768 })
                if (ImGui.Selectable($"{size:N0}", _cfg.GuideContextTokens == size)) { _cfg.GuideContextTokens = size; changed = true; }
            ImGui.EndCombo();
        }
        changed |= ImGui.SliderInt(GuideText("Committed RAM cap (GiB)", "Plafond de RAM engagée (Gio)", "RAM-Limit (GiB)", "コミットRAM上限（GiB）"), ref _cfg.GuideMemoryGiB, 4, 12);
        if (ImGui.Button(GuideText("Retry unresolved excerpts", "Relancer les passages non préparés", "Ungeklärte Abschnitte erneut versuchen", "未準備の文章を再試行"))) _guideSummaries?.Retry();
        ImGui.EndDisabled();
        if (changed) _cfg.Modified.Fire();
        ImGui.TextWrapped(GuideText("Model: 1.83 GB on disk + runtime 18–35 MB. 2 CPU threads, 15% total CPU cap. RAM cap is not an allocation or a VRAM limit; a larger context increases memory and preparation time. Settings apply to the next process; prepared cache is retained.",
            "Modèle : 1,83 Go sur disque + moteur 18–35 Mo. 2 threads CPU, plafond de 15 % du CPU total. Le plafond RAM n’est ni une allocation ni une limite de VRAM ; un contexte plus grand augmente la mémoire et le temps de préparation. Réglages appliqués au prochain processus ; cache préparé conservé.",
            "Modell: 1,83 GB + Laufzeit 18–35 MB. 2 CPU-Threads, 15% Gesamt-CPU. RAM-Limit ist keine Reservierung oder VRAM-Grenze. Größerer Kontext kostet Speicher und Zeit. Einstellungen gelten ab nächstem Prozess; Cache bleibt erhalten.",
            "モデル1.83 GB＋実行環境18～35 MB。CPU 2スレッド・全体の15%上限。RAM上限は予約やVRAM制限ではありません。コンテキスト拡大はメモリと準備時間を増やします。設定は次のプロセスから適用、キャッシュ保持。"));
        ImGui.TextWrapped(GuideText("Full prompt token count includes the chat template and output reserve. Overflow is reported, never silently clipped. Combat cancels inference and unloads the process; reading prepared results does not keep it running.",
            "Comptage du prompt complet, gabarit de conversation et réserve de réponse inclus. Tout dépassement est signalé, jamais coupé silencieusement. Le combat annule l’inférence et décharge le processus ; consulter les résultats préparés ne le maintient pas actif.",
            "Tokenzählung mit Vorlage und Ausgabereserve. Überschreitung wird gemeldet, nie still gekürzt. Im Kampf wird abgebrochen und entladen. Cache-Lesen hält keinen Prozess aktiv.",
            "テンプレートと出力予約を含むトークン計数。超過は明示し、切り捨てません。戦闘時は推論を中止し解放。準備済み結果の参照で起動は継続しません。"));
        DrawGuideSummaryProgress(_liveGuide ?? _guides?.Snapshot.Document);
        ImGui.Separator();
        ImGui.TextWrapped(GuideText("Current limitation: AI translates already-extracted excerpts. Whole-page semantic boss/mechanic extraction and additional source providers are not implemented yet. A new model alone does not fix the parser.",
            "Limite actuelle : l’IA traduit des passages déjà extraits. L’analyse sémantique de la page entière en boss/mécaniques et les autres fournisseurs de guides restent à implémenter. Changer de modèle seul ne corrige pas le parseur.",
            "Aktuell übersetzt KI bereits extrahierte Abschnitte. Semantische Ganzseitenanalyse und weitere Quellen fehlen noch. Ein Modellwechsel allein repariert den Parser nicht.",
            "現在のAIは抽出済み文章を翻訳。ページ全体の意味解析と他の情報源は未実装。モデル変更だけではパーサーは修正されません。"));
    }

    private void DrawInstanceDashboard()
    {
        ImGui.TextColored(ProductAccent, GuideText("GUIDE → CURRENT BOSS → LIVE SIGNAL", "GUIDE → BOSS ACTUEL → SIGNAL EN JEU", "ANLEITUNG → BOSS → LIVE-SIGNAL", "攻略→現在のボス→ゲーム内シグナル"));
        if (!_cfg.EnableGuides) ImGui.TextWrapped(GuideText("Automatic guides disabled. Enable them in Sources & guides.", "Guides automatiques désactivés. Activation dans Sources et guides.", "Automatische Anleitungen deaktiviert. Aktivierung unter Quellen.", "攻略の自動取得は無効。原典タブから有効化。"));
        else if (_guideDuty != null && _guides?.Snapshot is { } snapshot && snapshot.Duty == _guideDuty) DrawGuideState(snapshot);
        else ImGui.TextWrapped(GuideText("The current instance is detected automatically. No hardcoded duty list.", "L’instance actuelle est détectée automatiquement. Aucune liste d’instances codée en dur.", "Instanzen werden automatisch erkannt, ohne feste Liste.", "コンテンツを自動検出。固定リストはありません。"));
        DrawGuideSummaryProgress();
        if (_cfg.Mode is ForetellMode.Observe or ForetellMode.Legacy)
            ImGui.TextWrapped(GuideText("Foretell combat display is hidden in this mode. Hybrid displays BMR and Foretell together.", "L’affichage Foretell en combat est masqué dans ce mode. Hybrid affiche BMR et Foretell ensemble.", "Foretell-Kampfanzeige ist in diesem Modus verborgen. Hybrid zeigt BMR und Foretell.", "このモードではForetell戦闘表示は非表示。HybridでBMRと同時表示。"));
        ImGui.Separator();
        if (_guideFrame.Boss is { } boss && _cfg.EnableGuides)
        {
            ImGui.TextColored(ProductAccent, (_guideFrame.Upcoming ? GuideText("Next: ", "À venir : ", "Als Nächstes: ", "次：") : GuideText("Current: ", "Actuel : ", "Aktuell: ", "現在：")) + GuideBossName(boss));
            var signals = LiveGuideSignals().ToArray();
            foreach (var phase in boss.Phases)
                foreach (var mechanic in phase.Mechanics)
                {
                    var live = signals.FirstOrDefault(signal => signal.Boss == boss && signal.Phase == phase && signal.Mechanic == mechanic);
                    var instruction = GuideChecklistInstruction(boss, phase, mechanic, live);
                    ImGui.TextColored(GuideColor(live != null ? _cfg.GuideActiveColor : _cfg.GuideTextColor), GuideMechanicName(boss, mechanic) + " — " + instruction);
                    if (ImGui.IsItemHovered()) DrawGuideMechanicTooltip(boss, phase, mechanic, instruction);
                }
            if (!boss.Phases.Any(phase => phase.Mechanics.Length > 0))
                ImGui.TextWrapped(GuideText("Narrative guide only: consult Sources; no named mechanic can trigger a guide alert yet.", "Guide narratif uniquement : voir Sources ; aucune mécanique nommée ne peut encore déclencher d’alerte du guide.", "Nur Textanleitung: siehe Quellen. Noch keine benannten Mechanik-Warnungen.", "文章形式のみ。原典を参照。名前付きギミック警告は未対応。"));
        }
        else ImGui.TextWrapped(_guideFrame.Ambiguous
            ? GuideText("Ambiguous boss identity: no mechanics from another boss are displayed.", "Identité du boss ambiguë : aucune mécanique d’un autre boss n’est affichée.", "Boss mehrdeutig: keine fremden Mechaniken angezeigt.", "ボスの特定が曖昧なため他ボスのギミックは表示しません。")
            : GuideText("Waiting for a guide and an identified boss.", "En attente d’un guide et d’un boss identifié.", "Warte auf Anleitung und erkannten Boss.", "攻略とボス特定を待機中。"));
        ImGui.Separator();
        ImGui.TextWrapped(GuideText("Guide cues supplement BMR and observed client signals. Radar/3D use established geometry, never an AI-drawn guess. Display configures overlays; Advanced keeps observation memory, timelines and Analysis ZIP exports.",
            "Les consignes du guide complètent BMR et les signaux du jeu. Le radar et la 3D utilisent la géométrie établie, jamais une zone inventée par l’IA. Affichage règle les overlays ; Avancé conserve la mémoire observée, les timelines et les exports Analysis ZIP.",
            "Anleitungen ergänzen BMR und Client-Signale. Radar/3D verwenden belegte Geometrie, keine KI-Vermutung. Anzeige konfiguriert Overlays; Erweitert enthält Beobachtungen und Analysis-Exporte.",
            "攻略はBMRとゲーム内シグナルを補完。レーダーと3Dは確立した形状のみ使用。表示でオーバーレイ設定、詳細で観測記録とAnalysis出力。"));
        if (_guideDuty != null && ImGui.Button(GuideText("Reopen entry panel", "Revoir le panneau d’entrée", "Eintrittsfenster erneut öffnen", "入場パネルを再表示"))) _guideEntryDismissed = false;
    }

    private void DrawGuideAdvanced()
    {
        DrawObservedContentSelector();
        if (!ImGui.BeginTabBar("ForetellAdvancedTabs")) return;
        try
        {
            DrawInspectorTab(GuideText("Observed memory", "Mémoire observée", "Beobachtungen", "観測メモリ"), DrawKnowledgeExplorer);
            DrawInspectorTab(GuideText("Calibration", "Calibration", "Kalibrierung", "較正"), DrawDashboard);
            DrawInspectorTab("Timeline", DrawInspectorTimeline);
            DrawInspectorTab(GuideText("Analysis / recordings", "Analysis / enregistrements", "Analyse / Aufnahmen", "解析・記録"), DrawInspectorReplay);
            DrawInspectorTab(GuideText("Learning / storage", "Observation / stockage", "Lernen / Speicher", "学習・保存"), DrawLearningSettings);
            DrawInspectorTab(GuideText("Diagnostics", "Diagnostics", "Diagnose", "診断"), DrawDiagnostics);
        }
        finally { ImGui.EndTabBar(); }
    }
}
