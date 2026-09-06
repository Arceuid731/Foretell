using Dalamud.Bindings.ImGui;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private ForetellGuideService? _guides;
    private ForetellGuideSummaries? _guideSummaries;
    private GuideDuty? _guideDuty;
    private GuideDocument? _liveGuide;
    private (uint Content, uint Territory) _guideIdentity;
    private DateTime _guideSampleAt;
    private DateTime _guideNameBudgetAt;
    private readonly List<GuideMatch> _guideMatches = [];
    private readonly Dictionary<string, uint> _guideBossNames = [];
    private readonly Dictionary<(string Boss, string Mechanic), uint> _guideActionNames = [];
    private readonly Dictionary<(string Sheet, uint ID, bool Localized), string> _guideNames = [];
    private GuideLanguage _guideNameLanguage;
    private int _guideNameBudget;
    private Task<(GuideDuty Duty, string Display)[]>? _guideCatalogue;
    private string _guideSearch = "";
    private bool _guideCombat;
    private readonly GuideEncounterTracker _guideEncounter = new();
    private GuideCombatFrame _guideFrame = GuideCombatFrame.Empty;
    private readonly List<GuideSignal> _guideSignals = [];

    private static GuideLanguage GuideClientLanguage => Service.ClientState.ClientLanguage switch
    {
        Dalamud.Game.ClientLanguage.French => GuideLanguage.French,
        Dalamud.Game.ClientLanguage.German => GuideLanguage.German,
        Dalamud.Game.ClientLanguage.Japanese => GuideLanguage.Japanese,
        _ => GuideLanguage.English
    };
    private static string GuideText(string english, string french, string german, string japanese)
        => GuidePreparation.Local(GuideClientLanguage, english, french, german, japanese);

    private void UpdateGuides(DateTime now, bool inCombat)
    {
        if (_guides == null) return;
        _guideCombat = inCombat;
        if ((now - _guideNameBudgetAt).TotalMilliseconds >= 200 || now < _guideNameBudgetAt)
        {
            _guideNameBudgetAt = now; _guideNameBudget = 12;
            if (_guideNameLanguage != GuideClientLanguage)
            {
                _guideNames.Clear(); _guideNameLanguage = GuideClientLanguage; _guideCatalogue = null;
            }
        }
        if (!_cfg.EnableGuides)
        {
            _guideSummaries?.Update(null, GuideClientLanguage, false, inCombat, _cfg.GuideSummaryGpu, "");
            if (_guideIdentity != default || _guides.Snapshot.State != GuideState.Idle) { _guides.Cancel(); ResetGuideContext(); }
            return;
        }
        var identity = ((uint)_ws.CurrentCFCID, _ws.CurrentZone);
        if (_guideIdentity != identity)
        {
            _guides.Cancel(); ResetGuideContext(); _guideIdentity = identity;
            if (identity.Item1 != 0 && Service.LuminaRow<Lumina.Excel.Sheets.ContentFinderCondition>(identity.Item1) is { } content
                && content.TerritoryType.RowId == identity.Item2)
            {
                var duty = new GuideDuty(identity.Item1, identity.Item2, content.Name.ToString());
                if (duty.Valid) { _guideDuty = duty; _guides.RequestGuide(duty); }
            }
        }
        if (_guides.Snapshot is { Document: { } document } && document.Duty == _guideDuty) _liveGuide = document;
        if ((now - _guideSampleAt).TotalMilliseconds < 200 && now >= _guideSampleAt) return;
        _guideSampleAt = now; _guideMatches.Clear();
        _guideSummaries?.Update(_liveGuide ?? (_guideDuty == null ? _guides.Snapshot.Document : null), GuideClientLanguage,
            _cfg.GuideLocalSummaries, inCombat, _cfg.GuideSummaryGpu, _guideFrame.Boss?.Name ?? "", _cfg.GuideContextTokens, _cfg.GuideMemoryGiB);
        if (_liveGuide == null || _guideDuty == null) return;
        var player = _ws.Party[PartyState.PlayerSlot];
        List<GuideActorState> actors = [];
        foreach (var actor in _ws.Actors)
        {
            if (actor.Type != ActorType.Enemy || actor.IsAlly || actor.NameID == 0 || actor.IsDestroyed && !actor.IsDead) continue;
            var bossName = GuideSheetName("BNpcName", actor.NameID, false);
            if (bossName.Length == 0) continue;
            var bosses = _liveGuide.Bosses.Where(boss => GuideNames.Boss(boss.Name) == GuideNames.Boss(bossName)).ToArray();
            if (bosses.Length != 1) continue;
            _guideBossNames[bosses[0].Name] = actor.NameID;
            actors.Add(new(actor.InstanceID, actor.OID, actor.NameID, bossName, actor.IsDead,
                actor.InCombat || actor.CastInfo != null, player == null ? float.MaxValue : (actor.Position - player.Position).Length()));
            if (MatchGuideActor(_liveGuide, _guideDuty, actor, now, (sheet, id) => GuideSheetName(sheet, id, false)) is not { } match) continue;
            _guideMatches.Add(match);
            _guideActionNames[(match.Boss.Name, match.Mechanic.Name)] = match.Cast.ActionID;
        }
        _guideEncounter.Update(_liveGuide, actors, false);
        if (_guideEncounter.Frame is { Boss: { } ownerBoss, Upcoming: false, Ambiguous: false })
        {
            var owners = actors.Where(actor => !actor.Dead && GuideNames.Boss(actor.EnglishName) == GuideNames.Boss(ownerBoss.Name)).Select(actor => actor.ID).ToHashSet();
            foreach (var helper in _ws.Actors.Where(actor => !actor.IsAlly && actor.Type is ActorType.Enemy or ActorType.Helper && owners.Contains(actor.OwnerID)))
            {
                if (MatchOwnedGuideActor(_liveGuide, _guideDuty, helper, _ws.Actors.Find(helper.OwnerID), now,
                    (sheet, id) => GuideSheetName(sheet, id, false)) is not { } match) continue;
                _guideMatches.Add(match); _guideActionNames[(match.Boss.Name, match.Mechanic.Name)] = match.Cast.ActionID;
            }
        }
        _guideSignals.Clear();
        foreach (var match in _guideMatches)
        {
            var actor = _ws.Actors.Find(match.Cast.ActorID);
            _guideSignals.Add(new(match.Boss, match.Phase, match.Mechanic, GuideSignalKind.Cast, match.Cast.ActorID, match.Cast.ObjectID,
                match.Cast.NameID, match.Cast.ActionID, actor?.CastInfo?.TargetID ?? 0, match.Cast.FinishAt,
                GuideRules.LiveGuidance(match.Mechanic, match.Phase, match.Boss), "Action/BNpcName IDs + exact English names + current duty") { OwnerID = actor?.OwnerID ?? 0 });
        }
        if (_guideEncounter.Frame is { Boss: { } current, Upcoming: false, Ambiguous: false } && player != null)
        {
            foreach (var status in player.Statuses.Where(status => status.ID != 0 && status.ExpireAt > now))
            {
                if (_ws.Actors.Find(status.SourceID) is not { } source || !actors.Any(actor => actor.ID == source.InstanceID && GuideNames.Boss(actor.EnglishName) == GuideNames.Boss(current.Name))) continue;
                var name = GuideSheetName("Status", status.ID, false);
                if (name.Length == 0) continue;
                var candidates = current.Phases.SelectMany(phase => phase.Mechanics.Select(mechanic => (phase, mechanic)))
                    .Where(entry => GuideRules.Mentions(entry.mechanic.Text, name)).ToArray();
                if (candidates.Length != 1) continue;
                var candidate = candidates[0];
                _guideSignals.Add(new(current, candidate.phase, candidate.mechanic, GuideSignalKind.Status, source.InstanceID, source.OID, source.NameID,
                    status.ID, player.InstanceID, status.ExpireAt, GuideRules.StatusGuidance(candidate.mechanic, candidate.phase, name, current), "Status ID + exact name + current boss source"));
            }
        }
        _guideEncounter.Synchronize(_guideSignals);
        _guideFrame = _guideEncounter.Frame;
        _presentationFrame = null;
    }

    internal static GuideMatch? MatchGuideActor(GuideDocument document, GuideDuty duty, Actor actor, DateTime now, Func<string, uint, string> name)
    {
        if (actor.Type != ActorType.Enemy || actor.IsAlly || actor.IsDeadOrDestroyed || actor.NameID == 0
            || actor.CastInfo is not { } cast || cast.EventHappened || !cast.IsSpell() || !float.IsFinite(cast.NPCRemainingTime)
            || cast.NPCRemainingTime is <= 0 or > 120) return null;
        return GuideSynchronization.Match(document, new(duty, actor.InstanceID, actor.OID, actor.NameID, name("BNpcName", actor.NameID),
            cast.Action.ID, name("Action", cast.Action.ID), now.AddSeconds(cast.NPCRemainingTime)), now);
    }

    internal static bool GuideCastIsCurrent(GuideMatch match, GuideDuty? duty, Actor? actor, DateTime now)
        => match.Cast.Duty == duty && match.Cast.FinishAt > now && actor is { IsDeadOrDestroyed: false, CastInfo: { EventHappened: false } cast }
            && actor.InstanceID == match.Cast.ActorID && actor.OID == match.Cast.ObjectID && actor.NameID == match.Cast.NameID
            && cast.IsSpell() && cast.Action.ID == match.Cast.ActionID && float.IsFinite(cast.NPCRemainingTime) && cast.NPCRemainingTime is > 0 and <= 120
            && Math.Abs((now.AddSeconds(cast.NPCRemainingTime) - match.Cast.FinishAt).TotalSeconds) < .3;

    internal static GuideMatch? MatchOwnedGuideActor(GuideDocument document, GuideDuty duty, Actor actor, Actor? owner, DateTime now, Func<string, uint, string> name)
    {
        if (owner is not { IsDeadOrDestroyed: false, IsAlly: false, Type: ActorType.Enemy } || owner.NameID == 0 || actor.OwnerID != owner.InstanceID || actor.OwnerID == 0
            || actor.Type is not (ActorType.Enemy or ActorType.Helper) || actor.IsAlly || actor.IsDeadOrDestroyed || actor.NameID == 0
            || actor.CastInfo is not { EventHappened: false } cast || !cast.IsSpell() || !float.IsFinite(cast.NPCRemainingTime) || cast.NPCRemainingTime is <= 0 or > 120) return null;
        return GuideSynchronization.Match(document, new(duty, actor.InstanceID, actor.OID, actor.NameID, name("BNpcName", owner.NameID),
            cast.Action.ID, name("Action", cast.Action.ID), now.AddSeconds(cast.NPCRemainingTime)), now);
    }

    internal static GuideInstruction GuidePersonalResponse(GuideMatch match, ulong playerID, Actor? caster)
    {
        if (playerID == 0) return GuideInstruction.Unprepared;
        var instruction = GuidePreparation.Instruction(match.Mechanic);
        if (instruction == GuideInstruction.Stack || instruction == GuideInstruction.Tankbuster && caster?.CastInfo?.TargetID != playerID)
            return GuideInstruction.Unprepared;
        return instruction;
    }

    private void ResetGuideContext()
    {
        _guideIdentity = default; _guideDuty = null; _liveGuide = null; _guideSampleAt = default;
        _guideMatches.Clear(); _guideBossNames.Clear(); _guideActionNames.Clear();
        _guideSignals.Clear(); _guideEncounter.Reset(); _guideFrame = GuideCombatFrame.Empty;
        _guideEntryDismissed = false; _guideChecklistBoss = null; _guideChecklistPage = 0;
    }

    private string GuideSheetName(string sheet, uint id, bool localized)
    {
        if (id == 0 || _isReplay) return "";
        var key = (sheet, id, localized);
        if (_guideNames.TryGetValue(key, out var cached)) return cached;
        if (_guideNameBudget <= 0) return "";
        --_guideNameBudget;
        var name = sheet switch
        {
            "Action" => localized ? Service.LuminaDisplayRow<Lumina.Excel.Sheets.Action>(id)?.Name.ToString() : Service.LuminaRow<Lumina.Excel.Sheets.Action>(id)?.Name.ToString(),
            "Status" => localized ? Service.LuminaDisplayRow<Lumina.Excel.Sheets.Status>(id)?.Name.ToString() : Service.LuminaRow<Lumina.Excel.Sheets.Status>(id)?.Name.ToString(),
            "BNpcName" => localized ? Service.LuminaDisplayRow<Lumina.Excel.Sheets.BNpcName>(id)?.Singular.ToString() : Service.LuminaRow<Lumina.Excel.Sheets.BNpcName>(id)?.Singular.ToString(),
            "ContentFinderCondition" => localized ? Service.LuminaDisplayRow<Lumina.Excel.Sheets.ContentFinderCondition>(id)?.Name.ToString() : Service.LuminaRow<Lumina.Excel.Sheets.ContentFinderCondition>(id)?.Name.ToString(),
            _ => ""
        } ?? "";
        if (_guideNames.Count >= 4096) _guideNames.Clear();
        _guideNames[key] = name;
        return name;
    }

    private string DisplayActionName(uint id)
    {
        if (_isReplay) return LookupActionName(id) ?? "";
        var name = GuideSheetName("Action", id, true);
        return name.Length == 0 ? LookupActionName(id) ?? "" : name;
    }

    private string GuideDutyName(GuideDuty duty)
    {
        var name = GuideSheetName("ContentFinderCondition", duty.ContentID, true);
        return name.Length == 0 ? duty.EnglishName + " [EN]" : name;
    }
    private string GuideBossName(GuideBoss boss)
    {
        var name = GuideSheetName("BNpcName", _guideBossNames.GetValueOrDefault(boss.Name), true);
        return name.Length == 0 ? boss.Name + " [EN]" : name;
    }
    private string GuideMechanicName(GuideBoss boss, GuideMechanic mechanic)
    {
        var name = GuideSheetName("Action", _guideActionNames.GetValueOrDefault((boss.Name, mechanic.Name)), true);
        return name.Length == 0 ? mechanic.Name + " [EN]" : name;
    }

    private IEnumerable<GuideMatch> LiveGuideMatches()
        => _guideMatches.Where(match => ReferenceEquals(match.Boss, _guideFrame.Boss) && !_guideFrame.Upcoming && !_guideFrame.Ambiguous
            && _cfg.EnableGuides && GuideCastIsCurrent(match, _guideDuty, _ws.Actors.Find(match.Cast.ActorID), _ws.CurrentTime));

    private IEnumerable<GuideSignal> LiveGuideSignals()
        => _guideFrame.Active.Where(signal => _cfg.EnableGuides && signal.Until > _ws.CurrentTime
            && _guideDuty?.ContentID == _ws.CurrentCFCID && _guideDuty.TerritoryID == _ws.CurrentZone
            && _ws.Actors.Find(signal.SourceID) is { IsDeadOrDestroyed: false } source && source.OID == signal.SourceOID && source.NameID == signal.SourceNameID
            && (signal.OwnerID == 0 || source.OwnerID == signal.OwnerID && _ws.Actors.Find(signal.OwnerID) is { IsDeadOrDestroyed: false })
            && (signal.Kind == GuideSignalKind.Cast
                ? source.CastInfo is { EventHappened: false } cast && cast.IsSpell() && cast.Action.ID == signal.ID
                    && float.IsFinite(cast.NPCRemainingTime) && cast.NPCRemainingTime > 0 && Math.Abs((_ws.CurrentTime.AddSeconds(cast.NPCRemainingTime) - signal.Until).TotalSeconds) < .3
                : signal.Kind == GuideSignalKind.Status && _ws.Actors.Find(signal.TargetID) is { IsDeadOrDestroyed: false } target
                    && target.Statuses.Any(status => status.ID == signal.ID && status.SourceID == signal.SourceID && status.ExpireAt > _ws.CurrentTime)));

    private void ResolveGuideAction(Actor actor, uint action)
    {
        foreach (var signal in _guideFrame.Active.Where(signal => signal.Kind == GuideSignalKind.Cast && signal.SourceID == actor.InstanceID
            && signal.SourceOID == actor.OID && signal.SourceNameID == actor.NameID && signal.ID == action))
            _guideEncounter.Resolve(signal);
    }

    private void AssociateGuideHazards(List<DecisionHazard> hazards)
    {
        if (_liveGuide == null) return;
        foreach (var signal in LiveGuideSignals())
        {
            var associated = false;
            for (var index = 0; index < hazards.Count; ++index)
            {
                var linked = GuideDecisionBridge.Associate(hazards[index], signal, GuideMechanicName(signal.Boss, signal.Mechanic), _liveGuide.SourceUrl);
                associated |= linked != hazards[index];
                hazards[index] = linked;
            }
            if (!associated && hazards.Count < 128 && _ws.Actors.Find(signal.SourceID) is { } actor)
                hazards.Add(GuideDecisionBridge.Annotation(signal, V(actor.Position), GuideMechanicName(signal.Boss, signal.Mechanic), _liveGuide.SourceUrl));
        }
    }

    private bool DrawGuideCentralHints()
    {
        if (!_cfg.GuideCentralAlerts) return false;
        var shown = false;
        var player = _ws.Party[PartyState.PlayerSlot];
        if (player == null || player.IsDeadOrDestroyed) return false;
        foreach (var signal in LiveGuideSignals().OrderByDescending(signal => signal.TargetID == player.InstanceID).ThenBy(signal => signal.Until).Take(2))
        {
            var remaining = Math.Max(0, (signal.Until - _ws.CurrentTime).TotalSeconds);
            ImGui.SetWindowFontScale(_cfg.GuideAlertScale);
            ImGui.TextColored(GuideColor(_cfg.GuideActiveColor), GuideChecklistInstruction(signal.Boss, signal.Phase, signal.Mechanic, signal)
                + " — " + GuideMechanicName(signal.Boss, signal.Mechanic));
            ImGui.SetWindowFontScale(1);
            var total = signal.Kind == GuideSignalKind.Cast ? _ws.Actors.Find(signal.SourceID)?.CastInfo?.TotalTime ?? 0 : 0;
            ImGui.ProgressBar(total > 0 && float.IsFinite(total) ? Math.Clamp((float)remaining / total, 0, 1) : 0,
                new(360 * _cfg.GuideAlertScale, 0), $"{remaining:F1}s");
            shown = true;
        }
        return shown;
    }

    private string GuideChecklistInstruction(GuideBoss boss, GuidePhase phase, GuideMechanic mechanic, GuideSignal? live = null)
    {
        if (live == null) return GuideChecklistPresentation.Instruction(GuideRules.LiveGuidance(mechanic, phase, boss), GuideClientLanguage);
        var confirmed = _ws.Party[PartyState.PlayerSlot] is { IsDeadOrDestroyed: false } player && GuideAlertGuidance(live, player) != GuidanceKind.None;
        return GuideChecklistPresentation.Instruction(live.Guidance, GuideClientLanguage, confirmed);
    }

    private GuidanceKind GuideAlertGuidance(GuideSignal signal, Actor player)
    {
        var guidance = signal.Guidance;
        if (guidance == GuidanceKind.Tankbuster && signal.TargetID != player.InstanceID) return GuidanceKind.None;
        if (guidance is GuidanceKind.Stack or GuidanceKind.Soak)
            return PresentationFrame.Hazards.Any(hazard => hazard.Prediction.CasterID == signal.SourceID && hazard.Prediction.ActionID == signal.ID
                && hazard.Prediction.Guidance == guidance && hazard.Prediction.TargetID != 0 && hazard.Prediction.Confidence >= _cfg.WarningConfidence / 100f)
                ? guidance : GuidanceKind.None;
        if (guidance == GuidanceKind.Avoid)
            return PresentationFrame.Hazards.Any(hazard => hazard.Prediction.CasterID == signal.SourceID && hazard.Prediction.ActionID == signal.ID
                && hazard.SpatiallyKnown && hazard.Prediction.Confidence >= _cfg.VisualConfidence / 100f
                && ForetellDecisionCore.Contains(hazard.Prediction, V(player.Position), hazard.Prediction.Activation)) ? guidance : GuidanceKind.None;
        return guidance;
    }

    private bool GuideOwnsCentralPrediction(ActivePrediction prediction)
        => _cfg.GuideCentralAlerts && LiveGuideSignals().Any(signal => signal.Kind == GuideSignalKind.Cast
            && signal.SourceID == prediction.CasterID && signal.ID == prediction.ActionID && Math.Abs((signal.Until - prediction.Activation).TotalSeconds) < .4
            && (prediction.Guidance is GuidanceKind.None or GuidanceKind.Marker || signal.Guidance == prediction.Guidance));

    private void DrawGuideSidebar()
    {
        DrawGuideChecklist();
    }

    private void DrawGuideState(GuideSnapshot snapshot)
    {
        var state = snapshot.State switch
        {
            GuideState.ReadingCache => GuideText("Reading cache…", "Lecture du cache…", "Cache wird geladen…", "キャッシュ読込中…"),
            GuideState.Downloading => GuideText("Downloading wiki…", "Téléchargement du wiki…", "Wiki wird geladen…", "Wiki取得中…"),
            GuideState.Preparing => GuideText("Preparing guide…", "Préparation de la fiche…", "Anleitung wird aufbereitet…", "攻略準備中…"),
            GuideState.Ready => GuideText("Guide ready", "Fiche prête", "Anleitung bereit", "攻略準備完了"),
            GuideState.Offline => GuideText("Source unavailable · cached guide retained", "Source indisponible · fiche en cache conservée", "Quelle nicht verfügbar · Cache bleibt nutzbar", "取得失敗・キャッシュを使用"),
            GuideState.Failed => GuideText("Guide unavailable or unsupported page structure", "Fiche indisponible ou structure de page non prise en charge", "Anleitung nicht verfügbar oder Seitenstruktur nicht unterstützt", "攻略取得不可、または未対応のページ構造"),
            _ => GuideText("Choose an instance to prepare", "Choisis une instance à préparer", "Instanz zum Vorbereiten auswählen", "準備するコンテンツを選択")
        };
        ImGui.TextWrapped(state);
        if (snapshot.Document is { MechanicCount: 0 }) ImGui.TextWrapped(GuideNarrativeNotice());
        DrawGuideProgress(snapshot);
        if (snapshot.Error.Length != 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextWrapped(snapshot.Error);
            ImGui.PopStyleColor();
        }
    }

    private static string GuideNarrativeNotice() => GuideText(
        "Narrative guide: boss advice is available, but no named abilities were extracted. No automatic guide matching or alerts for these paragraphs.",
        "Guide narratif : conseils disponibles par boss, mais aucune capacité nommée extraite. Pas de correspondance ni d’alerte automatique du guide pour ces paragraphes.",
        "Textanleitung: Hinweise je Boss verfügbar, aber keine benannten Fähigkeiten extrahiert. Keine automatische Guide-Zuordnung oder Warnungen aus diesen Absätzen.",
        "文章形式の攻略：ボス別の助言は利用できますが、名称付きアクションは抽出されていません。この文章からの自動照合・警告はありません。");

    private void DrawGuideDocument(GuideDocument document, bool live)
    {
        ImGui.TextWrapped(GuideText("Source: Console Games Wiki (EN). The live checklist shows only the identified current/upcoming boss. Cast and status associations retain their evidence; geometry comes from client/observed data, never an AI summary.",
            "Source : Console Games Wiki (EN). La checklist en jeu n’affiche que le boss actuel/à venir identifié. Les associations de casts et statuts conservent leurs preuves ; les zones proviennent des données du client/observées, jamais d’un résumé IA.",
            "Quelle: Console Games Wiki (EN). Die Live-Checkliste zeigt nur den erkannten aktuellen/nächsten Boss. Zauber/Status bleiben nachvollziehbar; Geometrie stammt aus Spieldaten, nie aus KI-Zusammenfassungen.",
            "出典：Console Games Wiki（英語）。ライブ表示は特定した現在・次のボスのみ。詠唱・ステータスの根拠を保持し、範囲はゲームデータに基づきます。AI要約は使用しません。"));
        if (ImGui.SmallButton(GuideText("Copy source link", "Copier le lien source", "Quellenlink kopieren", "出典リンクをコピー"))) ImGui.SetClipboardText(document.SourceUrl);
        ImGui.SameLine(); ImGui.TextDisabled($"r{document.Revision} · {document.RetrievedAt.ToLocalTime():g}");
        var matches = live ? LiveGuideMatches().ToArray() : [];
        var bossIndex = 0;
        foreach (var boss in document.Bosses)
        {
            ImGui.PushID(bossIndex++);
            var activeBoss = matches.Any(match => ReferenceEquals(match.Boss, boss));
            if (activeBoss) ImGui.SetNextItemOpen(true, ImGuiCond.Always);
            if (ImGui.CollapsingHeader((live ? GuideBossName(boss) : boss.Name + " [EN]") + "###Boss", ImGuiTreeNodeFlags.DefaultOpen))
            {
                foreach (var phase in boss.Phases)
                {
                    var phaseName = GuidePhaseName(phase);
                    ImGui.TextColored(ProductAccent, phaseName);
                    if (GuideContextSummary(boss, phase, document) is { } contextSummary) ImGui.TextWrapped(contextSummary);
                    if (phase.Context.Length != 0 && ImGui.TreeNode(phaseName + " · " + GuideText("Context (EN)", "Contexte (EN)", "Kontext (EN)", "背景（英語）")))
                    { ImGui.TextWrapped(phase.Context); ImGui.TreePop(); }
                    var mechanicIndex = 0;
                    foreach (var mechanic in phase.Mechanics)
                    {
                        ImGui.PushID(phaseName + ":" + mechanicIndex++);
                        var active = matches.Any(match => ReferenceEquals(match.Mechanic, mechanic));
                        var instruction = GuidePreparation.Instruction(mechanic);
                        var guidance = GuideRules.LiveGuidance(mechanic, phase, boss);
                        ImGui.TextColored(active ? new Vector4(1, .83f, .28f, 1) : new Vector4(.85f, .85f, .85f, 1),
                            (active ? "▶ " : "• ") + (live ? GuideMechanicName(boss, mechanic) : mechanic.Name + " [EN]"));
                        ImGui.TextWrapped(guidance != GuidanceKind.None ? GuidanceInstruction(guidance, MechanicKind.Unknown, GeometryKind.Unknown) : GuidePreparation.Text(instruction, GuideClientLanguage));
                        if (GuideSummaryFor(boss, phase, mechanic, document) is { } summary) ImGui.TextWrapped(summary);
                        if (active) ImGui.TextDisabled(GuideText("Cast matched · response may be unresolved", "Cast relié · réponse éventuellement non résolue", "Zauber zugeordnet · Reaktion ggf. ungeklärt", "詠唱対応・対処は未確定の場合あり"));
                        if (ImGui.TreeNode(GuideText("Full source (EN)", "Source complète (EN)", "Vollständige Quelle (EN)", "原文（英語）")))
                        {
                            ImGui.TextWrapped(mechanic.Text);
                            if (live) DrawGuideStatusNames(mechanic);
                            ImGui.TreePop();
                        }
                        ImGui.PopID();
                    }
                }
            }
            ImGui.PopID();
        }
    }

    private void DrawGuideStatusNames(GuideMechanic mechanic)
    {
        if (_ws.Party[PartyState.PlayerSlot] is not { } player) return;
        foreach (var status in player.Statuses.Where(status => status.ID != 0))
        {
            var english = GuideSheetName("Status", status.ID, false);
            if (english.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(mechanic.Text,
                @"(?<![\p{L}\p{N}])" + System.Text.RegularExpressions.Regex.Escape(english) + @"(?![\p{L}\p{N}])",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) continue;
            var localized = GuideSheetName("Status", status.ID, true);
            if (localized.Length != 0) ImGui.TextDisabled($"{english} → {localized} (Status {status.ID})");
        }
    }

    private static string GuidePhaseName(GuidePhase phase)
    {
        if (phase.Name.Length == 0) return GuideText("Documented mechanics", "Mécaniques documentées", "Dokumentierte Mechaniken", "記載されたギミック");
        if (phase.Name.StartsWith("Phase ", StringComparison.OrdinalIgnoreCase) && int.TryParse(phase.Name[6..], out var number))
            return GuideText("Phase", "Phase", "Phase", "フェーズ") + " " + number;
        return phase.Name + " [EN]";
    }

    private void DrawGuideManager()
    {
        if (ImGui.Checkbox(GuideText("Automatic instance guides", "Fiches automatiques en instance", "Automatische Instanzanleitungen", "コンテンツ攻略の自動取得"), ref _cfg.EnableGuides)) _cfg.Modified.Fire();
        if (_guides == null) return;
        ImGui.TextWrapped(GuideText("Available provider: Console Games Wiki (English source). Coverage is not guaranteed; other providers are not connected yet. Model controls are in Local AI; overlays are in Display.",
            "Fournisseur disponible : Console Games Wiki (source anglaise). Couverture non garantie ; les autres sources ne sont pas encore raccordées. Réglages du modèle dans IA locale ; overlays dans Affichage.",
            "Verfügbare Quelle: Console Games Wiki (Englisch). Keine vollständige Abdeckung; weitere Quellen noch nicht angebunden. Modell unter Lokale KI, Overlays unter Anzeige.",
            "情報源：Console Games Wiki（英語）。全コンテンツ対応の保証なし。他の情報源は未接続。モデル設定はローカルAI、表示設定は表示タブ。"));
        var snapshot = _guides.Snapshot;
        DrawGuideState(snapshot);
        DrawGuideSummaryProgress(snapshot.Document);
        ImGui.TextWrapped(GuideText("Local cache: 128 guides / 64 MiB, refreshed after 7 days on demand. Separate translated-summary cache: 128 entries / 64 MiB. Original conditions and English source remain accessible. Ambiguous signals never become confirmed instructions.",
            "Cache local : 128 fiches / 64 Mio, actualisées au besoin après 7 jours. Cache distinct des résumés traduits : 128 entrées / 64 Mio. Les conditions et la source anglaise restent consultables. Un signal ambigu ne devient jamais une consigne confirmée.",
            "Lokaler Cache: 128 Anleitungen / 64 MiB; nach 7 Tagen bei Bedarf aktualisiert. Separater Zusammenfassungs-Cache: 128 Einträge / 64 MiB. Bedingungen und englische Quelle bleiben verfügbar. Mehrdeutige Signale sind keine bestätigten Anweisungen.",
            "攻略キャッシュ：128件・64 MiB、7日経過後に更新。翻訳要約は別キャッシュ128件・64 MiB。条件と英語原文を保持。曖昧なシグナルから確定指示は出しません。"));
        if (_cfg.Mode is ForetellMode.Observe or ForetellMode.Legacy)
            ImGui.TextWrapped(GuideText("Use Hybrid or Foretell mode for the live sidebar and central hints.", "Passe en mode Hybrid ou Foretell pour la liste en combat et les consignes centrales.", "Hybrid- oder Foretell-Modus zeigt Seitenleiste und zentrale Hinweise im Kampf.", "戦闘中の表示にはHybridまたはForetellモードを使用。"));
        ImGui.BeginDisabled(!_cfg.EnableGuides || _guideCombat || snapshot.State is GuideState.ReadingCache or GuideState.Downloading or GuideState.Preparing);
        if (_guideDuty != null && ImGui.Button(GuideText("Prepare current instance", "Préparer l’instance actuelle", "Aktuelle Instanz vorbereiten", "現在のコンテンツを準備"))) _guides.RequestGuide(_guideDuty, true);
        if (snapshot.Duty != null && ImGui.Button(GuideText("Refresh selected guide", "Actualiser la fiche sélectionnée", "Ausgewählte Anleitung aktualisieren", "選択した攻略を更新"))) _guides.RequestGuide(snapshot.Duty, true);
        _guideCatalogue ??= Task.Run(() => Service.LuminaSheet<Lumina.Excel.Sheets.ContentFinderCondition>()!
            .Where(content => content.RowId != 0 && content.TerritoryType.RowId != 0 && content.Name.ToString().Length != 0)
            .Select(content => (new GuideDuty(content.RowId, content.TerritoryType.RowId, content.Name.ToString()), Service.LuminaDisplayRow<Lumina.Excel.Sheets.ContentFinderCondition>(content.RowId)?.Name.ToString() ?? content.Name.ToString()))
            .OrderBy(entry => entry.Item2, StringComparer.CurrentCultureIgnoreCase).ToArray());
        ImGui.InputText(GuideText("Instance search", "Rechercher une instance", "Instanz suchen", "コンテンツ検索"), ref _guideSearch, 120);
        if (_guideCatalogue.IsCompletedSuccessfully && _guideSearch.Length >= 2)
            foreach (var entry in _guideCatalogue.Result.Where(entry => entry.Display.Contains(_guideSearch, StringComparison.CurrentCultureIgnoreCase) || entry.Duty.EnglishName.Contains(_guideSearch, StringComparison.OrdinalIgnoreCase)).Take(12))
                if (ImGui.Selectable(entry.Display + "###" + entry.Duty.Key)) _guides.RequestGuide(entry.Duty);
        if (_guideCatalogue.IsFaulted) ImGui.TextWrapped(GuideText("Instance catalogue unavailable.", "Catalogue des instances indisponible.", "Instanzkatalog nicht verfügbar.", "コンテンツ一覧を取得できません。"));
        ImGui.EndDisabled();
        if (snapshot.Document is { } document) { ImGui.Separator(); ImGui.TextWrapped(GuideDutyName(document.Duty)); DrawGuideDocument(document, document == _liveGuide); }
    }
}
