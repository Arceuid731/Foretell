from pathlib import Path

ROOT = Path('.')

def read(path):
    return (ROOT / path).read_text(encoding='utf-8-sig')

def write(path, text):
    (ROOT / path).write_text(text, encoding='utf-8')

def replace_once(text, old, new, label):
    if old not in text:
        raise RuntimeError(f'missing patch anchor: {label}')
    return text.replace(old, new, 1)

# 1) NetworkState: expose unconditional transient raw server/client streams without altering BMR replay semantics.
p = 'BossMod/Data/NetworkState.cs'
s = read(p)
anchor = '    public IDScrambleFields IDScramble;\n'
insert = '''    // Foretell raw transport taps. These are transient events rather than WorldState operations so the inherited\n    // BMR replay format/configuration keeps its original semantics; Foretell records them in its own replay stream.\n    public readonly struct RawServerIPC(Network.ServerIPC.PacketID id, ushort opcode, uint epoch, uint sourceServerActor, uint targetServerActor, DateTime sendTimestamp, byte[] payload)\n    {\n        public readonly Network.ServerIPC.PacketID ID = id;\n        public readonly ushort Opcode = opcode;\n        public readonly uint Epoch = epoch;\n        public readonly uint SourceServerActor = sourceServerActor;\n        public readonly uint TargetServerActor = targetServerActor;\n        public readonly DateTime SendTimestamp = sendTimestamp;\n        public readonly byte[] Payload = payload;\n    }\n\n    public readonly struct RawClientIPC(uint opcode, DateTime sendTimestamp, byte[] payload)\n    {\n        public readonly uint Opcode = opcode;\n        public readonly DateTime SendTimestamp = sendTimestamp;\n        public readonly byte[] Payload = payload;\n    }\n\n    public Event<RawServerIPC> RawServerIPCReceived = new();\n    public Event<RawClientIPC> RawClientIPCSent = new();\n\n'''
s = replace_once(s, anchor, insert + anchor, 'NetworkState raw streams')
write(p, s)

# 2) WorldStateGameSync: keep packet interceptor active, queue raw packets and publish on game update thread.
p = 'BossMod/Framework/WorldStateGameSync.cs'
s = read(p)
anchor = '    private readonly Network.PacketDecoderGame _decoder = new();\n'
insert = '''    private readonly System.Collections.Concurrent.ConcurrentQueue<NetworkState.RawServerIPC> _foretellRawServerPackets = new();\n    private readonly System.Collections.Concurrent.ConcurrentQueue<NetworkState.RawClientIPC> _foretellRawClientPackets = new();\n'''
s = replace_once(s, anchor, anchor + insert, 'raw packet queues')
old = '''        _netConfig = Service.Config.GetAndSubscribe<ReplayManagementConfig>(config =>\n        {\n            _interceptor.ActiveRecv = config.RecordServerPackets || config.DumpServerPackets;\n            _interceptor.ActiveSend = config.DumpClientPackets;\n        });'''
new = '''        _netConfig = Service.Config.GetAndSubscribe<ReplayManagementConfig>(_ =>\n        {\n            // Foretell learns from the transport itself, even when BMR packet recording/dumping is disabled.\n            // The taps are passive: existing BMR replay/dump settings still decide their own output behavior.\n            _interceptor.ActiveRecv = true;\n            _interceptor.ActiveSend = true;\n        });'''
s = replace_once(s, old, new, 'always-on passive packet taps')
old = '''        _globalOps.Clear();\n\n        _playerEnmity.Clear();'''
new = '''        _globalOps.Clear();\n\n        while (_foretellRawServerPackets.TryDequeue(out var rawServer))\n            _ws.Network.RawServerIPCReceived.Fire(rawServer);\n        while (_foretellRawClientPackets.TryDequeue(out var rawClient))\n            _ws.Network.RawClientIPCSent.Fire(rawClient);\n\n        _playerEnmity.Clear();'''
s = replace_once(s, old, new, 'drain raw packet queues')
old = '''        var id = _opcodeMap.ID(opcode);\n        // targetServerActor is always a player?..\n        var ipc = new NetworkState.ServerIPC(id, opcode, epoch, sourceServerActor, sendTimestamp, [.. payload]);\n        if (_netConfig.Data.RecordServerPackets)'''
new = '''        var id = _opcodeMap.ID(opcode);\n        // Keep a lossless Foretell copy regardless of BMR recorder configuration. It is delivered on Update().\n        var rawPayload = payload.ToArray();\n        _foretellRawServerPackets.Enqueue(new(id, opcode, epoch, sourceServerActor, targetServerActor, sendTimestamp, rawPayload));\n\n        // targetServerActor is always a player?..\n        var ipc = new NetworkState.ServerIPC(id, opcode, epoch, sourceServerActor, sendTimestamp, rawPayload);\n        if (_netConfig.Data.RecordServerPackets)'''
s = replace_once(s, old, new, 'server raw enqueue')
old = '''    private unsafe void ClientIPCSent(uint opcode, Span<byte> payload)\n    {\n        if (_netConfig.Data.DumpClientPackets)'''
new = '''    private unsafe void ClientIPCSent(uint opcode, Span<byte> payload)\n    {\n        _foretellRawClientPackets.Enqueue(new(opcode, DateTime.UtcNow, payload.ToArray()));\n        if (_netConfig.Data.DumpClientPackets)'''
s = replace_once(s, old, new, 'client raw enqueue')
write(p, s)

