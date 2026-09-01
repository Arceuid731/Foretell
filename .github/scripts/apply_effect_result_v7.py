from pathlib import Path
ROOT = Path('.')

def read(p): return (ROOT / p).read_text(encoding='utf-8-sig')
def write(p, s): (ROOT / p).write_text(s, encoding='utf-8')
def rep(s, a, b, label):
    if a not in s: raise RuntimeError(f'missing anchor {label}')
    return s.replace(a, b, 1)

# Model: explicit semantic EffectResult observation.
p='BossMod/Foretell/ForetellModel.cs'; s=read(p)
s=rep(s,
'''    CastStart, CastFinish, ActionResolved, AffectedTarget,\n''',
'''    CastStart, CastFinish, ActionResolved, AffectedTarget, EffectResult,\n''', 'effectresult kind')
write(p,s)

# Engine: exact action sequence -> episode correlation and direct event subscription.
p='BossMod/Foretell/ForetellEngine.cs'; s=read(p)
s=rep(s,
'''    private Dictionary<long, ActivePrediction> _predictions = [];\n    private Queue<ForetellObservation> _recentSignals = new();\n''',
'''    private Dictionary<long, ActivePrediction> _predictions = [];\n    private Dictionary<uint, long> _effectSequenceEpisodes = [];\n    private Queue<ForetellObservation> _recentSignals = new();\n''', 'effect sequence map')
s=rep(s,
'''            _ws.Actors.CastEvent.Subscribe(OnCastEvent),\n            _ws.Actors.EventObjectStateChange.Subscribe(OnEventObjectState),\n''',
'''            _ws.Actors.CastEvent.Subscribe(OnCastEvent),\n            _ws.Actors.EffectResult.Subscribe(OnEffectResult),\n            _ws.Actors.EventObjectStateChange.Subscribe(OnEventObjectState),\n''', 'effectresult subscription')
s=rep(s,
'''        _predictions.Clear();\n        _recentSignals.Clear();\n''',
'''        _predictions.Clear();\n        _effectSequenceEpisodes.Clear();\n        _recentSignals.Clear();\n''', 'effect map territory reset')
write(p,s)

# Observer: preserve sequence on per-target ActionEffect, expose semantic EffectResult,
# and avoid duplicating it through the generic WorldOperation path.
p='BossMod/Foretell/ForetellObserver.cs'; s=read(p)
s=rep(s,
'''            var affected = Observation(ObservationKind.AffectedTarget, actor, action, target: target.ID);\n            var effects = target.Effects.ValidEffects();\n''',
'''            var affected = Observation(ObservationKind.AffectedTarget, actor, action, target: target.ID);\n            affected.Numeric["action.globalSequence"] = ev.GlobalSequence;\n            var effects = target.Effects.ValidEffects();\n''', 'affected sequence')
s=rep(s,
'''        if (ev.MainTargetID != 0 && !seen.Contains(ev.MainTargetID))\n            ProcessObservation(Observation(ObservationKind.AffectedTarget, actor, action, target: ev.MainTargetID, detail: "main-target-only"));\n    }\n\n    private void OnTargetableChanged(Actor actor)\n''',
'''        if (ev.MainTargetID != 0 && !seen.Contains(ev.MainTargetID))\n        {\n            var affected = Observation(ObservationKind.AffectedTarget, actor, action, target: ev.MainTargetID, detail: "main-target-only");\n            affected.Numeric["action.globalSequence"] = ev.GlobalSequence;\n            ProcessObservation(affected);\n        }\n    }\n\n    private void OnEffectResult(Actor target, uint sequence, int targetIndex)\n    {\n        var obs = Observation(ObservationKind.EffectResult, primary: sequence, secondary: (uint)Math.Max(0, targetIndex), target: target.InstanceID, detail: "effect-result");\n        obs.Numeric["effectResult.sequence"] = sequence;\n        obs.Numeric["effectResult.targetIndex"] = targetIndex;\n        ProcessObservation(obs);\n    }\n\n    private void OnTargetableChanged(Actor actor)\n''', 'effectresult handler')
s=rep(s,
'''        if (op is NetworkState.OpServerIPC)\n        {\n            RegisterCapability("worldop.ServerIPC", op.GetType(), "ServerIPC", false, true, "duplicate of Foretell unconditional raw server IPC tap");\n            return;\n        }\n\n        var actorID =''',
'''        if (op is NetworkState.OpServerIPC)\n        {\n            RegisterCapability("worldop.ServerIPC", op.GetType(), "ServerIPC", false, true, "duplicate of Foretell unconditional raw server IPC tap");\n            return;\n        }\n        if (op is ActorState.OpEffectResult)\n        {\n            RegisterCapability("worldop.EffectResult", op.GetType(), "EffectResult", false, true, "duplicate of Foretell semantic EffectResult stream");\n            return;\n        }\n\n        var actorID =''', 'effectresult worldop dedup')
write(p,s)

