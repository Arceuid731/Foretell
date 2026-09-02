namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private void RefreshEncounterIdentity(EncounterMemory encounter, uint cfcID)
    {
        var territoryName = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(encounter.TerritoryID)
            ?.PlaceName.ValueNullable?.NameNoArticle.ToString();
        if (!string.IsNullOrWhiteSpace(territoryName))
            encounter.TerritoryName = territoryName;
        if (string.IsNullOrWhiteSpace(encounter.TerritoryName))
            encounter.TerritoryName = $"Territory {encounter.TerritoryID}";

        if (cfcID == 0)
        {
            if (encounter.ContentFinderConditionID == 0)
            {
                encounter.ContentName = encounter.TerritoryName;
                encounter.ContentCategory = "Open world";
            }
            return;
        }

        encounter.ContentFinderConditionID = cfcID;
        if (Service.LuminaRow<Lumina.Excel.Sheets.ContentFinderCondition>(cfcID) is { } cfc)
        {
            var contentName = cfc.Name.ToString();
            if (!string.IsNullOrWhiteSpace(contentName))
                encounter.ContentName = contentName;

            var contentTypeLink = Member(cfc, "ContentType");
            var contentTypeID = ToUInt(Member(contentTypeLink, "RowId")) ?? 0;
            var contentTypeName = contentTypeID != 0
                ? Service.LuminaRow<Lumina.Excel.Sheets.ContentType>(contentTypeID)?.Name.ToString()
                : null;
            encounter.ContentCategory = string.IsNullOrWhiteSpace(contentTypeName) ? "Instanced content" : contentTypeName;
        }

        if (string.IsNullOrWhiteSpace(encounter.ContentName))
            encounter.ContentName = encounter.TerritoryName;
        if (string.IsNullOrWhiteSpace(encounter.ContentCategory))
            encounter.ContentCategory = "Instanced content";
    }

    private string EncounterDisplayName(EncounterMemory encounter)
        => string.IsNullOrWhiteSpace(encounter.ContentName) ? encounter.TerritoryName : encounter.ContentName;

    private string SourceDisplayName(SourceMemory source)
    {
        if (!string.IsNullOrWhiteSpace(source.Name))
            return source.Name;

        if (source.NameID != 0)
        {
            var learnedName = Service.LuminaRow<Lumina.Excel.Sheets.BNpcName>(source.NameID)?.Singular.ToString();
            if (!string.IsNullOrWhiteSpace(learnedName))
                return learnedName;
        }

        var live = _ws.Actors.FirstOrDefault(actor => actor.OID == source.OID);
        if (live != null && !string.IsNullOrWhiteSpace(live.Name))
            return live.Name;

        if (source.OID == 0 || source.Kind == SourceKind.Environment)
            return "Environment";
        return $"{source.Kind} 0x{source.OID:X8}";
    }

    private static string MechanicDisplayName(ContextualMechanic mechanic)
    {
        if (mechanic.TriggerID != 0 && mechanic.TriggerKind is ObservationKind.CastStart or ObservationKind.CastFinish or ObservationKind.ActionResolved or ObservationKind.AffectedTarget)
        {
            var actionName = Service.LuminaRow<Lumina.Excel.Sheets.Action>(mechanic.TriggerID)?.Name.ToString();
            if (!string.IsNullOrWhiteSpace(actionName))
                return actionName;
        }
        return mechanic.TriggerID == 0
            ? mechanic.TriggerKind.ToString()
            : $"{mechanic.TriggerKind} 0x{mechanic.TriggerID:X}";
    }

    private void PurgeMechanic(uint territoryID, string key)
    {
        if (!_store.Encounters.TryGetValue(territoryID, out var encounter) || !encounter.Mechanics.Remove(key, out var mechanic))
            return;

        foreach (var edgeKey in encounter.Timeline.Where(kv => kv.Value.From == key || kv.Value.To == key).Select(kv => kv.Key).ToArray())
            encounter.Timeline.Remove(edgeKey);
        foreach (var phase in encounter.Phases.Values)
            phase.Signals.Remove(key);

        foreach (var episodeID in _episodes.Where(kv => kv.Value.SignalKey == key).Select(kv => kv.Key).ToArray())
        {
            _episodes.Remove(episodeID);
            _predictions.Remove(episodeID);
            foreach (var sequence in _effectSequenceEpisodes.Where(kv => kv.Value == episodeID).Select(kv => kv.Key).ToArray())
                _effectSequenceEpisodes.Remove(sequence);
        }

        RemoveOrphanGlobalKnowledge(mechanic.TriggerID);
    }

    private void PurgeSource(uint territoryID, uint sourceOID)
    {
        if (!_store.Encounters.TryGetValue(territoryID, out var encounter))
            return;
        foreach (var key in encounter.Mechanics.Where(kv => kv.Value.SourceOID == sourceOID).Select(kv => kv.Key).ToArray())
            PurgeMechanic(territoryID, key);
        encounter.Sources.Remove(sourceOID);

        var prefix = $"{sourceOID:X}:";
        foreach (var edgeKey in encounter.Timeline.Where(kv => kv.Value.From.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || kv.Value.To.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToArray())
            encounter.Timeline.Remove(edgeKey);
        foreach (var phase in encounter.Phases.Values)
            foreach (var signal in phase.Signals.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
                phase.Signals.Remove(signal);
    }

    private void PurgeEncounter(uint territoryID)
    {
        if (!_store.Encounters.Remove(territoryID, out var encounter))
            return;
        var actionIDs = encounter.Mechanics.Values.Select(mechanic => mechanic.TriggerID).Where(id => id != 0).Distinct().ToArray();
        _store.Sessions.RemoveAll(session => session.TerritoryID == territoryID);

        if (territoryID == _territory)
        {
            _episodes.Clear();
            _predictions.Clear();
            _effectSequenceEpisodes.Clear();
            _recentSignals.Clear();
            _tracks.Clear();
            _session = NewSession(_territory);
        }
        foreach (var actionID in actionIDs)
            RemoveOrphanGlobalKnowledge(actionID);
    }

    private void PurgeCategory(string category)
    {
        foreach (var territoryID in _store.Encounters.Values.Where(encounter => string.Equals(encounter.ContentCategory, category, StringComparison.Ordinal)).Select(encounter => encounter.TerritoryID).ToArray())
            PurgeEncounter(territoryID);
    }

    private void RemoveOrphanGlobalKnowledge(uint actionID)
    {
        if (actionID == 0 || _store.Encounters.Values.Any(encounter => encounter.Mechanics.Values.Any(mechanic => mechanic.TriggerID == actionID)))
            return;
        _store.Mechanics.Remove(actionID);
        foreach (var key in _store.Timeline.Where(kv => kv.Value.From == actionID || kv.Value.To == actionID).Select(kv => kv.Key).ToArray())
            _store.Timeline.Remove(key);
    }
}