# 3) Foretell model: explicit raw/log/native observation kinds + lossless binary payloads, schema 5.
p = 'BossMod/Foretell/ForetellModel.cs'
s = read(p)
s = replace_once(s,
'''    MapEffect, LegacyMapEffect, DirectorUpdate,\n    PositionSample, Displacement, ActorSnapshot,\n    ClientMetadata, GenericFeature''',
'''    MapEffect, LegacyMapEffect, DirectorUpdate, SystemLog, ObjectEffect,\n    WorldOperation, ServerIPC, ClientIPC,\n    PositionSample, Displacement, ActorSnapshot,\n    ClientMetadata, GenericFeature''', 'observation kinds')
s = s.replace('public int Schema { get; set; } = 4;', 'public int Schema { get; set; } = 5;')
old = '''    public Dictionary<string, double> Numeric { get; set; } = [];\n    public Dictionary<string, string> Text { get; set; } = [];\n}'''
new = '''    public Dictionary<string, double> Numeric { get; set; } = [];\n    public Dictionary<string, string> Text { get; set; } = [];\n    // Lossless opaque payloads (network packets and any future binary client structures). JSON serializes byte[] as base64.\n    public Dictionary<string, byte[]> Binary { get; set; } = [];\n}'''
s = replace_once(s, old, new, 'binary observation payload')
write(p, s)