# Learning: attach ActionEffect global sequence to the correlated mechanic episode, then make
# EffectResult correlation exact rather than temporal whenever possible.
p='BossMod/Foretell/ForetellLearning.cs'; s=read(p)
s=rep(s,
'''        switch (observation.Kind)\n        {\n            case ObservationKind.AffectedTarget:\n''',
'''        switch (observation.Kind)\n        {\n            case ObservationKind.ActionResolved:\n                if (observation.Numeric.TryGetValue("action.globalSequence", out var sequenceValue) && sequenceValue > 0 && sequenceValue <= uint.MaxValue)\n                {\n                    var sequence = (uint)sequenceValue;\n                    _effectSequenceEpisodes[sequence] = episode.ID;\n                    if (_effectSequenceEpisodes.Count > 4096)\n                    {\n                        foreach (var stale in _effectSequenceEpisodes.Where(kv => !_episodes.ContainsKey(kv.Value)).Select(kv => kv.Key).ToArray())\n                            _effectSequenceEpisodes.Remove(stale);\n                    }\n                }\n                break;\n            case ObservationKind.AffectedTarget:\n''', 'register action sequence')
s=rep(s,
'''    private MechanicEpisode? BestEpisode(ForetellObservation observation)\n    {\n        MechanicEpisode? best = null;\n''',
'''    private MechanicEpisode? BestEpisode(ForetellObservation observation)\n    {\n        if (observation.Kind == ObservationKind.EffectResult && observation.PrimaryID != 0\n            && _effectSequenceEpisodes.TryGetValue(observation.PrimaryID, out var exactID)\n            && _episodes.TryGetValue(exactID, out var exact) && !exact.Finalized)\n            return exact;\n\n        MechanicEpisode? best = null;\n''', 'exact effectresult lookup')
s=rep(s,
'''        foreach (var id in _episodes.Where(kv => kv.Value.Finalized && kv.Value.FinalizeAt.AddSeconds(20) < now).Select(kv => kv.Key).ToArray())\n            _episodes.Remove(id);\n''',
'''        foreach (var id in _episodes.Where(kv => kv.Value.Finalized && kv.Value.FinalizeAt.AddSeconds(20) < now).Select(kv => kv.Key).ToArray())\n        {\n            _episodes.Remove(id);\n            foreach (var sequence in _effectSequenceEpisodes.Where(kv => kv.Value == id).Select(kv => kv.Key).ToArray())\n                _effectSequenceEpisodes.Remove(sequence);\n        }\n''', 'effect map cleanup')
write(p,s)

# Contract documentation.
p='BossMod/Foretell/README.md'; s=read(p)
s += '''\nEffectResult is consumed as a first-class semantic stream and correlated back to the originating ActionEffect by the native global action sequence; the generic WorldOperation copy is explicitly de-duplicated. This gives downstream hit/status confirmation an exact causal edge instead of relying only on time proximity.\n'''
write(p,s)
print('effect result v7 applied')
