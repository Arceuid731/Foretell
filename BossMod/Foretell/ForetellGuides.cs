using Dalamud.Bindings.ImGui;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private ForetellGuideService? _guides;
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
        if (_liveGuide == null || _guideDuty == null) return;
        foreach (var actor in _ws.Actors)
        {
            if (actor.Type != ActorType.Enemy || actor.IsAlly || actor.IsDeadOrDestroyed || actor.NameID == 0) continue;
            var bossName = GuideSheetName("BNpcName", actor.NameID, false);
            if (bossName.Length == 0) continue;
            var bosses = _liveGuide.Bosses.Where(boss => GuideNames.Boss(boss.Name) == GuideNames.Boss(bossName)).ToArray();
            if (bosses.Length != 1) continue;
            _guideBossNames[bosses[0].Name] = actor.NameID;
            if (MatchGuideActor(_liveGuide, _guideDuty, actor, now, (sheet, id) => GuideSheetName(sheet, id, false)) is not { } match) continue;
            _guideMatches.Add(match);
            _guideActionNames[(match.Boss.Name, match.Mechanic.Name)] = match.Cast.ActionID;
        }
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
        => _guideMatches.Where(match => _cfg.EnableGuides && GuideCastIsCurrent(match, _guideDuty, _ws.Actors.Find(match.Cast.ActorID), _ws.CurrentTime));

    private bool DrawGuideCentralHints()
    {
        var shown = false;
        var player = _ws.Party[PartyState.PlayerSlot];
        if (player == null || player.IsDeadOrDestroyed) return false;
        foreach (var match in LiveGuideMatches().Take(2))
        {
            var instruction = GuidePersonalResponse(match, player.InstanceID, _ws.Actors.Find(match.Cast.ActorID));
            if (instruction == GuideInstruction.Unprepared) continue;
            ImGui.TextColored(ProductAccent, GuidePreparation.Text(instruction, GuideClientLanguage));
            ImGui.TextDisabled(GuideMechanicName(match.Boss, match.Mechanic) + " · " + GuideText("Wiki + live cast", "Wiki + cast reçu", "Wiki + beobachteter Zauber", "Wiki＋詠唱検知"));
            shown = true;
        }
        return shown;
    }

    private void DrawGuideSidebar()
    {
        if (_guideDuty == null) return;
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + new Vector2(20, Math.Max(80, viewport.Size.Y * .2f)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(360, Math.Min(600, viewport.Size.Y * .65f)), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(GuideText("Encounter guide", "Fiche de l’instance", "Instanzanleitung", "コンテンツ攻略") + "###ForetellGuideSidebar", ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.TextWrapped(GuideDutyName(_guideDuty));
            if (_guides?.Snapshot.Duty == _guideDuty) DrawGuideState(_guides.Snapshot);
            if (_liveGuide != null) DrawGuideDocument(_liveGuide, true);
        }
        ImGui.End();
    }

    private void DrawGuideState(GuideSnapshot snapshot)
    {
        var state = snapshot.State switch
        {
            GuideState.ReadingCache => GuideText("Reading cache…", "Lecture du cache…", "Cache wird geladen…", "キャッシュ読込中…"),
            GuideState.Downloading => GuideText("Downloading wiki…", "Téléchargement du wiki…", "Wiki wird geladen…", "Wiki取得中…"),
            GuideState.Preparing => GuideText("Preparing guide…", "Préparation de la fiche…", "Anleitung wird aufbereitet…", "攻略準備中…"),
            GuideState.Ready => GuideText("Document ready · synchronization partial", "Fiche prête · synchronisation partielle", "Dokument bereit · teilweise synchronisiert", "攻略読込済み・同期は部分対応"),
            GuideState.Offline => GuideText("Source unavailable · cached guide retained", "Source indisponible · fiche en cache conservée", "Quelle nicht verfügbar · Cache bleibt nutzbar", "取得失敗・キャッシュを使用"),
            GuideState.Failed => GuideText("Guide unavailable or unsupported page structure", "Fiche indisponible ou structure de page non prise en charge", "Anleitung nicht verfügbar oder Seitenstruktur nicht unterstützt", "攻略取得不可、または未対応のページ構造"),
            _ => GuideText("Choose an instance to prepare", "Choisis une instance à préparer", "Instanz zum Vorbereiten auswählen", "準備するコンテンツを選択")
        };
        ImGui.TextWrapped(state);
        if (snapshot.Error.Length != 0) ImGui.TextDisabled(snapshot.Error);
    }

    private void DrawGuideDocument(GuideDocument document, bool live)
    {
        ImGui.TextWrapped(GuideText("Source: Console Games Wiki (EN). Only received, uniquely matched casts are highlighted; phases and variants may be unresolved. Zones use independent game evidence.",
            "Source : Console Games Wiki (EN). Seuls les casts reçus et reliés sans ambiguïté sont surlignés ; phases et variantes peuvent rester inconnues. Les zones utilisent les signaux du jeu séparément.",
            "Quelle: Console Games Wiki (EN). Nur eindeutig zugeordnete beobachtete Zauber werden hervorgehoben; Phasen und Varianten können ungeklärt bleiben. Zonen verwenden separate Spieldaten.",
            "出典：Console Games Wiki（英語）。一意に対応した詠唱のみ強調。フェーズ・派生は未確定の場合があります。範囲表示は別のゲームデータに基づきます。"));
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
                    if (phase.Context.Length != 0 && ImGui.TreeNode(phaseName + " · " + GuideText("Context (EN)", "Contexte (EN)", "Kontext (EN)", "背景（英語）")))
                    { ImGui.TextWrapped(phase.Context); ImGui.TreePop(); }
                    var mechanicIndex = 0;
                    foreach (var mechanic in phase.Mechanics)
                    {
                        ImGui.PushID(phaseName + ":" + mechanicIndex++);
                        var active = matches.Any(match => ReferenceEquals(match.Mechanic, mechanic));
                        var instruction = GuidePreparation.Instruction(mechanic);
                        ImGui.TextColored(active ? new Vector4(1, .83f, .28f, 1) : new Vector4(.85f, .85f, .85f, 1),
                            (active ? "▶ " : "• ") + (live ? GuideMechanicName(boss, mechanic) : mechanic.Name + " [EN]"));
                        ImGui.TextWrapped(GuidePreparation.Text(instruction, GuideClientLanguage));
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
        ImGui.SameLine();
        if (ImGui.Checkbox(GuideText("Sidebar", "Liste latérale", "Seitenleiste", "サイドバー"), ref _cfg.GuideSidebar)) _cfg.Modified.Fire();
        if (_guides == null) return;
        var snapshot = _guides.Snapshot;
        DrawGuideState(snapshot);
        ImGui.TextWrapped(GuideText("Local cache: up to 128 guides / 64 MiB, refreshed after 7 days on demand. Simple prepared instructions use the client language. Complex source text remains in English, with no automatic tactical translation yet.",
            "Cache local : jusqu’à 128 fiches / 64 Mio, actualisées au besoin après 7 jours. Les consignes simples préparées utilisent la langue du client. Le texte source complexe reste en anglais ; sa traduction tactique automatique reste à réaliser.",
            "Lokaler Cache: bis zu 128 Anleitungen / 64 MiB; nach 7 Tagen bei Bedarf aktualisiert. Einfache Anweisungen nutzen die Clientsprache. Komplexe Quelltexte bleiben vorerst auf Englisch.",
            "ローカルキャッシュ：最大128件・64 MiB。7日経過後に必要に応じて更新。簡単な指示はクライアント言語、複雑な原文は英語のままで自動翻訳は未対応。"));
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