# 4) Runtime episodes: every binary byte contributes to the hashed learner space without exploding feature cardinality.
p = 'BossMod/Foretell/ForetellRuntime.cs'
s = read(p)
old = '''    public Dictionary<string, double> FeatureSums { get; } = [];\n    public Dictionary<string, int> FeatureCounts { get; } = [];\n    public bool Finalized { get; set; }'''
new = '''    public Dictionary<string, double> FeatureSums { get; } = [];\n    public Dictionary<string, int> FeatureCounts { get; } = [];\n    public double[] BinaryBuckets { get; } = new double[OnlineClassifier.FabricFeatureCount];\n    public HashSet<string> BinaryKeys { get; } = [];\n    public long BinaryBytes { get; private set; }\n    public bool Finalized { get; set; }'''
s = replace_once(s, old, new, 'binary episode fields')
old = '''        foreach (var (key, value) in observation.Text)\n        {\n            var token = $"@text:{key}={value}";\n            if (FeatureSums.Count >= 4096 && !FeatureSums.ContainsKey(token)) continue;\n            FeatureSums[token] = FeatureSums.GetValueOrDefault(token) + 1;\n            FeatureCounts[token] = FeatureCounts.GetValueOrDefault(token) + 1;\n        }\n    }'''
new = '''        foreach (var (key, value) in observation.Text)\n        {\n            var token = $"@text:{key}={value}";\n            if (FeatureSums.Count >= 4096 && !FeatureSums.ContainsKey(token)) continue;\n            FeatureSums[token] = FeatureSums.GetValueOrDefault(token) + 1;\n            FeatureCounts[token] = FeatureCounts.GetValueOrDefault(token) + 1;\n        }\n        foreach (var (key, bytes) in observation.Binary)\n        {\n            BinaryKeys.Add(key);\n            var lengthKey = $"binary.{key}.length";\n            FeatureSums[lengthKey] = FeatureSums.GetValueOrDefault(lengthKey) + bytes.Length;\n            FeatureCounts[lengthKey] = FeatureCounts.GetValueOrDefault(lengthKey) + 1;\n            BinaryBytes += bytes.LongLength;\n\n            // Signed feature hashing compresses an arbitrary-size opaque packet into the same fixed fabric space.\n            // Every byte participates; the raw bytes are still retained losslessly in Foretell replay.\n            var keyHash = StableBinaryHash(key);\n            for (var i = 0; i < bytes.Length; ++i)\n            {\n                unchecked\n                {\n                    var h = keyHash;\n                    h ^= (uint)i * 0x9E3779B9u;\n                    h *= 16777619u;\n                    h ^= bytes[i];\n                    h *= 16777619u;\n                    var slot = (int)(h % OnlineClassifier.FabricFeatureCount);\n                    var sign = (h & 0x80000000u) == 0 ? 1d : -1d;\n                    var centered = bytes[i] / 127.5d - 1d;\n                    BinaryBuckets[slot] += sign * centered;\n                }\n            }\n        }\n    }\n\n    private static uint StableBinaryHash(string value)\n    {\n        unchecked\n        {\n            var hash = 2166136261u;\n            foreach (var c in value)\n            {\n                hash ^= c;\n                hash *= 16777619u;\n            }\n            return hash;\n        }\n    }'''
s = replace_once(s, old, new, 'binary episode accumulation')
write(p, s)

