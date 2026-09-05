using System.Numerics;
using System.Reflection;
using BossMod;
using BossMod.Foretell;

internal static class CastRecoveryTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static void Run()
    {
        var at = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
        Actor Source(int i) => new((ulong)(100 + i), 900, 0, 0, "", 0, ActorType.Enemy, Class.None, 50,
            new(-15, 0, -16 + 8 * i, MathF.PI / 2), resolveGameMetadata: false)
            { CastInfo = new() { Action = new(ActionType.Spell, 901), TotalTime = 3, ElapsedTime = .4f, Rotation = new(MathF.PI / 2) } };
        var sources = Enumerable.Range(0, 5).Select(Source).ToArray();
        var accepted = new Dictionary<ulong, ActorCastInfo>();
        var recovered = new List<ForetellObservation>();
        var context = new DecisionContextSnapshot
        {
            ID = 1, At = at, Complete = true, InCombat = true, Duty = 1, Party = [1],
            Actors = [new(1, 0, 0x104, 19, 50, 0, 0, 0, 0, 1, 100, 100, true, false, false, true, false, 100),
                .. sources.Select(a => new DecisionActorSnapshot(a.InstanceID, a.OID, (ushort)a.Type, 0, 50,
                    a.PosRot.X, 0, a.PosRot.Z, a.PosRot.W, 1, 100, 100, true, false, false, true, true, 1))]
        };
        var frame = 0; var remainingBudget = 0;
        bool Observe(Actor actor)
        {
            --remainingBudget;
            var cast = actor.CastInfo!;
            recovered.Add(new()
            {
                Sequence = recovered.Count + 1, At = at.AddSeconds(frame / 60f), Kind = ObservationKind.CastStart,
                TerritoryID = 1, ActorID = actor.InstanceID, ActorOID = actor.OID, PrimaryID = cast.Action.ID,
                SourceKind = SourceKind.Enemy, ContextID = 1, Context = context, Value1 = cast.NPCRemainingTime,
                X = actor.PosRot.X, Z = actor.PosRot.Z, Rotation = cast.Rotation.Rad,
                Prior = new(901, GeometryKind.Rectangle, MechanicKind.GroundAOE, 40, 2, .94f, 4, 40, 4, false, 0, "", 0, "client rectangle")
            });
            return true;
        }
        // First callback burst was missed entirely. The next three frames can afford only two casts each.
        for (frame = 0; frame < 3; ++frame)
        {
            remainingBudget = 2;
            ForetellCastRecovery.Recover(sources, accepted, () => remainingBudget > 0, Observe);
            foreach (var actor in sources) actor.CastInfo!.ElapsedTime += 1f / 60;
        }
        Check(recovered.Count == 5 && accepted.Count == 5, "Cast recovery lost simultaneous sources under a per-frame budget");
        Check(recovered.Last().Value1 < recovered.First().Value1, "Recovered cast was backdated to its full original duration");
        remainingBudget = 20;
        ForetellCastRecovery.Recover(sources, accepted, () => remainingBudget > 0, Observe);
        Check(recovered.Count == 5, "An already accepted ongoing cast was predicted twice");
        var result = ForetellEngine.EvaluateRecordedObservations(recovered);
        var issued = result.Knowledge.DecisionAudit.Where(d => d.Stage == DecisionAuditStage.Proposed && d.TriggerID == 901
            && d.Geometry == GeometryKind.Rectangle && d.DisplayEligible).GroupBy(d => d.PredictionID).Select(g => g.First()).ToArray();
        Check(issued.Length == 5 && issued.Select(d => d.OriginZ).Distinct().Count() == 5,
            "Real engine collapsed five visible line casts into nearby footprints");
        Check(issued.All(d => Math.Abs((d.Activation.ToUniversalTime() - at).TotalSeconds - 2.9) < .002), "Recovery changed the known impact time: " + string.Join(", ", issued.Select(d => (d.Activation.ToUniversalTime() - at).TotalSeconds)) );
        sources[0].CastInfo = sources[0].CastInfo!.Clone();
        ForetellCastRecovery.Recover(sources, accepted, () => remainingBudget > 0, Observe);
        Check(recovered.Count == 6, "A later cast of the same action on the same actor was suppressed");
        var rejected = Source(20); var attempts = 0;
        ForetellCastRecovery.Recover([rejected], accepted, () => true, _ => { ++attempts; return false; });
        ForetellCastRecovery.Recover([rejected], accepted, () => true, _ => { ++attempts; return true; });
        Check(attempts == 2 && accepted.ContainsKey(rejected.InstanceID), "A failed retry was marked accepted prematurely");
        var ended = Source(21); ended.CastInfo!.EventHappened = true;
        var almostEnded = Source(22); almostEnded.CastInfo!.ElapsedTime = 3.2f;
        var ally = new Actor(200, 0, 0, 0, "", 0, ActorType.Player, Class.None, 50, Vector4.Zero, resolveGameMetadata: false) { CastInfo = Source(23).CastInfo };
        ForetellCastRecovery.Recover([ended, almostEnded, ally], accepted, () => true, _ => throw new InvalidOperationException("Ended or player cast recovered as enemy danger"));
        SheetStorageIsNotGameplayEvidence();
        ActionPriorTests.Run(context, recovered[0]);
        Console.WriteLine("Missed cast recovery, simultaneous real-engine lines and sheet storage filtering tests passed.");
    }

    private static void SheetStorageIsNotGameplayEvidence()
    {
        var action = Assembly.Load("Lumina.Excel").GetType("Lumina.Excel.Sheets.Action", throwOnError: true)!;
        var page = action.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(p => p.PropertyType.Name.Contains("ExcelPage"));
        Check(page != null && ForetellFabricPolicy.IsSheetStorageMember(action, page.PropertyType, page.Name),
            "Actual Lumina action row exposes its backing Excel page to recursive ingestion");
        var range = action.GetProperty("EffectRange")!;
        Check(!ForetellFabricPolicy.IsSheetStorageMember(action, range.PropertyType, range.Name), "Typed gameplay range was excluded with storage");
        var reference = action.GetProperties().First(p => p.PropertyType.Name.StartsWith("RowRef") && p.PropertyType.IsGenericType).PropertyType;
        var value = reference.GetProperty("Value")!;
        Check(ForetellFabricPolicy.IsSheetStorageMember(reference, value.PropertyType, value.Name)
            && !ForetellFabricPolicy.IsSheetStorageMember(reference, typeof(uint), "RowId"), "Row reference expanded an entire referenced sheet");
        foreach (var field in page!.PropertyType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.FieldType == typeof(byte[]) || f.FieldType == typeof(ReadOnlyMemory<byte>)))
            Check(ForetellFabricPolicy.IsSheetStorageMember(page.PropertyType, field.FieldType, field.Name), "Lumina storage bytes became per-actor evidence");
        Check(!ForetellFabricPolicy.IsSheetStorageMember(typeof(ForetellObservation), typeof(byte[]), "Data"), "Non-sheet raw gameplay payload was excluded");
    }
}
