namespace BossMod.Foretell;

public sealed record DecisionActorSnapshot(ulong ID, uint OID, ushort Type, uint Job, int Level,
    float X, float Y, float Z, float Rotation, float Hitbox, uint HP, uint MaxHP,
    bool Targetable, bool Ally, bool Dead, bool Combat, bool Aggro, ulong Target);

public sealed class DecisionContextSnapshot
{
    public bool Learning { get; set; } = true;
    public bool ML { get; set; } = true;
    public float VisualThreshold { get; set; } = 75;
    public float WarningThreshold { get; set; } = 95;
    public float StrictThreshold { get; set; } = 99;
    public long ID { get; set; }
    public DateTime At { get; set; }
    public ushort Duty { get; set; }
    public bool InCombat { get; set; }
    public bool Complete { get; set; }
    public long OutcomeGap { get; set; }
    public ulong BossID { get; set; }
    public ulong[] Party { get; set; } = [];
    public DecisionActorSnapshot[] Actors { get; set; } = [];
}

public sealed record ForetellReplayEvaluation(ReplayReport Report, ForetellStore Knowledge);