# 5) Data Fabric: special-case binary blobs, lean raw events, replay coverage, merge binary buckets into ML vector.
p = 'BossMod/Foretell/ForetellDataFabric.cs'
s = read(p)
old = '''        observation.Numeric ??= [];\n        observation.Text ??= [];\n\n        foreach (var (k, v) in _runtimeNumeric)\n            observation.Numeric.TryAdd(k, v);\n        foreach (var (k, v) in _runtimeText)\n            observation.Text.TryAdd(k, v);'''
new = '''        observation.Numeric ??= [];\n        observation.Text ??= [];\n        observation.Binary ??= [];\n\n        // Raw packet/operation observations arrive at high frequency; nearby actor snapshots and semantic events\n        // already carry the cached runtime context, so avoid duplicating it into every transport record.\n        var leanTransport = observation.Kind is ObservationKind.ServerIPC or ObservationKind.ClientIPC or ObservationKind.WorldOperation;\n        if (!leanTransport)\n        {\n            foreach (var (k, v) in _runtimeNumeric)\n                observation.Numeric.TryAdd(k, v);\n            foreach (var (k, v) in _runtimeText)\n                observation.Text.TryAdd(k, v);\n        }'''
s = replace_once(s, old, new, 'lean transport enrichment')
old = '''    private bool TryStoreScalar(object value, Type type, string path, ForetellObservation observation)\n    {\n        if (type.IsEnum)'''
new = '''    private bool TryStoreScalar(object value, Type type, string path, ForetellObservation observation)\n    {\n        if (value is byte[] bytes)\n        {\n            observation.Binary[path] = bytes.ToArray();\n            RegisterCapability(path, type, path, true, false, $"binary {bytes.Length} bytes, lossless");\n            return true;\n        }\n        if (value is ReadOnlyMemory<byte> rom)\n        {\n            observation.Binary[path] = rom.ToArray();\n            RegisterCapability(path, type, path, true, false, $"binary {rom.Length} bytes, lossless");\n            return true;\n        }\n        if (value is Memory<byte> mem)\n        {\n            observation.Binary[path] = mem.ToArray();\n            RegisterCapability(path, type, path, true, false, $"binary {mem.Length} bytes, lossless");\n            return true;\n        }\n        if (value is ArraySegment<byte> segment)\n        {\n            observation.Binary[path] = segment.ToArray();\n            RegisterCapability(path, type, path, true, false, $"binary {segment.Count} bytes, lossless");\n            return true;\n        }\n        if (type.IsEnum)'''
s = replace_once(s, old, new, 'binary scalar handling')
old = '''        observation.Numeric ??= [];\n        observation.Text ??= [];\n        foreach (var key in observation.Numeric.Keys)'''
new = '''        observation.Numeric ??= [];\n        observation.Text ??= [];\n        observation.Binary ??= [];\n        foreach (var key in observation.Numeric.Keys)'''
s = replace_once(s, old, new, 'replay binary init')
old = '''        foreach (var key in observation.Text.Keys)\n            RegisterCapability(key, typeof(ForetellObservation), key, true, false, "replayed recorded feature");\n    }'''
new = '''        foreach (var key in observation.Text.Keys)\n            RegisterCapability(key, typeof(ForetellObservation), key, true, false, "replayed recorded feature");\n        foreach (var key in observation.Binary.Keys)\n            RegisterCapability(key, typeof(ForetellObservation), key, true, false, "replayed lossless binary feature");\n    }'''
s = replace_once(s, old, new, 'replay binary coverage')
old = '''        var result = new double[OnlineClassifier.FeatureCount];\n        Array.Copy(core, result, Math.Min(core.Length, OnlineClassifier.BaseFeatureCount));\n        foreach (var (key, sum) in episode.FeatureSums)'''
new = '''        var result = new double[OnlineClassifier.FeatureCount];\n        Array.Copy(core, result, Math.Min(core.Length, OnlineClassifier.BaseFeatureCount));\n\n        if (episode.BinaryBytes > 0)\n        {\n            var scale = 1d / Math.Sqrt(Math.Max(1, episode.BinaryBytes));\n            for (var i = 0; i < episode.BinaryBuckets.Length; ++i)\n            {\n                var slot = OnlineClassifier.BaseFeatureCount + i;\n                result[slot] = Math.Clamp(result[slot] + Math.Tanh(episode.BinaryBuckets[i] * scale), -4, 4);\n            }\n            foreach (var key in episode.BinaryKeys)\n                MarkCapabilityUsed(key);\n        }\n\n        foreach (var (key, sum) in episode.FeatureSums)'''
s = replace_once(s, old, new, 'binary ML merge')
write(p, s)

# 6) Engine: subscribe to every BMR operation, system logs, raw transport, plus native object-effect tap.
p = 'BossMod/Foretell/ForetellEngine.cs'
s = read(p)
old = '''        SyncReplayWriter();\n        InstallForetellCommand();\n\n        _subscriptions = new('''
new = '''        SyncReplayWriter();\n        InstallForetellCommand();\n        InitializeNativeHooks();\n\n        _subscriptions = new(\n            _ws.Modified.Subscribe(OnWorldOperation),\n            _ws.SystemLogMessage.Subscribe(OnSystemLog),\n            _ws.Network.RawServerIPCReceived.Subscribe(OnRawServerIPC),\n            _ws.Network.RawClientIPCSent.Subscribe(OnRawClientIPC),'''
s = replace_once(s, old, new, 'engine raw subscriptions')
old = '''        _replay?.Dispose();\n        _subscriptions.Dispose();'''
new = '''        _replay?.Dispose();\n        _subscriptions.Dispose();\n        DisposeNativeHooks();'''
s = replace_once(s, old, new, 'native hook dispose')
s = s.replace('_store.Schema = Math.Max(_store.Schema, 4);', '_store.Schema = Math.Max(_store.Schema, 5);')
write(p, s)

