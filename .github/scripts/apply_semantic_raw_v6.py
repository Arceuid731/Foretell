from pathlib import Path
ROOT=Path('.')

def read(p): return (ROOT/p).read_text(encoding='utf-8-sig')
def write(p,s): (ROOT/p).write_text(s,encoding='utf-8')
def rep(s,a,b,label):
    if a not in s: raise RuntimeError('missing anchor '+label)
    return s.replace(a,b,1)

# NetworkState: raw ActorControl transient stream
p='BossMod/Data/NetworkState.cs'; s=read(p)
a='''    public Event<RawServerIPC> RawServerIPCReceived = new();\n    public Event<RawClientIPC> RawClientIPCSent = new();\n'''
b='''    public readonly struct RawActorControl(uint sourceID, uint command, uint p1, uint p2, uint p3, uint p4, uint p5, uint p6, uint p7, uint p8, ulong targetID, byte replaying)\n    {\n        public readonly uint SourceID = sourceID;\n        public readonly uint Command = command;\n        public readonly uint P1 = p1; public readonly uint P2 = p2; public readonly uint P3 = p3; public readonly uint P4 = p4;\n        public readonly uint P5 = p5; public readonly uint P6 = p6; public readonly uint P7 = p7; public readonly uint P8 = p8;\n        public readonly ulong TargetID = targetID;\n        public readonly byte Replaying = replaying;\n    }\n\n    public Event<RawServerIPC> RawServerIPCReceived = new();\n    public Event<RawClientIPC> RawClientIPCSent = new();\n    public Event<RawActorControl> RawActorControlReceived = new();\n'''
s=rep(s,a,b,'NetworkState actorcontrol'); write(p,s)

# WorldStateGameSync: queue/drain every ActorControl before semantic switch
p='BossMod/Framework/WorldStateGameSync.cs'; s=read(p)
a='''    private readonly System.Collections.Concurrent.ConcurrentQueue<NetworkState.RawServerIPC> _foretellRawServerPackets = new();\n    private readonly System.Collections.Concurrent.ConcurrentQueue<NetworkState.RawClientIPC> _foretellRawClientPackets = new();\n'''
b=a+'''    private readonly System.Collections.Concurrent.ConcurrentQueue<NetworkState.RawActorControl> _foretellRawActorControls = new();\n'''
s=rep(s,a,b,'actorcontrol queue')
a='''        while (_foretellRawClientPackets.TryDequeue(out var rawClient))\n            _ws.Network.RawClientIPCSent.Fire(rawClient);\n\n        _playerEnmity.Clear();'''
b='''        while (_foretellRawClientPackets.TryDequeue(out var rawClient))\n            _ws.Network.RawClientIPCSent.Fire(rawClient);\n        while (_foretellRawActorControls.TryDequeue(out var rawControl))\n            _ws.Network.RawActorControlReceived.Fire(rawControl);\n\n        _playerEnmity.Clear();'''
s=rep(s,a,b,'actorcontrol drain')
a='''    private void ProcessPacketActorControlDetour(uint actorID, uint category, uint p1, uint p2, uint p3, uint p4, uint p5, uint p6, uint p7, uint p8, ulong targetID, byte replaying)\n    {\n        _processPacketActorControlHook.Original(actorID, category, p1, p2, p3, p4, p5, p6, p7, p8, targetID, replaying);\n        switch ((Network.ServerIPC.ActorControlCategory)category)'''
b='''    private void ProcessPacketActorControlDetour(uint actorID, uint category, uint p1, uint p2, uint p3, uint p4, uint p5, uint p6, uint p7, uint p8, ulong targetID, byte replaying)\n    {\n        _processPacketActorControlHook.Original(actorID, category, p1, p2, p3, p4, p5, p6, p7, p8, targetID, replaying);\n        _foretellRawActorControls.Enqueue(new(actorID, category, p1, p2, p3, p4, p5, p6, p7, p8, targetID, replaying));\n        switch ((Network.ServerIPC.ActorControlCategory)category)'''
s=rep(s,a,b,'actorcontrol enqueue'); write(p,s)

# Model: explicit raw ActorControl kind
p='BossMod/Foretell/ForetellModel.cs'; s=read(p)
s=rep(s,'    WorldOperation, ServerIPC, ClientIPC,\n','    WorldOperation, ServerIPC, ClientIPC, ActorControlRaw,\n','actorcontrol kind'); write(p,s)

# Engine subscription
p='BossMod/Foretell/ForetellEngine.cs'; s=read(p)
a='''            _ws.Network.RawServerIPCReceived.Subscribe(OnRawServerIPC),\n            _ws.Network.RawClientIPCSent.Subscribe(OnRawClientIPC),\n'''
b=a+'''            _ws.Network.RawActorControlReceived.Subscribe(OnRawActorControl),\n'''
s=rep(s,a,b,'actorcontrol subscription'); write(p,s)

