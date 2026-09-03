namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    // Complete managed state snapshot at a low cadence. WorldState operations remain the primary delta stream;
    // this snapshot supplies initial state, continuous timers and a lossless safety net without reflection.
    private void StoreTypedWorldSnapshot(ForetellObservation obs)
    {
        StoreConditionState(obs);
        StoreWaymarks(obs);
        StoreParty(obs);
        StoreClient(obs);
        if (_ws.DeepDungeon.DungeonId != 0)
            StoreDeepDungeon(obs);
        else
            StoreFabric(obs, "runtime.deepDungeon.id", 0);
    }

    // Large player-owned arrays are captured once on startup/territory change. Their subsequent WorldState
    // operations remain lossless, but they no longer get re-enumerated on a timer while the player is fighting.
    private void StoreColdTypedWorldSnapshot(ForetellObservation obs)
    {
        var client = _ws.Client;
        for (var i = 0; i < client.Cooldowns.Length; ++i)
        {
            StoreFabric(obs, $"runtime.client.cooldowns[{i}].elapsed", client.Cooldowns[i].Elapsed);
            StoreFabric(obs, $"runtime.client.cooldowns[{i}].total", client.Cooldowns[i].Total);
        }
        StoreArray(obs, "runtime.client.bozjaHolster", client.BozjaHolster);
        StoreArray(obs, "runtime.client.blueMageSpells", client.BlueMageSpells);
        StoreArray(obs, "runtime.client.classJobLevels", client.ClassJobLevels);
        StoreArray(obs, "runtime.client.contentKeyValue", client.ContentKeyValueData);
        StoreArray(obs, "runtime.client.procTimers", client.ProcTimers);
    }

    private void StoreWaymarks(ForetellObservation obs)
    {
        for (var i = 0; i < (int)Waymark.Count; ++i)
        {
            var pos = _ws.Waymarks[(Waymark)i];
            StoreFabric(obs, $"runtime.waymarks.field[{i}].active", pos != null);
            if (pos is not Vector3 p)
                continue;
            StoreFabric(obs, $"runtime.waymarks.field[{i}].x", p.X);
            StoreFabric(obs, $"runtime.waymarks.field[{i}].y", p.Y);
            StoreFabric(obs, $"runtime.waymarks.field[{i}].z", p.Z);
        }
        for (var i = 0; i < (int)Sign.Count; ++i)
            StoreFabric(obs, $"runtime.waymarks.sign[{i}].target", _ws.Waymarks[(Sign)i]);
    }

    private void StoreParty(ForetellObservation obs)
    {
        StoreFabric(obs, "runtime.party.capacity", _ws.Party.Members.Length);
        StoreFabric(obs, "runtime.party.limitBreak.current", _ws.Party.LimitBreakCur);
        StoreFabric(obs, "runtime.party.limitBreak.maximum", _ws.Party.LimitBreakMax);
        for (var i = 0; i < _ws.Party.Members.Length; ++i)
        {
            ref readonly var member = ref _ws.Party.Members[i];
            StoreFabric(obs, $"runtime.party[{i}].contentId", member.ContentId);
            StoreFabric(obs, $"runtime.party[{i}].instanceId", member.InstanceId);
            StoreFabric(obs, $"runtime.party[{i}].inCutscene", member.InCutscene);
        }
    }

    private void StoreClient(ForetellObservation obs)
    {
        var client = _ws.Client;
        StoreFabric(obs, "runtime.client.countdown.active", client.CountdownRemaining != null);
        StoreFabric(obs, "runtime.client.countdown.remaining", client.CountdownRemaining ?? 0);
        StoreFabric(obs, "runtime.client.cameraAzimuth", client.CameraAzimuth.Rad);
        StoreFabric(obs, "runtime.client.gauge.low", client.GaugePayload.Low);
        StoreFabric(obs, "runtime.client.gauge.high", client.GaugePayload.High);
        StoreFabric(obs, "runtime.client.animationLock", client.AnimationLock);
        StoreFabric(obs, "runtime.client.combo.action", client.ComboState.Action);
        StoreFabric(obs, "runtime.client.combo.remaining", client.ComboState.Remaining);
        StoreFabric(obs, "runtime.client.stats.skillSpeed", client.PlayerStats.SkillSpeed);
        StoreFabric(obs, "runtime.client.stats.spellSpeed", client.PlayerStats.SpellSpeed);
        StoreFabric(obs, "runtime.client.stats.haste", client.PlayerStats.Haste);
        StoreFabric(obs, "runtime.client.moveSpeed", client.MoveSpeed);
        StoreFabric(obs, "runtime.client.flying", client.Flying);

        StoreFabric(obs, "runtime.client.fate.id", client.ActiveFate.ID);
        StoreFabric(obs, "runtime.client.fate.center.x", client.ActiveFate.Center.X);
        StoreFabric(obs, "runtime.client.fate.center.y", client.ActiveFate.Center.Y);
        StoreFabric(obs, "runtime.client.fate.center.z", client.ActiveFate.Center.Z);
        StoreFabric(obs, "runtime.client.fate.radius", client.ActiveFate.Radius);
        StoreFabric(obs, "runtime.client.fate.progress", client.ActiveFate.Progress);
        StoreFabric(obs, "runtime.client.fate.handInCount", client.ActiveFate.HandInCount);
        StoreFabric(obs, "runtime.client.fate.objectiveNpc", client.ActiveFate.ObjectiveNpc);
        StoreFabric(obs, "runtime.client.pet.instanceId", client.ActivePet.InstanceID);
        StoreFabric(obs, "runtime.client.pet.order", client.ActivePet.Order);
        StoreFabric(obs, "runtime.client.pet.stance", client.ActivePet.Stance);
        StoreFabric(obs, "runtime.client.companion.instanceId", client.ActiveCompanion.InstanceID);
        StoreFabric(obs, "runtime.client.companion.stance", client.ActiveCompanion.Stance);
        StoreFabric(obs, "runtime.client.companion.timeLeft", client.ActiveCompanion.TimeLeft);
        StoreFabric(obs, "runtime.client.companion.stabled", client.ActiveCompanion.Stabled);
        StoreFabric(obs, "runtime.client.focusTargetId", client.FocusTargetId);
        StoreFabric(obs, "runtime.client.forcedMovementDirection", client.ForcedMovementDirection.Rad);

        StoreFabric(obs, "runtime.client.cooldowns.capacity", client.Cooldowns.Length);
        // Full player cooldown, inventory and progression collections are not encounter evidence. Their previous
        // one-hertz enumeration caused allocation/GC spikes in open world; action/status deltas retain the useful
        // combat signal without sweeping unrelated account state.
        for (var i = 0; i < client.DutyActions.Length; ++i)
        {
            StoreFabric(obs, $"runtime.client.dutyActions[{i}].type", client.DutyActions[i].Action.Type);
            StoreFabric(obs, $"runtime.client.dutyActions[{i}].id", client.DutyActions[i].Action.ID);
            StoreFabric(obs, $"runtime.client.dutyActions[{i}].charges", client.DutyActions[i].CurCharges);
            StoreFabric(obs, $"runtime.client.dutyActions[{i}].maxCharges", client.DutyActions[i].MaxCharges);
        }
        StoreFabric(obs, "runtime.client.hate.primary", client.CurrentTargetHate.InstanceID);
        for (var i = 0; i < client.CurrentTargetHate.Targets.Length; ++i)
        {
            StoreFabric(obs, $"runtime.client.hate[{i}].instanceId", client.CurrentTargetHate.Targets[i].InstanceID);
            StoreFabric(obs, $"runtime.client.hate[{i}].enmity", client.CurrentTargetHate.Targets[i].Enmity);
        }
    }

    private void StoreDeepDungeon(ForetellObservation obs)
    {
        var dd = _ws.DeepDungeon;
        StoreFabric(obs, "runtime.deepDungeon.id", dd.DungeonId);
        StoreFabric(obs, "runtime.deepDungeon.progress.floor", dd.Progress.Floor);
        StoreFabric(obs, "runtime.deepDungeon.progress.tileset", dd.Progress.Tileset);
        StoreFabric(obs, "runtime.deepDungeon.progress.weaponLevel", dd.Progress.WeaponLevel);
        StoreFabric(obs, "runtime.deepDungeon.progress.armorLevel", dd.Progress.ArmorLevel);
        StoreFabric(obs, "runtime.deepDungeon.progress.syncedGearLevel", dd.Progress.SyncedGearLevel);
        StoreFabric(obs, "runtime.deepDungeon.progress.hoardCount", dd.Progress.HoardCount);
        StoreFabric(obs, "runtime.deepDungeon.progress.return", dd.Progress.ReturnProgress);
        StoreFabric(obs, "runtime.deepDungeon.progress.passage", dd.Progress.PassageProgress);
        StoreFabric(obs, "runtime.deepDungeon.progress.hoardCurrentFloor", dd.Progress.HoardCurrentFloor);
        StoreArray(obs, "runtime.deepDungeon.rooms", dd.Rooms);
        for (var i = 0; i < dd.Party.Length; ++i)
        {
            StoreFabric(obs, $"runtime.deepDungeon.party[{i}].entityId", dd.Party[i].EntityId);
            StoreFabric(obs, $"runtime.deepDungeon.party[{i}].room", dd.Party[i].Room);
        }
        for (var i = 0; i < dd.Pomanders.Length; ++i)
        {
            StoreFabric(obs, $"runtime.deepDungeon.pomanders[{i}].count", dd.Pomanders[i].Count);
            StoreFabric(obs, $"runtime.deepDungeon.pomanders[{i}].flags", dd.Pomanders[i].Flags);
        }
        for (var i = 0; i < dd.Chests.Length; ++i)
        {
            StoreFabric(obs, $"runtime.deepDungeon.chests[{i}].type", dd.Chests[i].Type);
            StoreFabric(obs, $"runtime.deepDungeon.chests[{i}].room", dd.Chests[i].Room);
        }
        StoreArray(obs, "runtime.deepDungeon.magicite", dd.Magicite);
    }

    private void StoreArray<T>(ForetellObservation obs, string prefix, T[] values) where T : notnull
    {
        StoreFabric(obs, $"{prefix}.count", values.Length);
        for (var i = 0; i < values.Length; ++i)
            StoreFabric(obs, $"{prefix}[{i}]", values[i]);
    }
}