# 7) Observer: generic BMR operation companion, dedicated logs and raw transport.
p = 'BossMod/Foretell/ForetellObserver.cs'
s = read(p)
anchor = '''    private void SamplePartyPositions()\n    {'''
insert = '''    private void OnWorldOperation(WorldState.Operation op)\n    {\n        // FrameStart is deliberately represented by the sampled runtime fabric: recording it at render frequency\n        // would multiply replay size/CPU without adding a distinct state transition. This substitution is audited.\n        if (op is WorldState.OpFrameStart)\n        {\n            RegisterCapability("worldop.FrameStart", op.GetType(), "FrameStart", false, true, "represented by sampled runtime.worldState/frame/gauge/camera context");\n            return;\n        }\n        // If BMR packet recording is also enabled, don't duplicate the independent Foretell raw transport tap.\n        if (op is NetworkState.OpServerIPC)\n        {\n            RegisterCapability("worldop.ServerIPC", op.GetType(), "ServerIPC", false, true, "duplicate of Foretell unconditional raw server IPC tap");\n            return;\n        }\n\n        var actorID = ToULong(Member(op, "InstanceID")) ?? ToULong(Member(op, "ActorID")) ?? ToULong(Member(op, "SourceID")) ?? ToULong(Member(op, "CasterID")) ?? 0;\n        var actor = actorID != 0 ? _ws.Actors.Find(actorID) : null;\n        var typeName = op.GetType().FullName ?? op.GetType().Name;\n        var obs = Observation(ObservationKind.WorldOperation, actor, StableHash(typeName), detail: typeName);\n        if (actor == null && actorID != 0)\n        {\n            obs.ActorID = actorID;\n            obs.SourceKind = SourceKind.Unknown;\n        }\n        ProcessRichObservation(obs, op);\n    }\n\n    private void OnSystemLog(WorldState.OpSystemLogMessage op)\n        => ProcessRichObservation(Observation(ObservationKind.SystemLog, primary: op.MessageID), op);\n\n    private void OnRawServerIPC(NetworkState.RawServerIPC packet)\n    {\n        var actor = packet.SourceServerActor != 0 ? _ws.Actors.Find(packet.SourceServerActor) : null;\n        var obs = Observation(ObservationKind.ServerIPC, actor, packet.Opcode, ToUInt(packet.ID) ?? 0, packet.TargetServerActor, detail: packet.ID.ToString());\n        if (actor == null && packet.SourceServerActor != 0)\n        {\n            obs.ActorID = packet.SourceServerActor;\n            obs.SourceKind = SourceKind.Unknown;\n        }\n        ProcessRichObservation(obs, packet);\n    }\n\n    private void OnRawClientIPC(NetworkState.RawClientIPC packet)\n    {\n        var obs = Observation(ObservationKind.ClientIPC, primary: packet.Opcode, detail: "client->server");\n        ProcessRichObservation(obs, packet);\n    }\n\n'''
s = replace_once(s, anchor, insert + anchor, 'observer raw handlers')
write(p, s)

# 8) Native object-effect tap: covers EventObject.PlayAnimation surface used independently by Splatoon-like tooling.
p = 'BossMod/Foretell/ForetellNativeHooks.cs'
write(p, '''using Dalamud.Hooking;\nusing FFXIVEventObject = FFXIVClientStructs.FFXIV.Client.Game.Object.EventObject;\n\nnamespace BossMod.Foretell;\n\npublic sealed partial class ForetellEngine\n{\n    private Hook<FFXIVEventObject.Delegates.PlayAnimation>? _foretellObjectEffectHook;\n\n    private unsafe void InitializeNativeHooks()\n    {\n        try\n        {\n            var address = (nint)FFXIVEventObject.Addresses.PlayAnimation.Value;\n            if (address == 0)\n            {\n                RegisterCapability("native.objectEffect", typeof(FFXIVEventObject), "PlayAnimation", false, true, "FFXIVClientStructs address unavailable");\n                return;\n            }\n            _foretellObjectEffectHook = Service.Hook.HookFromAddress<FFXIVEventObject.Delegates.PlayAnimation>(address, ForetellObjectEffectDetour);\n            _foretellObjectEffectHook.Enable();\n            RegisterCapability("native.objectEffect", typeof(FFXIVEventObject), "PlayAnimation", true, false, "direct passive client hook");\n        }\n        catch (Exception e)\n        {\n            RegisterCapability("native.objectEffect", typeof(FFXIVEventObject), "PlayAnimation", false, true, $"hook unavailable: {e.GetType().Name}");\n            Service.Log($"[Foretell] Native ObjectEffect hook unavailable: {e.Message}");\n        }\n    }\n\n    private void DisposeNativeHooks()\n    {\n        _foretellObjectEffectHook?.Disable();\n        _foretellObjectEffectHook?.Dispose();\n        _foretellObjectEffectHook = null;\n    }\n\n    private unsafe void ForetellObjectEffectDetour(FFXIVEventObject* self, uint entityId, uint actionId, ulong arg4)\n    {\n        _foretellObjectEffectHook!.Original(self, entityId, actionId, arg4);\n        try\n        {\n            if (self == null) return;\n            var instanceID = (ulong)self->EntityId;\n            var actor = _ws.Actors.Find(instanceID);\n            var obs = Observation(ObservationKind.ObjectEffect, actor, entityId, actionId);\n            if (actor == null)\n            {\n                obs.ActorID = instanceID;\n                obs.ActorOID = self->BaseId;\n                obs.SourceKind = SourceKind.EventObject;\n            }\n            obs.Numeric["native.objectEffect.arg4"] = arg4;\n            ProcessObservation(obs);\n        }\n        catch (Exception e)\n        {\n            Service.LogVerbose($"[Foretell] ObjectEffect observation failed: {e.Message}");\n        }\n    }\n}\n''')