# Observer: typed lossless ActionEffect extraction, all system-log args, raw ActorControl handler
p='BossMod/Foretell/ForetellObserver.cs'; s=read(p)
a='''    private void OnCastEvent(Actor actor, ActorCastEvent ev)\n    {\n        var action = ReadActionID(ev);\n        if (action == 0) return;\n        var targets = ExtractTargetIDs(ev);\n        ProcessRichObservation(Observation(ObservationKind.ActionResolved, actor, action, value1: targets.Count), ev);\n        foreach (var target in targets)\n            ProcessObservation(Observation(ObservationKind.AffectedTarget, actor, action, target: target));\n    }'''
b='''    private void OnCastEvent(Actor actor, ActorCastEvent ev)\n    {\n        var action = ev.Action.ID;\n        if (action == 0) return;\n        var resolved = Observation(ObservationKind.ActionResolved, actor, action, value1: ev.Targets.Count);\n        resolved.Numeric["action.globalSequence"] = ev.GlobalSequence;\n        resolved.Numeric["action.sourceSequence"] = ev.SourceSequence;\n        resolved.Numeric["action.maxTargets"] = ev.MaxTargets;\n        resolved.Numeric["action.animationLock"] = ev.AnimationLockTime;\n        resolved.Numeric["action.targetY"] = ev.TargetPos.Y;\n        ProcessRichObservation(resolved, ev);\n\n        var seen = new HashSet<ulong>();\n        foreach (var target in ev.Targets)\n        {\n            seen.Add(target.ID);\n            var affected = Observation(ObservationKind.AffectedTarget, actor, action, target: target.ID);\n            var effects = target.Effects.ValidEffects();\n            affected.Numeric["actionEffect.count"] = effects.Length;\n            for (var i = 0; i < effects.Length; ++i)\n            {\n                ref readonly var effect = ref effects[i];\n                var prefix = $"actionEffect.{i}";\n                affected.Numeric[$"{prefix}.type"] = (byte)effect.Type;\n                affected.Numeric[$"{prefix}.param0"] = effect.Param0;\n                affected.Numeric[$"{prefix}.param1"] = effect.Param1;\n                affected.Numeric[$"{prefix}.param2"] = effect.Param2;\n                affected.Numeric[$"{prefix}.param3"] = effect.Param3;\n                affected.Numeric[$"{prefix}.param4"] = effect.Param4;\n                affected.Numeric[$"{prefix}.value"] = effect.Value;\n                affected.Numeric[$"{prefix}.fromTarget"] = effect.FromTarget ? 1 : 0;\n                affected.Numeric[$"{prefix}.atSource"] = effect.AtSource ? 1 : 0;\n                affected.Numeric[$"{prefix}.damageType"] = (int)effect.DamageType;\n                affected.Numeric[$"{prefix}.damageElement"] = (int)effect.DamageElement;\n                affected.Numeric[$"{prefix}.damageHealValue"] = effect.DamageHealValue;\n                affected.Text[$"{prefix}.typeName"] = effect.Type.ToString();\n                affected.Binary[$"{prefix}.raw"] =\n                [\n                    (byte)effect.Type, effect.Param0, effect.Param1, effect.Param2, effect.Param3, effect.Param4,\n                    (byte)(effect.Value & 0xFF), (byte)(effect.Value >> 8)\n                ];\n            }\n            ProcessObservation(affected);\n        }\n\n        if (ev.MainTargetID != 0 && !seen.Contains(ev.MainTargetID))\n            ProcessObservation(Observation(ObservationKind.AffectedTarget, actor, action, target: ev.MainTargetID, detail: "main-target-only"));\n    }'''
s=rep(s,a,b,'typed action effects')
a='''    private void OnSystemLog(WorldState.OpSystemLogMessage op)\n        => ProcessRichObservation(Observation(ObservationKind.SystemLog, primary: op.MessageID), op);\n'''
b='''    private void OnSystemLog(WorldState.OpSystemLogMessage op)\n    {\n        var obs = Observation(ObservationKind.SystemLog, primary: op.MessageID);\n        obs.Numeric["systemLog.argCount"] = op.Args.Length;\n        for (var i = 0; i < op.Args.Length; ++i)\n            obs.Numeric[$"systemLog.arg.{i}"] = op.Args[i];\n        ProcessRichObservation(obs, op);\n    }\n'''
s=rep(s,a,b,'all system log args')
a='''    private void OnRawClientIPC(NetworkState.RawClientIPC packet)\n    {\n        var obs = Observation(ObservationKind.ClientIPC, primary: packet.Opcode, detail: "client->server");\n        ProcessRichObservation(obs, packet);\n    }\n\n'''
b=a+'''    private void OnRawActorControl(NetworkState.RawActorControl control)\n    {\n        var actor = control.SourceID != 0 ? _ws.Actors.Find(control.SourceID) : null;\n        var obs = Observation(ObservationKind.ActorControlRaw, actor, control.Command, target: control.TargetID, flag: control.Replaying != 0);\n        if (actor == null && control.SourceID != 0)\n        {\n            obs.ActorID = control.SourceID;\n            obs.SourceKind = SourceKind.Unknown;\n        }\n        obs.Numeric["actorControl.p1"] = control.P1; obs.Numeric["actorControl.p2"] = control.P2;\n        obs.Numeric["actorControl.p3"] = control.P3; obs.Numeric["actorControl.p4"] = control.P4;\n        obs.Numeric["actorControl.p5"] = control.P5; obs.Numeric["actorControl.p6"] = control.P6;\n        obs.Numeric["actorControl.p7"] = control.P7; obs.Numeric["actorControl.p8"] = control.P8;\n        ProcessRichObservation(obs, control);\n    }\n\n'''
s=rep(s,a,b,'raw actorcontrol observer')
write(p,s)

# README detail
p='BossMod/Foretell/README.md'; s=read(p)
s += '''\nActionEffect handling is typed rather than reflection-only: all valid target effects retain Type, Param0..4, Value, derived damage/element fields and the exact original 8-byte effect record. Raw ActorControl retains command, p1..p8, target and replay flag; SystemLog retains every argument without the generic collection sampling cap.\n'''
write(p,s)
print('semantic raw v6 applied')
