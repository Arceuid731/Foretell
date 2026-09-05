using System.IO;
using System.Text.Json;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    private string ExportEncounterKnowledge(EncounterMemory encounter)
    {
        var path = Path.Combine(_replayDir, $"foretell-knowledge-T{encounter.TerritoryID}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var payload = new
        {
            schema = _store.Schema,
            exportedAt = DateTime.UtcNow,
            content = EncounterDisplayName(encounter),
            encounter
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, _diagnosticJson));
        return path;
    }

    private void RefreshEncounterIdentity(EncounterMemory encounter, uint cfcID)
    {
        if (_isReplay)
        {
            encounter.ContentFinderConditionID = cfcID;
            if (encounter.TerritoryName.Length == 0) encounter.TerritoryName = $"Territory {encounter.TerritoryID}";
            return;
        }
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

        if (_isReplay) return source.OID == 0 ? "Recorded environment" : $"Recorded source 0x{source.OID:X}";
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

    private string MechanicDisplayName(ContextualMechanic mechanic)
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

    private string? LookupActionName(uint actionID)
    {
        if (_isReplay) return null;
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
        foreach (var causalKey in encounter.CausalEdges.Where(kv => kv.Value.Cause == key).Select(kv => kv.Key).ToArray())
            encounter.CausalEdges.Remove(causalKey);
        foreach (var compositeKey in encounter.Composites.Where(kv => kv.Value.Signals.Contains(key)).Select(kv => kv.Key).ToArray())
            encounter.Composites.Remove(compositeKey);
        foreach (var triggerKey in encounter.TriggerContexts.Where(kv => kv.Value.Signal == key).Select(kv => kv.Key).ToArray())
            PurgeTriggerContext(territoryID, triggerKey);

        foreach (var episodeID in _episodes.Where(kv => kv.Value.SignalKey == key).Select(kv => kv.Key).ToArray())
        {
            _episodes.Remove(episodeID);
            _predictions.Remove(episodeID);
            foreach (var sequence in _effectSequenceEpisodes.Where(kv => kv.Value == episodeID).Select(kv => kv.Key).ToArray())
                _effectSequenceEpisodes.Remove(sequence);
        }
        foreach (var forecast in _timelineForecasts.Values.Where(item => item.MechanicKey == key).ToArray())
        {
            _timelineForecasts.Remove(forecast.ID);
            _predictions.Remove(forecast.ID);
        }
        _store.DecisionAudit.RemoveAll(entry => entry.TerritoryID == territoryID && entry.SignalKey == key);

        RemoveOrphanGlobalKnowledge(mechanic.TriggerID);
    }

    private void PurgeSource(uint territoryID, uint sourceOID)
    {
        if (!_store.Encounters.TryGetValue(territoryID, out var encounter))
            return;
        foreach (var key in encounter.Mechanics.Where(kv => kv.Value.SourceOID == sourceOID).Select(kv => kv.Key).ToArray())
            PurgeMechanic(territoryID, key);
        encounter.Sources.Remove(sourceOID);
        _store.DecisionAudit.RemoveAll(entry => entry.TerritoryID == territoryID && entry.SourceOID == sourceOID);

        var prefix = $"{sourceOID:X}:";
        foreach (var edgeKey in encounter.Timeline.Where(kv => kv.Value.From.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || kv.Value.To.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToArray())
            encounter.Timeline.Remove(edgeKey);
        foreach (var phase in encounter.Phases.Values)
            foreach (var signal in phase.Signals.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
                phase.Signals.Remove(signal);
        foreach (var compositeKey in encounter.Composites.Where(item => item.Value.Signals.Any(signal => signal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).Select(item => item.Key).ToArray())
            encounter.Composites.Remove(compositeKey);
        foreach (var causalKey in encounter.CausalEdges.Where(item => item.Value.Cause.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(item => item.Key).ToArray())
            encounter.CausalEdges.Remove(causalKey);
        foreach (var triggerKey in encounter.TriggerContexts.Where(item => item.Value.Signal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || item.Value.BossOID == sourceOID).Select(item => item.Key).ToArray())
            PurgeTriggerContext(territoryID, triggerKey);
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
        _store.DecisionAudit.RemoveAll(entry => entry.TerritoryID == territoryID);

        if (territoryID == _territory)
        {
            _episodes.Clear();
            _episodeFinalization.Clear();
            _episodeCleanup.Clear();
            _predictions.Clear();
            _effectSequenceEpisodes.Clear();
            _timelineForecasts.Clear();
            _tracks.Clear();
            _signalOccurrencesThisPull.Clear();
            _skippedTriggerContextsThisPull.Clear();
            _triggerForecastCandidates.Clear();
            _retryTriggerForecastCandidates = false;
            _bossHealthTracks.Clear();
            _bossHealthSnapshots.Clear();
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
        foreach (var forecast in _timelineForecasts.Values.Where(item => item.TerritoryID == territoryID && item.EdgeKey == key).ToArray())
        {
            _timelineForecasts.Remove(forecast.ID);
            _predictions.Remove(forecast.ID);
        }
    }

    private void PurgeComposite(uint territoryID, string key)
    {
        if (_store.Encounters.TryGetValue(territoryID, out var encounter))
            encounter.Composites.Remove(key);
        foreach (var forecast in _timelineForecasts.Values.Where(item => item.TerritoryID == territoryID && item.CompositeKey == key).ToArray())
        {
            _timelineForecasts.Remove(forecast.ID);
            _predictions.Remove(forecast.ID);
        }
    }

    private void PurgeTriggerContext(uint territoryID, string key)
    {
        if (_store.Encounters.TryGetValue(territoryID, out var encounter))
            encounter.TriggerContexts.Remove(key);
        foreach (var forecast in _timelineForecasts.Values.Where(item => item.TerritoryID == territoryID && item.TriggerContextKey == key).ToArray())
        {
            _timelineForecasts.Remove(forecast.ID);
            _predictions.Remove(forecast.ID);
        }
        _triggerForecastCandidates.RemoveAll(item => item.Key == key);
    }

    private void PurgeCausalEdge(uint territoryID, string key)
    {
        if (_store.Encounters.TryGetValue(territoryID, out var encounter))
            encounter.CausalEdges.Remove(key);
    }

    private void PurgeRawOpcode(uint territoryID, uint opcode)
    {
        if (_store.Encounters.TryGetValue(territoryID, out var encounter))
            encounter.RawOpcodes.Remove(opcode);
    }

    private void PurgePhase(uint territoryID, int phase)
    {
        if (!_store.Encounters.TryGetValue(territoryID, out var encounter)) return;
        encounter.Phases.Remove(phase);
        foreach (var key in encounter.Timeline.Where(item => item.Value.Phase == phase).Select(item => item.Key).ToArray())
            PurgeTimelineEdge(territoryID, key);
        foreach (var key in encounter.Composites.Where(item => item.Value.Phase == phase).Select(item => item.Key).ToArray())
            encounter.Composites.Remove(key);
        foreach (var key in encounter.TriggerContexts.Where(item => item.Value.Phase == phase).Select(item => item.Key).ToArray())
            PurgeTriggerContext(territoryID, key);
    }

    private void PurgePhaseSignal(uint territoryID, int phase, string signal)
    {
        if (!_store.Encounters.TryGetValue(territoryID, out var encounter)) return;
        if (encounter.Phases.TryGetValue(phase, out var phaseMemory))
            phaseMemory.Signals.Remove(signal);
        foreach (var key in encounter.Timeline.Where(item => item.Value.Phase == phase && (item.Value.From == signal || item.Value.To == signal)).Select(item => item.Key).ToArray())
            PurgeTimelineEdge(territoryID, key);
        foreach (var key in encounter.Composites.Where(item => item.Value.Phase == phase && item.Value.Signals.Contains(signal)).Select(item => item.Key).ToArray())
            encounter.Composites.Remove(key);
        foreach (var key in encounter.CausalEdges.Where(item => item.Value.Cause == signal).Select(item => item.Key).ToArray())
            encounter.CausalEdges.Remove(key);
        foreach (var key in encounter.TriggerContexts.Where(item => item.Value.Phase == phase && item.Value.Signal == signal).Select(item => item.Key).ToArray())
            PurgeTriggerContext(territoryID, key);
    }

    private void IgnoreSignal(uint territoryID, string signal, string label)
    {
        if (string.IsNullOrWhiteSpace(signal) || signal.Length > 256)
            throw new InvalidDataException("invalid signal key");
        var encounter = Encounter(territoryID);
        if (!encounter.ExcludedSignals.ContainsKey(signal) && encounter.ExcludedSignals.Count >= 4096)
            throw new InvalidDataException("signal exclusion limit reached for this territory");
        encounter.ExcludedSignals[signal] = new()
        {
            Signal = signal,
            Label = string.IsNullOrWhiteSpace(label) ? signal : label[..Math.Min(label.Length, 160)],
            CreatedAt = DateTime.UtcNow
        };
        PurgeSignalKnowledge(territoryID, signal);
    }

    private void RestoreSignal(uint territoryID, string signal)
    {
        if (_store.Encounters.TryGetValue(territoryID, out var encounter))
            encounter.ExcludedSignals.Remove(signal);
    }

    private void PurgeSignalKnowledge(uint territoryID, string signal)
    {
        if (!_store.Encounters.TryGetValue(territoryID, out var encounter)) return;
        if (encounter.Mechanics.ContainsKey(signal))
            PurgeMechanic(territoryID, signal);
        foreach (var phase in encounter.Phases.Values)
            phase.Signals.Remove(signal);
        foreach (var key in encounter.Timeline.Where(item => item.Value.From == signal || item.Value.To == signal).Select(item => item.Key).ToArray())
            PurgeTimelineEdge(territoryID, key);
        foreach (var key in encounter.Composites.Where(item => item.Value.Signals.Contains(signal)).Select(item => item.Key).ToArray())
            PurgeComposite(territoryID, key);
        foreach (var key in encounter.CausalEdges.Where(item => item.Value.Cause == signal).Select(item => item.Key).ToArray())
            encounter.CausalEdges.Remove(key);
        foreach (var key in encounter.TriggerContexts.Where(item => item.Value.Signal == signal).Select(item => item.Key).ToArray())
            PurgeTriggerContext(territoryID, key);
        foreach (var episodeID in _episodes.Where(item => item.Value.SignalKey == signal).Select(item => item.Key).ToArray())
            RemoveEpisode(episodeID);
        foreach (var forecast in _timelineForecasts.Values.Where(item => item.ExpectedSignal == signal || item.MechanicKey == signal).ToArray())
        {
            _timelineForecasts.Remove(forecast.ID);
            _predictions.Remove(forecast.ID);
        }
        _store.DecisionAudit.RemoveAll(entry => entry.TerritoryID == territoryID && entry.SignalKey == signal);
        if (_previousSignal == signal)
        {
            _previousSignal = "";
            _previousSignalTime = default;
        }
    }

    private string ExportSignalFilters()
    {
        var export = new SignalFilterExport();
        foreach (var encounter in _store.Encounters.Values.Where(item => item.ExcludedSignals.Count > 0))
            export.Territories[encounter.TerritoryID] = encounter.ExcludedSignals.Values.OrderBy(item => item.Label).ToList();
        var temporary = _signalFilterPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(export, _diagnosticJson));
        File.Move(temporary, _signalFilterPath, true);
        return _signalFilterPath;
    }

    private int ImportSignalFilters()
    {
        if (!File.Exists(_signalFilterPath))
            throw new FileNotFoundException("export a filter file first, then edit or replace it", _signalFilterPath);
        if (new FileInfo(_signalFilterPath).Length > 4 * 1024 * 1024)
            throw new InvalidDataException("signal filter file exceeds 4 MiB");
        var import = JsonSerializer.Deserialize<SignalFilterExport>(File.ReadAllText(_signalFilterPath), _diagnosticJson)
            ?? throw new InvalidDataException("empty signal filter file");
        if (import.Schema != 1 || import.Territories == null || import.Territories.Count > 2048)
            throw new InvalidDataException("unsupported or invalid signal filter schema");
        var imported = 0;
        foreach (var (territoryID, exclusions) in import.Territories)
        {
            if (territoryID == 0 || exclusions == null) continue;
            foreach (var exclusion in exclusions.Take(4096))
            {
                if (exclusion == null || !IsValidSignalKey(exclusion.Signal)) continue;
                IgnoreSignal(territoryID, exclusion.Signal, exclusion.Label);
                ++imported;
            }
        }
        return imported;
    }

    private static bool IsValidSignalKey(string signal)
    {
        if (string.IsNullOrWhiteSpace(signal) || signal.Length > 256) return false;
        var parts = signal.Split(':', 3);
        return parts.Length == 3
            && uint.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out _)
            && Enum.TryParse<ObservationKind>(parts[1], out _)
            && uint.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out _);
    }

    private void PurgePhaseBoundary(uint territoryID, string signature)
    {
        if (_store.Encounters.TryGetValue(territoryID, out var encounter))
            encounter.PhaseBoundaries.Remove(signature);
        if (territoryID == _territory)
            _phaseBoundariesThisPull.Remove(signature);
    }

    private void PurgeSession(string sessionID)
    {
        _store.Sessions.RemoveAll(session => string.Equals(session.SessionID, sessionID, StringComparison.Ordinal));
        _store.DecisionAudit.RemoveAll(entry => string.Equals(entry.SessionID, sessionID, StringComparison.Ordinal));
    }

    private void PurgeArenaBoundary(uint territoryID, string fingerprint)
    {
        if (_store.Encounters.TryGetValue(territoryID, out var encounter))
            encounter.ArenaBoundaries.Remove(fingerprint);
        if (territoryID == _territory && _arenaBoundary?.Fingerprint == fingerprint)
            _arenaBoundary = null;
    }

    private void DeleteStorageFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var rawRoot = Path.GetFullPath(_rawDir) + Path.DirectorySeparatorChar;
        var replayRoot = Path.GetFullPath(_replayDir) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rawRoot, StringComparison.OrdinalIgnoreCase) && !fullPath.StartsWith(replayRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("path is outside Foretell storage");
        if (string.Equals(fullPath, Path.GetFullPath(_rawPath), StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(_raw.ActivePath) && string.Equals(fullPath, Path.GetFullPath(_raw.ActivePath), StringComparison.OrdinalIgnoreCase))
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
