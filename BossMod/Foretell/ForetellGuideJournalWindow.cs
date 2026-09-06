using Dalamud.Bindings.ImGui;
using System.Text.Json;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private bool _guideJournalOpen;
    private int _guideJournalDrawFrame = -1;
    private string _guideJournalReportID = "";
    private int _guideJournalCallIndex = -1;
    private Task<string>? _guideJournalExportTask;
    private string _guideJournalExportReportID = "";
    private string _guideJournalExportPath = "";
    private string _guideJournalExportError = "";

    internal static ImGuiWindowFlags GuideJournalFlags => ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNavFocus;

    private string GuideAnalysisStepLabel(string stage) => stage switch
    {
        "Outline" => GuideText("Reading sources", "Lecture des sources", "Quellen lesen", "原典を読み込み中"),
        "Draft" => GuideText("Preparing mechanics", "Préparation des mécaniques", "Mechaniken vorbereiten", "ギミックを準備中"),
        "Review" => GuideText("Reviewing instructions", "Relecture des consignes", "Anweisungen prüfen", "指示を見直し中"),
        "Validation" => GuideText("Checking the result", "Vérification du résultat", "Ergebnis prüfen", "結果を確認中"),
        "Split" => GuideText("Reading a long guide", "Lecture d’un guide long", "Lange Anleitung lesen", "長い攻略を読み込み中"),
        "Cache" => GuideText("Loading saved guide", "Chargement du guide enregistré", "Gespeicherte Anleitung laden", "保存済み攻略を読み込み中"),
        "Queued" => GuideText("Waiting to analyze", "Analyse en attente", "Analyse vorgemerkt", "解析待ち"),
        "Ready" => GuideText("Guide ready", "Guide prêt", "Anleitung bereit", "攻略準備完了"),
        "PausedInCombat" => GuideText("Paused during combat", "En pause pendant le combat", "Im Kampf pausiert", "戦闘中は一時停止"),
        "InstallingOrStarting" => GuideText("Starting the model", "Démarrage du modèle", "Modell starten", "モデルを起動中"),
        "Failed" => GuideText("Analysis failed", "Échec de l’analyse", "Analyse fehlgeschlagen", "解析失敗"),
        "Cancelled" => GuideText("Analysis cancelled", "Analyse annulée", "Analyse abgebrochen", "解析をキャンセル"),
        "Interrupted" => GuideText("Analysis interrupted", "Analyse interrompue", "Analyse unterbrochen", "解析中断"),
        "Analyzing" or "Summarizing" => GuideText("Analyzing guide", "Analyse du guide", "Anleitung analysieren", "攻略を解析中"),
        _ => stage
    };

    private string GuideAnalysisAttemptLabel(int attempt) => GuideText($"Attempt {attempt}", $"Essai {attempt}", $"Versuch {attempt}", $"試行 {attempt}");

    private static string GuideJournalShortText(string text, int limit = 180)
    {
        var lineEnd = text.IndexOfAny(['\r', '\n']);
        var length = Math.Min(lineEnd >= 0 ? lineEnd : text.Length, limit);
        return text[..length].Trim() + (length < text.Length ? "…" : "");
    }

    private static void DrawGuideJournalText(string text)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    private GuideAnalysisReport? GuideJournalReport(GuideAnalysisReport[] reports)
    {
        var selected = reports.FirstOrDefault(report => report.ID == _guideJournalReportID);
        if (selected != null) return selected;
        selected = reports.FirstOrDefault();
        _guideJournalReportID = selected?.ID ?? "";
        _guideJournalCallIndex = -1;
        return selected;
    }

    private string GuideJournalReportLabel(GuideAnalysisReport report)
        => $"{report.StartedAt.ToLocalTime():dd/MM HH:mm:ss} · {GuideJournalShortText(GuideDutyName(report.Duty), 80)} · {GuideAnalysisStepLabel(report.Stage)}";

    private GuideAnalysisReport? DrawGuideJournalReportSelector(GuideAnalysisReport[] reports)
    {
        var selected = GuideJournalReport(reports);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("###GuideAnalysisReport", selected == null
            ? GuideText("No analysis recorded", "Aucune analyse enregistrée", "Keine Analyse aufgezeichnet", "解析記録なし")
            : GuideJournalReportLabel(selected)))
        {
            foreach (var report in reports)
            {
                ImGui.PushID(report.ID);
                if (ImGui.Selectable(GuideJournalReportLabel(report).Replace("#", "＃"), report.ID == selected?.ID))
                {
                    _guideJournalReportID = report.ID;
                    _guideJournalCallIndex = -1;
                    selected = report;
                }
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }
        return selected;
    }

    private void DrawGuideAnalysisLogSettings()
    {
        if (!ImGui.CollapsingHeader(GuideText("Analysis log", "Journal d’analyse", "Analyseprotokoll", "解析ログ") + "###GuideAnalysisLog")) return;
        if (ImGui.Checkbox(GuideText("Record prompts and responses", "Enregistrer les prompts et réponses", "Prompts und Antworten aufzeichnen", "プロンプトと応答を記録"), ref _cfg.GuideDebugConversation))
            _cfg.Modified.Fire();
        var reports = _guideSummaries?.Diagnostics.Reports ?? [];
        ImGui.TextUnformatted(GuideText("Recent analyses", "Analyses récentes", "Letzte Analysen", "最近の解析"));
        var selected = DrawGuideJournalReportSelector(reports);
        ImGui.BeginDisabled(selected == null);
        if (ImGui.Button(GuideText("View analysis log", "Voir le journal d’analyse", "Analyseprotokoll anzeigen", "解析ログを表示"))) _guideJournalOpen = true;
        ImGui.EndDisabled();
        if (selected?.RecordingError is { Length: > 0 } recordingError)
            DrawGuideJournalText(GuideText("Log could not be saved: ", "Journal non enregistré : ", "Protokoll nicht gespeichert: ", "ログを保存できません：") + GuideJournalShortText(recordingError));
    }

    private void DrawGuideJournalWindow()
    {
        if (!_guideJournalOpen || _guideJournalDrawFrame == ImGui.GetFrameCount()) return;
        _guideJournalDrawFrame = ImGui.GetFrameCount();
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowSize(Vector2.Min(new(980, 740), viewport.Size * .9f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new(440, 320), Vector2.Max(new(440, 320), viewport.Size - new Vector2(16)));
        var visible = ImGui.Begin(GuideText("Foretell · analysis log", "Foretell · journal d’analyse", "Foretell · Analyseprotokoll", "Foretell・解析ログ") + "###ForetellGuideJournal",
            ref _guideJournalOpen, GuideJournalFlags);
        try
        {
            if (!visible) return;
            var report = DrawGuideJournalReportSelector(_guideSummaries?.Diagnostics.Reports ?? []);
            if (report == null) return;
            DrawGuideJournalReportHeader(report);
            DrawGuideJournalExport(report);
            ImGui.Separator();
            if (!ImGui.BeginTabBar("GuideJournalTabs")) return;
            try
            {
                if (ImGui.BeginTabItem(GuideText("Requests", "Requêtes", "Anfragen", "リクエスト") + "###Requests"))
                {
                    DrawGuideJournalCalls(report);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem(GuideText("Steps and errors", "Étapes et erreurs", "Schritte und Fehler", "手順とエラー") + "###Events"))
                {
                    DrawGuideJournalEvents(report);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem(GuideText("Engine log", "Journal du moteur", "Laufzeitprotokoll", "エンジンログ") + "###Runtime"))
                {
                    DrawGuideJournalTextPane("EngineLog", report.RuntimeLog);
                    ImGui.EndTabItem();
                }
            }
            finally { ImGui.EndTabBar(); }
        }
        finally { ImGui.End(); }
    }

    private void DrawGuideJournalReportHeader(GuideAnalysisReport report)
    {
        DrawGuideJournalText(GuideDutyName(report.Duty) + " · " + report.ModelID);
        DrawGuideJournalText(GuideAnalysisStepLabel(report.Stage) + (report.Boss.Length > 0 ? " · " + report.Boss : "")
            + (report.Attempt > 0 ? " · " + GuideAnalysisAttemptLabel(report.Attempt) : ""));
        var started = report.StartedAt.ToLocalTime().ToString("g");
        var finished = report.FinishedAt?.ToLocalTime().ToString("g") ?? "—";
        DrawGuideJournalText(GuideText($"Started: {started} · Finished: {finished} · {report.Calls.Length} requests",
            $"Début : {started} · Fin : {finished} · {report.Calls.Length} requêtes",
            $"Beginn: {started} · Ende: {finished} · {report.Calls.Length} Anfragen", $"開始：{started} · 終了：{finished} · {report.Calls.Length} リクエスト"));
        var context = report.ContextTokens > 0 ? report.ContextTokens.ToString() : "—";
        var memory = report.MemoryGiB > 0 ? report.MemoryGiB.ToString() : "—";
        DrawGuideJournalText(GuideText($"Context: {context} · RAM limit: {memory} GiB", $"Contexte : {context} · RAM maximale : {memory} Gio",
            $"Kontext: {context} · RAM-Limit: {memory} GiB", $"コンテキスト：{context} · RAM上限：{memory} GiB"));
        if (report.Error.Length > 0)
            DrawGuideJournalText(GuideText("Error: ", "Erreur : ", "Fehler: ", "エラー：") + report.Error);
        if (report.RecordingError.Length > 0)
            DrawGuideJournalText(GuideText("Recording error: ", "Erreur d’enregistrement : ", "Aufzeichnungsfehler: ", "記録エラー：") + report.RecordingError);
        if (!report.CaptureConversation)
            DrawGuideJournalText(GuideText("Prompts and responses were not recorded for this analysis.", "Les prompts et réponses n’ont pas été enregistrés pour cette analyse.",
                "Prompts und Antworten wurden für diese Analyse nicht aufgezeichnet.", "この解析のプロンプトと応答は記録されていません。"));
        else if (report.ContentOmitted)
            DrawGuideJournalText(GuideText("Some conversation content was not retained.", "Une partie des échanges n’a pas été conservée.", "Ein Teil des Gesprächs wurde nicht gespeichert.", "会話の一部は保存されていません。"));
    }

    private void DrawGuideJournalExport(GuideAnalysisReport report)
    {
        if (_guideJournalExportTask is { IsCompleted: true } completed)
        {
            try { _guideJournalExportPath = completed.GetAwaiter().GetResult(); }
            catch (Exception error) { _guideJournalExportError = error.GetBaseException().Message; }
            _guideJournalExportTask = null;
        }
        if (ImGui.Button(GuideText("Copy report", "Copier le rapport", "Bericht kopieren", "レポートをコピー")))
            ImGui.SetClipboardText(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        ImGui.SameLine();
        ImGui.BeginDisabled(_guideJournalExportTask != null || _guideSummaries == null);
        if (ImGui.Button(GuideText("Export ZIP", "Exporter le ZIP", "ZIP exportieren", "ZIPをエクスポート")) && _guideSummaries?.Diagnostics is { } diagnostics)
        {
            _guideJournalExportReportID = report.ID;
            _guideJournalExportPath = _guideJournalExportError = "";
            _guideJournalExportTask = diagnostics.ExportAsync(report.ID);
        }
        ImGui.EndDisabled();
        if (_guideJournalExportTask != null)
            ImGui.TextUnformatted(GuideText("Exporting…", "Export en cours…", "Export läuft…", "エクスポート中…"));
        if (_guideJournalExportReportID == report.ID)
        {
            if (_guideJournalExportPath.Length > 0)
            {
                if (ImGui.Button(GuideText("Copy ZIP path", "Copier le chemin du ZIP", "ZIP-Pfad kopieren", "ZIPのパスをコピー"))) ImGui.SetClipboardText(_guideJournalExportPath);
                DrawGuideJournalText(_guideJournalExportPath);
            }
            if (_guideJournalExportError.Length > 0)
                DrawGuideJournalText(GuideText("Export failed: ", "Échec de l’export : ", "Export fehlgeschlagen: ", "エクスポート失敗：") + _guideJournalExportError);
        }
        if (report.FilePath.Length > 0 && ImGui.SmallButton(GuideText("Copy log file path", "Copier le chemin du journal", "Protokollpfad kopieren", "ログのパスをコピー")))
            ImGui.SetClipboardText(report.FilePath);
    }

    private string GuideJournalCallState(string state) => state switch
    {
        "Started" => GuideText("Started", "En cours", "Gestartet", "開始"),
        "Tokenized" => GuideText("Prompt counted", "Prompt compté", "Prompt gezählt", "トークン計測済み"),
        "Completed" => GuideText("Completed", "Terminée", "Abgeschlossen", "完了"),
        "Failed" => GuideText("Failed", "Échec", "Fehlgeschlagen", "失敗"),
        _ => state
    };

    private void DrawGuideJournalCalls(GuideAnalysisReport report)
    {
        if (report.Calls.Length == 0)
        {
            ImGui.TextUnformatted(GuideText("No model requests recorded.", "Aucune requête au modèle enregistrée.", "Keine Modellanfragen aufgezeichnet.", "モデルへのリクエスト記録なし。"));
            return;
        }
        var selected = report.Calls.FirstOrDefault(call => call.Index == _guideJournalCallIndex) ?? report.Calls[^1];
        var listHeight = Math.Clamp(ImGui.GetContentRegionAvail().Y * .3f, 90, 220);
        if (ImGui.BeginChild("GuideJournalRequests", new Vector2(0, listHeight), true))
        {
            foreach (var call in report.Calls)
            {
                ImGui.PushID(call.Index);
                var label = $"#{call.Index} · {GuideAnalysisStepLabel(call.Stage)}"
                    + (call.Boss.Length > 0 ? " · " + call.Boss : "")
                    + " · " + GuideAnalysisAttemptLabel(call.Attempt) + " · " + GuideJournalCallState(call.State);
                if (ImGui.Selectable(label.Replace("#", "＃"), selected.Index == call.Index))
                {
                    _guideJournalCallIndex = call.Index;
                    selected = call;
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(label);
                    if (call.Error.Length > 0) DrawGuideJournalText(call.Error);
                    ImGui.EndTooltip();
                }
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        DrawGuideJournalCall(selected);
    }

    private void DrawGuideJournalCall(GuideAnalysisCall call)
    {
        ImGui.PushID(call.Index);
        DrawGuideJournalText($"#{call.Index} · {call.StartedAt.ToLocalTime():HH:mm:ss} · {GuideAnalysisStepLabel(call.Stage)} · {GuideAnalysisAttemptLabel(call.Attempt)} · {GuideJournalCallState(call.State)}");
        if (call.Boss.Length > 0) DrawGuideJournalText(call.Boss);
        var input = call.PromptTokens?.ToString() ?? "—";
        var output = call.OutputTokens?.ToString() ?? "—";
        DrawGuideJournalText(GuideText($"Tokens in / out: {input} / {output} · {call.Seconds:F1}s", $"Tokens entrée / sortie : {input} / {output} · {call.Seconds:F1}s",
            $"Tokens ein / aus: {input} / {output} · {call.Seconds:F1}s", $"入力 / 出力トークン：{input} / {output} · {call.Seconds:F1}秒"));
        if (call.FinishReason.Length > 0) DrawGuideJournalText(GuideText("Finish reason: ", "Fin de réponse : ", "Beendigungsgrund: ", "終了理由：") + call.FinishReason);
        if (call.Error.Length > 0) DrawGuideJournalText(GuideText("Error: ", "Erreur : ", "Fehler: ", "エラー：") + call.Error);
        if (ImGui.SmallButton(GuideText("Copy request and response", "Copier la requête et la réponse", "Anfrage und Antwort kopieren", "リクエストと応答をコピー")))
            ImGui.SetClipboardText(JsonSerializer.Serialize(call, new JsonSerializerOptions { WriteIndented = true }));
        if (ImGui.BeginTabBar("Conversation"))
        {
            DrawGuideJournalConversationTab(GuideText("Response", "Réponse", "Antwort", "応答") + "###Response", call.Response);
            DrawGuideJournalConversationTab("Prompt###Prompt", call.Source);
            DrawGuideJournalConversationTab(GuideText("System instructions", "Instructions système", "Systemanweisungen", "システム指示") + "###System", call.System);
            DrawGuideJournalConversationTab("JSON###RequestJson", call.RequestJson);
            DrawGuideJournalConversationTab(GuideText("Reasoning", "Raisonnement", "Überlegungen", "推論") + "###Reasoning", call.Reasoning);
            ImGui.EndTabBar();
        }
        ImGui.PopID();
    }

    private void DrawGuideJournalConversationTab(string label, string content)
    {
        if (!ImGui.BeginTabItem(label)) return;
        DrawGuideJournalTextPane(label, content);
        ImGui.EndTabItem();
    }

    private void DrawGuideJournalTextPane(string id, string content)
    {
        if (ImGui.BeginChild(id, new Vector2(0, Math.Max(100, ImGui.GetContentRegionAvail().Y)), true))
            DrawGuideJournalText(content.Length > 0 ? content : GuideText("No content recorded.", "Aucun contenu enregistré.", "Kein Inhalt aufgezeichnet.", "記録された内容なし。"));
        ImGui.EndChild();
    }

    private void DrawGuideJournalEvents(GuideAnalysisReport report)
    {
        if (ImGui.BeginChild("GuideJournalEvents", new Vector2(0, Math.Max(100, ImGui.GetContentRegionAvail().Y)), true))
        {
            foreach (var entry in report.Events)
            {
                DrawGuideJournalText($"{entry.At.ToLocalTime():HH:mm:ss} · {GuideAnalysisStepLabel(entry.Stage)}"
                    + (entry.Boss.Length > 0 ? " · " + entry.Boss : "")
                    + (entry.Attempt > 0 ? " · " + GuideAnalysisAttemptLabel(entry.Attempt) : ""));
                if (entry.Detail.Length > 0) DrawGuideJournalText(entry.Detail);
                ImGui.Separator();
            }
            if (report.Events.Length == 0)
                ImGui.TextUnformatted(GuideText("No steps recorded.", "Aucune étape enregistrée.", "Keine Schritte aufgezeichnet.", "手順の記録なし。"));
        }
        ImGui.EndChild();
    }
}