# 9) Learning: object effects/system logs can be causal/timeline triggers; raw packets/ops stay correlated feature evidence only.
p = 'BossMod/Foretell/ForetellLearning.cs'
s = read(p)
s = s.replace('ObservationKind.MapEffect or ObservationKind.LegacyMapEffect or ObservationKind.DirectorUpdate or ObservationKind.NpcYell)',
              'ObservationKind.MapEffect or ObservationKind.LegacyMapEffect or ObservationKind.DirectorUpdate or ObservationKind.NpcYell or ObservationKind.ObjectEffect or ObservationKind.SystemLog)')
s = s.replace('or ObservationKind.ActionTimelineEvent or ObservationKind.MapEffect or ObservationKind.LegacyMapEffect or ObservationKind.DirectorUpdate;',
              'or ObservationKind.ActionTimelineEvent or ObservationKind.MapEffect or ObservationKind.LegacyMapEffect or ObservationKind.DirectorUpdate or ObservationKind.ObjectEffect;')
s = s.replace('or ObservationKind.NpcYell or ObservationKind.MapEffect or ObservationKind.LegacyMapEffect or ObservationKind.DirectorUpdate;',
              'or ObservationKind.NpcYell or ObservationKind.MapEffect or ObservationKind.LegacyMapEffect or ObservationKind.DirectorUpdate or ObservationKind.ObjectEffect or ObservationKind.SystemLog;')
# IsEpisodeTrigger has the same tail as timeline in current source; replacement above may cover both occurrences. Ensure explicitly.
if 'ObservationKind.ObjectEffect' not in s or 'ObservationKind.SystemLog' not in s:
    raise RuntimeError('learning signal expansion failed')
write(p, s)

# 10) README contract: source hierarchy and no ACT dependency.
p = 'BossMod/Foretell/README.md'
s = read(p)
s += '''\n### Raw event hierarchy\n\nForetell does not require ACT or IINACT for encounter telemetry. The inherited BMR sync layer already hooks/decodes native FFXIV client and network surfaces (casts, ActionEffect/EffectResult, ActorControl, statuses, map/director events, system logs, timelines, etc.). Foretell additionally consumes every non-frame `WorldState.Operation`, an unconditional lossless server/client IPC tap, and a direct EventObject animation/object-effect surface. Full binary payloads are retained in Foretell replay and every byte contributes to the compressed hashed learner feature space.\n\nBMR/Splatoon encounter-authored answers remain forbidden. Generic BMR primitives and algorithms (geometry, AOE mathematics, arena/pathfinding/constraint utilities, packet decoders and raw sensors) are allowed because they are encounter-agnostic machinery rather than manually authored mechanic knowledge.\n'''
write(p, s)

print('raw-surfaces-v5 patches applied')
