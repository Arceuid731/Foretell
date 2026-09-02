using System.IO;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private void RefreshEncounterIdentity(EncounterMemory encounter, uint cfcID)
    {
        string? territoryName = null;
        try
        {
            territoryName = Service.LuminaRow<Lumina.Excel.Sheets.TerritoryType>(encounter.TerritoryID)
                ?.PlaceName.ValueNullable?.NameNoArticle.ToString();
        }
        catch (Exception e) { Service.LogVerbose($"[Foretell] Territory name lookup rejected safely: {e.Message}"); }
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
        try
        {
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
        }
        catch (Exception e) { Service.LogVerbose($"[Foretell] Content name lookup rejected safely: {e.Message}"); }

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
            string? learnedName = null;
            try { learnedName = Service.LuminaRow<Lumina.Excel.Sheets.BNpcName>(source.NameID)?.Singular.ToString(); }
            catch { }
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
            var actionName = LookupActionName(mechanic.TriggerID);
            if (!string.IsNullOrWhiteSpace(actionName))
                return actionName;
        }
        if (!string.IsNullOrWhiteSpace(mechanic.TriggerDetail))
        {
            var detail = mechanic.TriggerDetail.Replace('\\', '/');
            var leaf = detail[(detail.LastIndexOf('/') + 1)..];
            if (leaf.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)) leaf = leaf[..^5];
            if (leaf.Length > 0 && mechanic.TriggerKind is ObservationKind.NativeVFXSpawn or ObservationKind.NativeVFXDestroy or ObservationKind.VFX)
                return $"Visual effect · {leaf}";
            if (mechanic.TriggerKind == ObservationKind.NpcYell)
                return $"NPC call · {DisplayText(detail, 60)}";
        }
        var label = ObservationLabel(mechanic.TriggerKind);
        return mechanic.TriggerID == 0
            ? label
            : $"{label} · 0x{mechanic.TriggerID:X}";
    }

    private static string? LookupActionName(uint actionID)
    {
        try { return Service.LuminaRow<Lumina.Excel.Sheets.Action>(actionID)?.Name.ToString(); }
        catch { return null; }
    }

    private void PurgeMechanic(uint territoryID, string key)
    {
        if (!_store.Encounters.TryGetValue(territoryID, out var encounter) || !encounter.Mechanics.Remove(key, out var mechanic))
            return;

        foreach (var edgeKey in encounter.Timeline.Where(kv => kv.Value.From == key || kv.Value.To == key).Select(kv => kv.Key).ToArray())
            encounter.Timeline.Remove(edgeKey);
        foreach (var phase in encounter.Phases.Values)
            phase.Signals.Remove(key);
        foreach (var compositeKey in encounter.Composites.Where(kv => kv.Value.Signals.Contains(key)).Select(kv => kv.Key).ToArray())
            encounter.Composites.Remove(compositeKey);

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
        foreach (var compositeKey in encounter.Composites.Where(item => item.Value.Signals.Any(signal => signal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).Select(item => item.Key).ToArray())
            encounter.Composites.Remove(compositeKey);
        foreach (var episodeID in _episodes.Where(item => item.Value.Trigger.TerritoryID == territoryID && item.Value.Trigger.ActorOID == sourceOID).Select(item => item.Key).ToArray())
        {
            _episodes.Remove(episodeID);
            _predictions.Remove(episodeID);
            foreach (var sequence in _effectSequenceEpisodes.Where(item => item.Value == episodeID).Select(item => item.Key).ToArray())
                _effectSequenceEpisodes.Remove(sequence);
        }
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
            _episodeFinalization.Clear();
            _episodeCleanup.Clear();
            _predictions.Clear();
            _effectSequenceEpisodes.Clear();
            _tracks.Clear();
            _session = NewSession(_territory);
        }
        foreach (var actionID in actionIDs)
            RemoveOrphanGlobalKnowledge(actionID);
    }

    private void PurgeTopology(uint territoryID, string fingerprint)
    {
        if (!_store.Encounters.TryGetValue(territoryID, out var encounter)) return;
        encounter.Topologies.Remove(fingerprint);
        if (territoryID == _territory && _topologyFingerprint == fingerprint)
        {
            _topologyFingerprint = "";
            _topologyAnalysis = null;
            InvalidateTopology();
        }
    }

    private void PurgeTimelineEdge(uint territoryID, string key)
    {
        if (_store.Encounters.TryGetValue(territoryID, out var encounter))
            encounter.Timeline.Remove(key);
    }

    private void PurgeComposite(uint territoryID, string key)
    {
        if (_store.Encounters.TryGetValue(territoryID, out var encounter))
            encounter.Composites.Remove(key);
    }

    private void PurgePhase(uint territoryID, int phase)
    {
        if (!_store.Encounters.TryGetValue(territoryID, out var encounter)) return;
        encounter.Phases.Remove(phase);
        foreach (var key in encounter.Timeline.Where(item => item.Value.Phase == phase).Select(item => item.Key).ToArray())
            encounter.Timeline.Remove(key);
        foreach (var key in encounter.Composites.Where(item => item.Value.Phase == phase).Select(item => item.Key).ToArray())
            encounter.Composites.Remove(key);
    }

    private void PurgePhaseSignal(uint territoryID, int phase, string signal)
    {
        if (!_store.Encounters.TryGetValue(territoryID, out var encounter)) return;
        if (encounter.Phases.TryGetValue(phase, out var phaseMemory))
            phaseMemory.Signals.Remove(signal);
        foreach (var key in encounter.Timeline.Where(item => item.Value.Phase == phase && (item.Value.From == signal || item.Value.To == signal)).Select(item => item.Key).ToArray())
            encounter.Timeline.Remove(key);
        foreach (var key in encounter.Composites.Where(item => item.Value.Phase == phase && item.Value.Signals.Contains(signal)).Select(item => item.Key).ToArray())
            encounter.Composites.Remove(key);
    }

    private void PurgePhaseBoundary(uint territoryID, string signature)
    {
        if (_store.Encounters.TryGetValue(territoryID, out var encounter))
            encounter.PhaseBoundaries.Remove(signature);
        if (territoryID == _territory)
            _phaseBoundariesThisPull.Remove(signature);
    }

    private void PurgeSession(string sessionID)
        => _store.Sessions.RemoveAll(session => string.Equals(session.SessionID, sessionID, StringComparison.Ordinal));

    private void DeleteStorageFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var rawRoot = Path.GetFullPath(_rawDir) + Path.DirectorySeparatorChar;
        var replayRoot = Path.GetFullPath(_replayDir) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rawRoot, StringComparison.OrdinalIgnoreCase) && !fullPath.StartsWith(replayRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("path is outside Foretell storage");
        if (string.Equals(fullPath, Path.GetFullPath(_rawPath), StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullPath, Path.GetFullPath(_replayPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("active recording cannot be deleted");
        File.Delete(fullPath);
        _lastStorageRefresh = default;
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
