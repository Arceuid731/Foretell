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
    private readonly List<GuideSignal> _guideInstantSignals = [];
    private readonly GuideActionQueue _guidePendingActions = new();

    internal const GuideLanguage GuideContentLanguage = GuideLanguage.English;
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
        if (_guideSummaries != null) _guideSummaries.CaptureConversation = _cfg.GuideDebugConversation;
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
            _guideSummaries?.Update(null, GuideContentLanguage, false, inCombat && _cfg.GuidePauseInCombat, _cfg.GuideSummaryGpu, "");
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
        var pageSource = _guides.Snapshot.Document;
        if ((now - _guideSampleAt).TotalMilliseconds < 200 && now >= _guideSampleAt) return;
        _guideSampleAt = now;
        _guideSummaries?.Update(pageSource?.Duty == _guideDuty || _guideDuty == null ? pageSource : null, GuideContentLanguage,
            _cfg.GuideLocalSummaries, inCombat && _cfg.GuidePauseInCombat, _cfg.GuideSummaryGpu, _guideFrame.Boss?.Name ?? "", _cfg.GuideContextTokens, _cfg.GuideMemoryGiB, _cfg.GuideModelID);
        UpdateGuideIdPlan(_guideSummaries?.Snapshot?.Prepared);
        _liveGuide = _guideSummaries?.Snapshot is { Prepared: { } prepared } analysis && prepared.Duty == _guideDuty
            && prepared.SourceHash == pageSource?.SourceHash && analysis.Language == GuideContentLanguage && analysis.ModelID == GuideModelCatalog.Get(_cfg.GuideModelID).ID
            ? prepared : null;
        if (_liveGuide == null) { _guideFrame = GuideCombatFrame.Empty; _guideSignals.Clear(); _guideInstantSignals.Clear(); _guidePendingActions.Clear(); }
        if (_guideDuty == null) return;
        ResolvePendingGuideActions(now);
        var player = _ws.Party[PartyState.PlayerSlot];
        List<GuideActorState> actors = [];
        foreach (var actor in _ws.Actors)
        {
            if (actor.Type != ActorType.Enemy || actor.IsAlly || actor.NameID == 0 || actor.IsDestroyed && !actor.IsDead) continue;
            var idPlan = GuideIDs(_liveGuide);
            var bossName = idPlan == null ? GuideSheetName("BNpcName", actor.NameID, false) : idPlan.Boss(actor.NameID)?.Name ?? "";
            if (bossName.Length == 0) continue;
            actors.Add(new(actor.InstanceID, actor.OID, actor.NameID, bossName, actor.IsDead,
                actor.InCombat || actor.CastInfo != null, player == null ? float.MaxValue : (actor.Position - player.Position).Length()));
            if (_liveGuide == null) continue;
            var bosses = _liveGuide.Bosses.Where(boss => GuideNames.Boss(boss.Name) == GuideNames.Boss(bossName)).ToArray();
            if (bosses.Length != 1) continue;
            _guideBossNames[bosses[0].Name] = actor.NameID;
        }
        _guideEncounter.Update(_liveGuide, actors, false);
        if (_liveGuide == null) return;
        _guideSignals.Clear();
        UpdateGuideEventBindings(now);
        _guideInstantSignals.RemoveAll(signal => signal.Until <= now || !ReferenceEquals(signal.Boss, _guideEncounter.Frame.Boss)
            || _guideEncounter.Frame.Upcoming || _guideEncounter.Frame.Ambiguous);
        _guideSignals.AddRange(_guideInstantSignals);
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

    internal static GuideMatch? MatchNamedGuideHelper(GuideDocument document, GuideDuty duty, Actor helper, GuideCombatFrame frame, DateTime now, Func<string, uint, string> name)
    {
        if (frame is not { Boss: { } boss, Upcoming: false, Ambiguous: false } || helper.Type != ActorType.Helper || helper.OwnerID != 0
            || helper.IsAlly || helper.IsDeadOrDestroyed || helper.NameID == 0
            || helper.CastInfo is not { EventHappened: false } cast || !cast.IsSpell() || !float.IsFinite(cast.NPCRemainingTime) || cast.NPCRemainingTime is <= 0 or > 120
            || GuideNames.Boss(name("BNpcName", helper.NameID)) != GuideNames.Boss(boss.Name)) return null;
        var match = GuideSynchronization.Match(document, new(duty, helper.InstanceID, helper.OID, helper.NameID, boss.Name,
            cast.Action.ID, name("Action", cast.Action.ID), now.AddSeconds(cast.NPCRemainingTime)), now);
        return ReferenceEquals(match?.Boss, boss) ? match : null;
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
        _guideBindingSeen.Clear(); _guideBindingDocument = null; _guideBindingBoss = null;
        _guideBindingScope = "";
        _guideIdentity = default; _guideDuty = null; _liveGuide = null; _guideSampleAt = default;
        _guideBossNames.Clear(); _guideActionNames.Clear();
        _guideSignals.Clear(); _guideInstantSignals.Clear(); _guidePendingActions.Clear(); _guideEncounter.Reset(); _guideFrame = GuideCombatFrame.Empty;
        _guideEntryDismissed = false; _guideListLayout = null;
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
        return name.Length == 0 ? boss.DisplayName.Length > 0 ? boss.DisplayName : boss.Name : name;
    }
    private string GuideMechanicName(GuideBoss boss, GuideMechanic mechanic)
    {
        var name = GuideSheetName("Action", _guideActionNames.GetValueOrDefault((boss.Name, mechanic.Name)), true);
        return name.Length == 0 ? mechanic.Advice?.DisplayName ?? mechanic.Name : name;
    }

    private IEnumerable<GuideSignal> LiveGuideSignals()
        => _guideFrame.Active.Where(signal => _cfg.EnableGuides && signal.Until > _ws.CurrentTime
            && _guideDuty?.ContentID == _ws.CurrentCFCID && _guideDuty.TerritoryID == _ws.CurrentZone
            && _ws.Actors.Find(signal.SourceID) is { IsDeadOrDestroyed: false } source && source.OID == signal.SourceOID && source.NameID == signal.SourceNameID
            && source.OwnerID == signal.OwnerID && (signal.OwnerID == 0 || _ws.Actors.Find(signal.OwnerID) is { IsDeadOrDestroyed: false })
            && (signal.Trigger == null || GuideEventMatching.TargetApplies(signal.Trigger.Target,
                signal.Kind == GuideSignalKind.Cast ? source.CastInfo?.TargetID ?? 0 : signal.TargetID,
                _ws.Party.Player()?.InstanceID ?? 0, _ws.Party.FindSlot(signal.Kind == GuideSignalKind.Cast ? source.CastInfo?.TargetID ?? 0 : signal.TargetID) is >= 0 and < PartyState.MaxPartySize))
            && (signal.Kind == GuideSignalKind.Cast
                ? source.CastInfo is { EventHappened: false } cast && cast.IsSpell() && cast.Action.ID == signal.ID
                    && float.IsFinite(cast.NPCRemainingTime) && cast.NPCRemainingTime > 0 && Math.Abs((_ws.CurrentTime.AddSeconds(cast.NPCRemainingTime) - signal.Until).TotalSeconds) < .3
                : signal.Kind == GuideSignalKind.Action || signal.Kind == GuideSignalKind.Status && _ws.Actors.Find(signal.TargetID) is { IsDeadOrDestroyed: false } target
                    && target.Statuses.Any(status => status.ID == signal.ID && status.SourceID == signal.SourceID && status.ExpireAt > _ws.CurrentTime
                        && (status.Extra & 0xFF) >= (signal.Trigger?.MinimumStacks ?? 0))));

    private void ResolveGuideAction(Actor actor, ActorCastEvent actionEvent)
    {
        var action = actionEvent.Action.ID;
        var visible = _guideFrame.Active.Where(signal => signal.Kind == GuideSignalKind.Cast && signal.SourceID == actor.InstanceID
            && signal.SourceOID == actor.OID && signal.SourceNameID == actor.NameID && signal.ID == action).ToArray();
        foreach (var signal in visible)
            _guideEncounter.Resolve(signal);
        if (visible.Length != 0 || actor.CastInfo?.Action.ID == action || actionEvent.Action.Type != ActionType.Spell || !_cfg.EnableGuides
            || _guideDuty == null || _liveGuide == null || _guideFrame is not { Boss: { } boss, Upcoming: false, Ambiguous: false }
            || actor.IsAlly || actor.IsDeadOrDestroyed || actor.Type is not (ActorType.Enemy or ActorType.Helper)) return;
        var now = _ws.CurrentTime;
        _guidePendingActions.Enqueue(_guideDuty, boss, actor, _ws.Actors.Find(actor.OwnerID), action, actionEvent.MainTargetID, now);
        ResolvePendingGuideActions(now);
    }

    private void ResolvePendingGuideActions(DateTime now)
    {
        if (_liveGuide == null || _guideDuty == null) { _guidePendingActions.Clear(); return; }
        foreach (var signal in _guidePendingActions.Resolve(_liveGuide, _guideDuty, _guideEncounter.Frame, now,
            id => _ws.Actors.Find(id), (sheet, id) => GuideSheetName(sheet, id, false), GuideIDs(_liveGuide)))
        {
            _guideInstantSignals.RemoveAll(existing => existing.Until <= now || existing.SourceID == signal.SourceID && existing.ID == signal.ID);
            if (_guideInstantSignals.Count >= 32) continue;
            _guideInstantSignals.Add(signal);
            _guideActionNames[(signal.Boss.Name, signal.Mechanic.Name)] = signal.ID;
        }
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
            {
                var target = signal.Kind == GuideSignalKind.Status ? _ws.Actors.Find(signal.TargetID) : null;
                hazards.Add(GuideDecisionBridge.Annotation(signal, V(actor.Position), GuideMechanicName(signal.Boss, signal.Mechanic), _liveGuide.SourceUrl,
                    target is { IsDeadOrDestroyed: false } ? V(target.Position) : null));
            }
        }
    }

    private IEnumerable<ForetellCentralAlert> GuideCentralHints()
    {
        if (!_cfg.GuideCentralAlerts) yield break;
        var player = _ws.Party[PartyState.PlayerSlot];
        if (player == null || player.IsDeadOrDestroyed) yield break;
        foreach (var signal in CentralGuideSignals())
        {
            var remaining = Math.Max(0, (signal.Until - _ws.CurrentTime).TotalSeconds);
            var total = signal.Kind == GuideSignalKind.Cast ? _ws.Actors.Find(signal.SourceID)?.CastInfo?.TotalTime ?? 0 : 0;
            var personal = signal.TargetID == player.InstanceID || PresentationFrame.Hazards.Any(hazard =>
                ForetellCentralPresentation.Personal(hazard.Prediction, V(player.Position), player.InstanceID)
                && GuideOwnsCentralPrediction(hazard.Prediction, [signal]));
            yield return new(GuideRolePresentation.Prefix(signal.Mechanic.Advice?.Roles ?? []) + GuideChecklistInstruction(signal.Boss, signal.Phase, signal.Mechanic, signal),
                GuideMechanicName(signal.Boss, signal.Mechanic), signal.Kind == GuideSignalKind.Action ? -1 : remaining, total,
                signal.Until, personal, true);
        }
    }

    private string GuideChecklistInstruction(GuideBoss boss, GuidePhase phase, GuideMechanic mechanic, GuideSignal? live = null)
    {
        if (live?.Instruction.Length > 0) return live.Instruction;
        if (mechanic.Advice is { } advice && advice.Language == GuideContentLanguage) return GuideListFlow.Instruction(mechanic, advice.Cue);
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
        => GuideOwnsCentralPrediction(prediction, CentralGuideSignals());

    private bool GuideOwnsCentralPrediction(ActivePrediction prediction, GuideSignal[] displayed)
    {
        var actor = _ws.Actors.Find(prediction.CasterID);
        return GuideCentralPresentation.Owns(prediction, displayed)
            || GuideCentralPresentation.OwnsRelated(prediction, displayed, GuideSheetName("Action", prediction.ActionID, false), actor?.NameID ?? 0, actor?.OwnerID ?? 0);
    }

    private GuideSignal[] CentralGuideSignals()
        => _cfg.GuideCentralAlerts && _ws.Party[PartyState.PlayerSlot] is { IsDeadOrDestroyed: false } player
            ? GuideCentralPresentation.Select(LiveGuideSignals().Where(signal => GuideCombatRelevance.IsActionable(signal.Mechanic)), player.InstanceID) : [];

    private void DrawGuideSidebar()
    {
        DrawGuideChecklist();
    }

    private void DrawGuideState(GuideSnapshot snapshot)
    {
        var state = snapshot.State switch
        {
            GuideState.ReadingCache => GuideText("Opening guide…", "Ouverture du guide…", "Anleitung wird geöffnet…", "攻略読込中…"),
            GuideState.Downloading => GuideText("Downloading guide…", "Téléchargement du guide…", "Anleitung wird heruntergeladen…", "攻略取得中…"),
            GuideState.Preparing => GuideText("Reading guide…", "Lecture du guide…", "Anleitung wird gelesen…", "攻略読込中…"),
            GuideState.Ready => PreparedGuide != null ? GuideText("Guide ready", "Guide prêt", "Anleitung bereit", "攻略準備完了")
                : GuideText("Guide downloaded", "Guide téléchargé", "Anleitung heruntergeladen", "攻略取得済み"),
            GuideState.Offline => GuideText("Offline · using the saved guide", "Hors ligne · guide enregistré disponible", "Offline · gespeicherte Anleitung", "オフライン・保存済み攻略を使用"),
            GuideState.Failed => GuideText("Guide unavailable. Try again later.", "Guide indisponible. Réessaie plus tard.", "Anleitung nicht verfügbar. Später erneut versuchen.", "攻略を取得できません。後で再試行してください。"),
            _ => GuideText("Choose an instance", "Choisis une instance", "Instanz auswählen", "コンテンツを選択")
        };
        ImGui.TextWrapped(state);
        if (snapshot.State is GuideState.Downloading or GuideState.ReadingCache) DrawGuideProgress(snapshot);
    }

    private void DrawGuideDocument(GuideDocument document, bool live)
    {
        if (document.Summary.Length > 0) ImGui.TextWrapped(document.Summary);
        if (ImGui.SmallButton(GuideText("Copy source link", "Copier le lien du guide", "Quellenlink kopieren", "攻略リンクをコピー"))) ImGui.SetClipboardText(document.SourceUrl);
        foreach (var boss in document.Bosses)
        {
            ImGui.PushID(boss.Name);
            if (ImGui.CollapsingHeader(GuideBossName(boss) + "###Boss", ImGuiTreeNodeFlags.DefaultOpen))
            {
                if (boss.Summary.Length > 0) ImGui.TextWrapped(boss.Summary);
                foreach (var phase in boss.Phases)
                    foreach (var mechanic in phase.Mechanics)
                    {
                        var signal = live ? LiveGuideSignals().FirstOrDefault(signal => signal.Mechanic == mechanic) : null;
                        var instruction = GuideRolePresentation.Prefix(mechanic.Advice?.Roles ?? []) + GuideChecklistInstruction(boss, phase, mechanic, signal);
                        ImGui.TextColored(GuideColor(signal != null ? _cfg.GuideActiveColor : _cfg.GuideTextColor),
                            GuideMechanicName(boss, mechanic) + " — " + instruction);
                        if (ImGui.IsItemHovered()) DrawGuideMechanicTooltip(boss, phase, mechanic, instruction);
                    }
            }
            ImGui.PopID();
        }
    }

    private static string GuidePhaseName(GuidePhase phase) => phase.Name.Length == 0
        ? GuideText("Mechanics", "Mécaniques", "Mechaniken", "ギミック") : phase.Name;

    private void DrawGuideManager()
    {
        if (ImGui.Checkbox(GuideText("Automatic instance guides", "Guides automatiques en instance", "Automatische Instanzanleitungen", "コンテンツ攻略の自動取得"), ref _cfg.EnableGuides)) _cfg.Modified.Fire();
        if (_guides == null) return;
        var snapshot = _guides.Snapshot;
        DrawGuideProviderStatus(snapshot.Document);
        DrawGuideState(snapshot);
        DrawGuideSummaryProgress(snapshot.Document);
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
        if (PreparedGuide is { } document)
        {
            ImGui.Separator();
            ImGui.TextWrapped(GuideDutyName(document.Duty));
            DrawGuideDocument(document, document == _liveGuide);
        }
    }
}
