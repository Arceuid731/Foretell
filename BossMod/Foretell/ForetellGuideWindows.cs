using Dalamud.Bindings.ImGui;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private bool _guideEntryDismissed;

    private static Vector4 GuideColor(uint packed) => new((packed & 255) / 255f, ((packed >> 8) & 255) / 255f, ((packed >> 16) & 255) / 255f, (packed >> 24) / 255f);

    private void DrawGuideProgress(GuideSnapshot snapshot)
    {
        if (snapshot.StartedAt == default) return;
        var pending = snapshot.State is GuideState.ReadingCache or GuideState.Downloading or GuideState.Preparing;
        var elapsed = pending ? Math.Max(0, (DateTime.UtcNow - snapshot.StartedAt).TotalSeconds) : snapshot.ElapsedSeconds;
        ImGui.TextDisabled(snapshot.FromCache
            ? GuideText($"Cache · {elapsed:F2}s", $"Cache · {elapsed:F2}s", $"Cache · {elapsed:F2}s", $"キャッシュ · {elapsed:F2}秒")
            : GuideText($"Elapsed: {elapsed:F1}s", $"Écoulé : {elapsed:F1}s", $"Vergangen: {elapsed:F1}s", $"経過：{elapsed:F1}秒"));
        if (pending)
        {
            ImGui.TextWrapped(snapshot.EstimatedSeconds is { } estimate
                ? elapsed < estimate
                    ? GuideText($"Estimated remaining: ~{estimate - elapsed:F0}s (recent measurements)", $"Reste estimé : ~{estimate - elapsed:F0}s (mesures récentes)", $"Noch etwa {estimate - elapsed:F0}s (Messwerte)", $"残り約{estimate - elapsed:F0}秒（実測値）")
                    : GuideText("Slower than recent preparations; still working…", "Plus long que les préparations récentes ; en cours…", "Dauert länger als zuletzt; läuft weiter…", "以前より時間がかかっています…")
                : GuideText("First preparation: measuring duration… (wiki timeout: 25s)", "Première préparation : durée en cours de mesure… (délai wiki : 25s)", "Erste Vorbereitung: Zeitmessung… (Wiki-Limit: 25s)", "初回準備：所要時間を測定中…（Wiki取得上限25秒）"));
            if (snapshot.ReceivedBytes > 0)
            {
                ImGui.TextDisabled($"{snapshot.ReceivedBytes / 1024d:F0} KiB");
                if (snapshot.TotalBytes is > 0)
                    ImGui.ProgressBar(Math.Clamp((float)snapshot.ReceivedBytes / snapshot.TotalBytes.Value, 0, 1), new(-1, 0));
            }
        }
    }

    private void DrawGuideEntry()
    {
        if (_guideDuty == null || _guideEntryDismissed || _guideCombat || _guides?.Snapshot is not { } snapshot || snapshot.Duty != _guideDuty) return;
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + new Vector2(viewport.Size.X * .5f - 250, Math.Max(20, viewport.Size.Y * .12f)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new(500, Math.Min(560, viewport.Size.Y * .75f)), ImGuiCond.Always);
        var open = true;
        if (ImGui.Begin(GuideText("Foretell · preparing your guide", "Foretell · préparation du guide", "Foretell · Anleitung vorbereiten", "Foretell・攻略準備") + "###ForetellGuideEntry", ref open,
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNavFocus))
        {
            ImGui.TextColored(GuideColor(_cfg.GuideActiveColor), GuideDutyName(_guideDuty));
            ImGui.TextDisabled("Console Games Wiki");
            ImGui.TextDisabled(GuideText("Stays open until dismissed; hidden during combat.", "Reste ouvert jusqu’à fermeture ; masqué pendant le combat.",
                "Bleibt bis zum Schließen offen; im Kampf ausgeblendet.", "閉じるまで表示。戦闘中は一時的に非表示。"));
            DrawGuideState(snapshot);
            DrawGuideSummaryProgress();
            if (snapshot.Document is { } document)
            {
                ImGui.Separator();
                ImGui.TextWrapped(GuideText($"{document.Bosses.Length} bosses · {document.MechanicCount} documented mechanics", $"{document.Bosses.Length} boss · {document.MechanicCount} mécaniques documentées",
                    $"{document.Bosses.Length} Bosse · {document.MechanicCount} dokumentierte Mechaniken", $"ボス{document.Bosses.Length}体・ギミック{document.MechanicCount}件"));
                foreach (var boss in document.Bosses.Take(6))
                {
                    ImGui.TextWrapped(GuideBossName(boss));
                    DrawGuideBossSummary(boss);
                }
                ImGui.TextWrapped(GuideText("The checklist follows the upcoming boss, then only the boss identified in combat.", "La checklist suit le boss à venir, puis uniquement le boss identifié en combat.",
                    "Die Checkliste folgt dem nächsten, dann dem im Kampf erkannten Boss.", "チェックリストは次のボス、戦闘中は特定されたボスのみ表示。"));
                if (ImGui.Button(GuideText("Show checklist", "Afficher la checklist", "Checkliste anzeigen", "チェックリストを表示")))
                { _cfg.GuideSidebar = true; _cfg.Modified.Fire(); open = false; }
            }
            if (ImGui.SmallButton(GuideText("Dismiss", "Fermer", "Schließen", "閉じる"))) open = false;
        }
        ImGui.End();
        if (!open) _guideEntryDismissed = true;
    }

    private void DrawGuideBossSummary(GuideBoss boss)
    {
        var kinds = boss.Phases.SelectMany(phase => phase.Mechanics).SelectMany(mechanic => mechanic.Rules).Select(rule => rule.Guidance).Distinct().Take(5).ToArray();
        ImGui.TextWrapped(kinds.Length == 0
            ? GuideText("Specific mechanics: consult the source details.", "Mécaniques spécifiques : consulter les détails de la source.", "Spezielle Mechaniken: Quelldetails lesen.", "固有ギミック：原文の詳細を確認。")
            : GuideText("Topics (read conditions): ", "Thèmes (lire les conditions) : ", "Themen (Bedingungen lesen): ", "種類（条件を確認）：")
                + string.Join(" · ", kinds.Select(kind => GuidanceInstruction(kind, MechanicKind.Unknown, GeometryKind.Unknown))));
        var summary = boss.Phases.Select(phase => GuideContextSummary(boss, phase))
            .Concat(boss.Phases.SelectMany(phase => phase.Mechanics.Select(mechanic => GuideSummaryFor(boss, phase, mechanic)))).FirstOrDefault(text => text != null);
        if (summary != null) ImGui.TextWrapped(GuideText("Excerpt (auto-summary): ", "Extrait (résumé auto) : ", "Auszug (automatisch): ", "抜粋（自動要約）：") + summary);
    }


    private void DrawGuideSettings()
    {
        if (!ImGui.CollapsingHeader(GuideText("Checklist and alerts", "Checklist et alertes", "Checkliste und Hinweise", "チェックリストと警告"))) return;
        var changed = ImGui.Checkbox(GuideText("Entry popup", "Panneau à l’entrée", "Fenster beim Betreten", "入場時の案内"), ref _cfg.GuideEntryPopup);
        changed |= ImGui.Checkbox(GuideText("Unlock checklist (drag / resize)", "Déverrouiller la checklist (déplacer / redimensionner)", "Checkliste entsperren (bewegen / skalieren)", "チェックリストの移動・サイズ変更を許可"), ref _cfg.GuideChecklistUnlocked);
        changed |= ImGui.Checkbox(GuideText("Central guide alerts", "Alertes centrales du guide", "Zentrale Guide-Warnungen", "攻略の中央警告"), ref _cfg.GuideCentralAlerts);
        changed |= ImGui.Checkbox(GuideText("Local translated summaries", "Résumés traduits localement", "Lokal übersetzte Zusammenfassungen", "ローカル翻訳要約"), ref _cfg.GuideLocalSummaries);
        changed |= ImGui.Checkbox(GuideText("Vulkan GPU (disable for CPU mode)", "GPU Vulkan (décocher pour le CPU)", "Vulkan-GPU (deaktivieren für CPU)", "Vulkan GPU（オフでCPU）"), ref _cfg.GuideSummaryGpu);
        ImGui.TextWrapped(GuideText("One-time model download: 1.83 GB + runtime (18–35 MB). Isolated process: 2 CPU threads, 15% total CPU cap, 4 GiB committed RAM cap, 4096-token context. Stops inference in combat. Prepared cache needs no model or network. AI summaries never define live geometry or targeting.",
            "Téléchargement initial : modèle de 1,83 Go + moteur (18–35 Mo). Processus isolé : 2 threads CPU, plafond de 15 % du CPU total, 4 Gio de mémoire engagée, contexte de 4096 tokens. Inférence arrêtée en combat. Le cache préparé fonctionne sans modèle ni réseau. Les résumés IA ne déterminent ni zones ni cibles en direct.",
            "Einmalig: 1,83 GB Modell + 18–35 MB Laufzeit. Isoliert: 2 CPU-Threads, 15% Gesamt-CPU, 4 GiB zugesicherter RAM, 4096 Token. Keine Inferenz im Kampf. Cache funktioniert offline. KI-Zusammenfassungen bestimmen keine Live-Geometrie oder Ziele.",
            "初回：モデル1.83 GB＋実行環境18～35 MB。別プロセス：CPU 2スレッド、全CPUの15%、コミットRAM 4 GiB、4096トークン上限。戦闘中は推論停止。キャッシュはオフライン対応。AI要約は範囲や対象を決定しません。"));
        changed |= ImGui.SliderFloat(GuideText("Width", "Largeur", "Breite", "幅"), ref _cfg.GuideWidth, 260, 1000, "%.0f");
        changed |= ImGui.SliderFloat(GuideText("Maximum height (automatic when locked)", "Hauteur maximale (automatique une fois verrouillée)", "Maximale Höhe (gesperrt automatisch)", "最大高さ（ロック中は自動）"), ref _cfg.GuideHeight, 180, 1000, "%.0f");
        changed |= ImGui.SliderFloat(GuideText("Text scale", "Échelle du texte", "Textgröße", "文字倍率"), ref _cfg.GuideScale, .7f, 1.8f, "%.2f");
        changed |= ImGui.SliderFloat(GuideText("Central alert scale", "Taille de l’alerte centrale", "Größe der zentralen Warnung", "中央警告の倍率"), ref _cfg.GuideAlertScale, 1, 2.5f, "%.2f");
        changed |= EditGuideColor(GuideText("Editing background only", "Fond en édition uniquement", "Hintergrund nur beim Bearbeiten", "編集中のみの背景"), ref _cfg.GuideBackgroundColor);
        changed |= EditGuideColor(GuideText("Text", "Texte", "Text", "文字"), ref _cfg.GuideTextColor);
        changed |= EditGuideColor(GuideText("Active mechanic / alert", "Mécanique active / alerte", "Aktive Mechanik / Warnung", "発動中・警告"), ref _cfg.GuideActiveColor);
        changed |= EditGuideColor(GuideText("Resolved this pull", "Résolue sur ce pull", "In diesem Versuch aufgelöst", "この戦闘で解決済み"), ref _cfg.GuideResolvedColor);
        changed |= EditGuideColor(GuideText("Unresolved response", "Consigne non résolue", "Ungeklärte Reaktion", "未確定の対処"), ref _cfg.GuideUnresolvedColor);
        if (ImGui.Button(GuideText("Reset checklist layout", "Réinitialiser la disposition", "Layout zurücksetzen", "配置をリセット")))
        { _cfg.GuidePositionX = _cfg.GuidePositionY = -1; _cfg.GuideWidth = 380; _cfg.GuideHeight = 520; _cfg.GuideScale = 1; _guideChecklistWasUnlocked = false; changed = true; }
        if (_guideDuty != null && ImGui.Button(GuideText("Show entry summary again", "Revoir le résumé d’entrée", "Zusammenfassung erneut anzeigen", "入場時の要約を再表示")))
        { _guideEntryDismissed = false; }
        if (!_guideCombat && ImGui.Button(GuideText("Retry unfinished summaries", "Relancer les résumés non préparés", "Fehlende Zusammenfassungen erneut versuchen", "未完了の要約を再試行"))) _guideSummaries?.Retry();
        if (changed) { _guideChecklistWasUnlocked = false; _cfg.Modified.Fire(); }
    }

    private string? GuideSummaryFor(GuideBoss boss, GuidePhase phase, GuideMechanic mechanic, GuideDocument? document = null)
        => _guideSummaries?.Snapshot is { } snapshot && snapshot.SourceHash == (document ?? _liveGuide)?.SourceHash && snapshot.Language == GuideClientLanguage
            ? snapshot.Summaries.GetValueOrDefault(ForetellGuideSummaries.Key(boss, phase, mechanic)) : null;

    private string? GuideContextSummary(GuideBoss boss, GuidePhase phase, GuideDocument? document = null)
        => _guideSummaries?.Snapshot is { } snapshot && snapshot.SourceHash == (document ?? _liveGuide)?.SourceHash && snapshot.Language == GuideClientLanguage
            ? snapshot.Summaries.GetValueOrDefault(ForetellGuideSummaries.ContextKey(boss, phase)) : null;

    private void DrawGuideSummaryProgress(GuideDocument? document = null)
    {
        if (!_cfg.GuideLocalSummaries || _guideSummaries?.Snapshot is not { } snapshot || snapshot.SourceHash != (document ?? _liveGuide)?.SourceHash) return;
        var stage = snapshot.Stage switch
        {
            "Ready" => GuideText("Translated summaries ready", "Résumés traduits prêts", "Übersetzte Zusammenfassungen bereit", "翻訳要約完了"),
            "PausedInCombat" => GuideText("Summary preparation paused in combat", "Préparation des résumés suspendue en combat", "Zusammenfassung im Kampf pausiert", "戦闘中のため要約を一時停止"),
            "InstallingOrStarting" => GuideText("First model setup / startup · quick guide already usable", "Installation / démarrage du modèle · fiche rapide déjà utilisable", "Modelleinrichtung / Start · Kurzanleitung nutzbar", "モデル準備・起動中。簡易攻略は使用可能"),
            "ReadyWithUnresolved" => GuideText("Summaries ready · some excerpts remain unresolved", "Résumés prêts · certains passages restent non préparés", "Zusammenfassungen bereit · einige Abschnitte ungeklärt", "要約完了・一部は未対応"),
            "Summarizing" => GuideText("Preparing translated summaries", "Préparation des résumés traduits", "Übersetzte Zusammenfassungen vorbereiten", "翻訳要約を準備中"),
            "Queued" => GuideText("Summaries queued", "Résumés en attente", "Zusammenfassungen vorgemerkt", "要約待ち"),
            _ => GuideText("Local summaries unavailable · source retained", "Résumés locaux indisponibles · source conservée", "Lokale Zusammenfassungen nicht verfügbar · Quelle bleibt", "ローカル要約不可・原文は保持")
        };
        ImGui.TextWrapped(stage + $" · {snapshot.Completed}/{snapshot.Total}");
        if (snapshot.Total > 0) ImGui.ProgressBar((float)snapshot.Completed / snapshot.Total, new(-1, 0));
        if (snapshot.RemainingSeconds is { } remaining) ImGui.TextDisabled(GuideText($"Estimate: ~{remaining:F0}s outside combat", $"Estimation : ~{remaining:F0}s hors combat", $"Schätzung: ~{remaining:F0}s außerhalb des Kampfes", $"推定残り約{remaining:F0}秒（非戦闘中）"));
        if (snapshot.Transfer is { } transfer)
        {
            ImGui.TextWrapped(transfer.Stage);
            if (transfer.Total > 0)
            {
                ImGui.ProgressBar((float)transfer.Received / transfer.Total, new(-1, 0), $"{transfer.Received / 1000000d:F0}/{transfer.Total / 1000000d:F0} MB");
                if (transfer.RemainingSeconds is { } seconds) ImGui.TextDisabled($"~{TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 86400)):g}");
            }
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
