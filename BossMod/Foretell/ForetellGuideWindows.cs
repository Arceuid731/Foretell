using Dalamud.Bindings.ImGui;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private bool _guideEntryDismissed;
    private DateTime _guideEntryReadyAt;
    private bool _guideChecklistWasUnlocked;
    private bool _guideChecklistDirty;

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
        if (snapshot.Document != null)
        {
            if (_guideEntryReadyAt == default) _guideEntryReadyAt = DateTime.UtcNow;
            if ((DateTime.UtcNow - _guideEntryReadyAt).TotalSeconds > 15) { _guideEntryDismissed = true; return; }
        }
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + new Vector2(viewport.Size.X * .5f - 250, Math.Max(20, viewport.Size.Y * .12f)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new(500, Math.Min(560, viewport.Size.Y * .75f)), ImGuiCond.Always);
        var open = true;
        if (ImGui.Begin(GuideText("Foretell · preparing your guide", "Foretell · préparation du guide", "Foretell · Anleitung vorbereiten", "Foretell・攻略準備") + "###ForetellGuideEntry", ref open,
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNavFocus))
        {
            ImGui.TextColored(GuideColor(_cfg.GuideActiveColor), GuideDutyName(_guideDuty));
            ImGui.TextDisabled("Console Games Wiki");
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

    private void DrawGuideChecklist()
    {
        if (_guideDuty == null && !_cfg.GuideChecklistUnlocked) return;
        var viewport = ImGui.GetMainViewport();
        var size = FiniteViewport(viewport.Size) ? viewport.Size : new Vector2(1920, 1080);
        var width = Math.Clamp(_cfg.GuideWidth, 260, Math.Max(260, size.X));
        var height = Math.Clamp(_cfg.GuideHeight, 180, Math.Max(180, size.Y));
        var position = viewport.Pos + new Vector2(_cfg.GuidePositionX < 0 ? 20 : _cfg.GuidePositionX * size.X, _cfg.GuidePositionY < 0 ? size.Y * .2f : _cfg.GuidePositionY * size.Y);
        position = Vector2.Clamp(position, viewport.Pos, viewport.Pos + Vector2.Max(Vector2.Zero, size - new Vector2(width, height)));
        if (!_cfg.GuideChecklistUnlocked || !_guideChecklistWasUnlocked)
        { ImGui.SetNextWindowPos(position, ImGuiCond.Always); ImGui.SetNextWindowSize(new(width, height), ImGuiCond.Always); }
        ImGui.SetNextWindowSizeConstraints(new(260, 180), Vector2.Min(new(1000, 1000), Vector2.Max(new(260, 180), size)));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, GuideColor(_cfg.GuideBackgroundColor));
        ImGui.PushStyleColor(ImGuiCol.Text, GuideColor(_cfg.GuideTextColor));
        var flags = ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings;
        if (!_cfg.GuideChecklistUnlocked) flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
        var visible = ImGui.Begin(GuideText("Foretell · boss checklist", "Foretell · checklist du boss", "Foretell · Boss-Checkliste", "Foretell・ボスチェックリスト") + "###ForetellGuideChecklist", flags);
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
            if (_guideDuty != null) ImGui.TextWrapped(GuideDutyName(_guideDuty));
            if (_liveGuide == null)
            {
                if (_guides != null) DrawGuideState(_guides.Snapshot);
                return;
            }
            if (_guideFrame.Boss is not { } boss)
            {
                ImGui.TextWrapped(_guideFrame.Ambiguous
                    ? GuideText("Several bosses active: waiting for unambiguous identification.", "Plusieurs boss actifs : attente d’une identification sans ambiguïté.", "Mehrere Bosse aktiv: eindeutige Erkennung abwarten.", "複数のボスが活動中：特定待ち。")
                    : GuideText("Documented bosses completed.", "Boss documentés terminés.", "Dokumentierte Bosse abgeschlossen.", "記載されたボスを撃破済み。"));
                return;
            }
            ImGui.TextColored(GuideColor(_cfg.GuideActiveColor), (_guideFrame.Upcoming ? GuideText("Upcoming: ", "À venir : ", "Als Nächstes: ", "次：") : GuideText("Current: ", "Actuel : ", "Aktuell: ", "現在：")) + GuideBossName(boss));
            DrawGuideBossSummary(boss);
            DrawGuideSummaryProgress();
            var active = LiveGuideSignals().ToArray();
            foreach (var phase in boss.Phases)
            {
                ImGui.Separator();
                ImGui.TextWrapped(GuidePhaseName(phase));
                if (GuideContextSummary(boss, phase) is { } contextSummary)
                    ImGui.TextWrapped(GuideText("Context (auto-summary): ", "Contexte (résumé auto) : ", "Kontext (automatisch): ", "背景（自動要約）：") + contextSummary);
                if (phase.Context.Length > 0 && ImGui.TreeNode(GuideText("Phase context (source EN)", "Contexte de phase (source EN)", "Phasenkontext (EN)", "フェーズ背景（英語）") + "###" + phase.Name))
                { ImGui.TextWrapped(phase.Context); ImGui.TreePop(); }
                for (var index = 0; index < phase.Mechanics.Length; ++index)
                {
                    var mechanic = phase.Mechanics[index];
                    ImGui.PushID(phase.Name + ":" + index);
                    var live = active.Where(signal => ReferenceEquals(signal.Mechanic, mechanic)).ToArray();
                    var resolved = _guideEncounter.Resolved(boss, phase, mechanic);
                    var prepared = GuideRules.LiveGuidance(mechanic, phase, boss) != GuidanceKind.None;
                    var color = live.Length > 0 ? _cfg.GuideActiveColor : resolved ? _cfg.GuideResolvedColor : prepared ? _cfg.GuideTextColor : _cfg.GuideUnresolvedColor;
                    ImGui.TextColored(GuideColor(color), (live.Length > 0 ? "▶ " : resolved ? "✓ " : "• ") + GuideMechanicName(boss, mechanic));
                    if (live.Length > 0) ImGui.TextDisabled($"{Math.Max(0, (live.Min(signal => signal.Until) - _ws.CurrentTime).TotalSeconds):F1}s · {live[0].Kind}");
                    if (mechanic.Rules.Length > 0) ImGui.TextWrapped((prepared ? "" : GuideText("Topics: ", "Thèmes : ", "Themen: ", "種類："))
                        + string.Join(" · ", mechanic.Rules.Select(rule => GuidanceInstruction(rule.Guidance, MechanicKind.Unknown, GeometryKind.Unknown))));
                    if (GuideSummaryFor(boss, phase, mechanic) is { } summary)
                    {
                        ImGui.TextWrapped(summary);
                        ImGui.TextDisabled(GuideText("Local auto-summary · check source", "Résumé automatique local · vérifier la source", "Lokale automatische Zusammenfassung · Quelle prüfen", "ローカル自動要約・原文を確認"));
                    }
                    if (!prepared) ImGui.TextWrapped(GuideText("Conditional / specific response: source required.", "Consigne conditionnelle / spécifique : consulter la source.", "Bedingte / spezielle Reaktion: Quelle lesen.", "条件付き・固有の対処：原文を確認。"));
                    if (ImGui.TreeNode(GuideText("Details and source (EN)", "Détails et source (EN)", "Details und Quelle (EN)", "詳細・原文（英語）")))
                    { ImGui.TextWrapped(mechanic.Text); DrawGuideStatusNames(mechanic); ImGui.TreePop(); }
                    ImGui.PopID();
                }
            }
            ImGui.Separator();
            ImGui.TextWrapped(GuideText("✓ = observed resolution this pull, not permanently finished. Missing geometry is never invented.", "✓ = résolution observée sur ce pull, pas définitivement terminée. Aucune zone manquante n’est inventée.",
                "✓ = in diesem Versuch aufgelöst, nicht dauerhaft erledigt. Fehlende Geometrie wird nie erfunden.", "✓＝この戦闘で解決済み。再発しない意味ではありません。不明な範囲は描画しません。"));
        }
        finally { ImGui.End(); ImGui.PopStyleColor(2); }
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
        changed |= ImGui.SliderFloat(GuideText("Height", "Hauteur", "Höhe", "高さ"), ref _cfg.GuideHeight, 180, 1000, "%.0f");
        changed |= ImGui.SliderFloat(GuideText("Text scale", "Échelle du texte", "Textgröße", "文字倍率"), ref _cfg.GuideScale, .7f, 1.8f, "%.2f");
        changed |= EditGuideColor(GuideText("Background", "Fond", "Hintergrund", "背景"), ref _cfg.GuideBackgroundColor);
        changed |= EditGuideColor(GuideText("Text", "Texte", "Text", "文字"), ref _cfg.GuideTextColor);
        changed |= EditGuideColor(GuideText("Active mechanic / alert", "Mécanique active / alerte", "Aktive Mechanik / Warnung", "発動中・警告"), ref _cfg.GuideActiveColor);
        changed |= EditGuideColor(GuideText("Resolved this pull", "Résolue sur ce pull", "In diesem Versuch aufgelöst", "この戦闘で解決済み"), ref _cfg.GuideResolvedColor);
        changed |= EditGuideColor(GuideText("Unresolved response", "Consigne non résolue", "Ungeklärte Reaktion", "未確定の対処"), ref _cfg.GuideUnresolvedColor);
        if (ImGui.Button(GuideText("Reset checklist layout", "Réinitialiser la disposition", "Layout zurücksetzen", "配置をリセット")))
        { _cfg.GuidePositionX = _cfg.GuidePositionY = -1; _cfg.GuideWidth = 380; _cfg.GuideHeight = 520; _cfg.GuideScale = 1; _guideChecklistWasUnlocked = false; changed = true; }
        if (_guideDuty != null && ImGui.Button(GuideText("Show entry summary again", "Revoir le résumé d’entrée", "Zusammenfassung erneut anzeigen", "入場時の要約を再表示")))
        { _guideEntryDismissed = false; _guideEntryReadyAt = default; }
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
