namespace BossMod.Foretell;

internal sealed record GuideIdCandidate(string Boss, string Mechanic, string Kind, string Name, uint[] IDs, string Reason);
internal sealed record GuideIdResolutionInfo(string CatalogHash, string PlanHash, string State, GuideIdCandidate[] Triggers);

internal sealed class GuideIdPlan
{
    private readonly Dictionary<(GuideBoss Boss, string Kind, uint ID), List<GuideEventCandidate>> _events = [];
    private readonly Dictionary<uint, GuideBoss[]> _bosses;
    public GuideDocument Document { get; }
    public GuideIdCatalog Catalog { get; }
    public GuideIdCandidate[] Entries { get; }
    public string Fingerprint { get; }
    public GuideIdResolutionInfo Info { get; }

    public GuideIdPlan(GuideDocument document, GuideIdCatalog catalog)
    {
        Document = document;
        Catalog = catalog;
        Fingerprint = GuideNames.Hash(catalog.Fingerprint + "|" + document.Duty.Key + "|" + string.Join('|', document.Bosses.Select(boss => GuideEventMatching.Scope(document, boss))));
        List<GuideIdCandidate> entries = [];
        List<(uint ID, GuideBoss Boss)> bosses = [];
        var assignments = 0;
        if (document.Bosses.Length > 32 || document.Bosses.Sum(boss => boss.Phases.Sum(phase => phase.Mechanics.Length)) > 2048)
            throw new System.IO.InvalidDataException("Guide ID plan exceeds the boss/mechanic limit.");
        foreach (var boss in document.Bosses)
        {
            var rows = catalog.Find("boss", boss.Name);
            Reserve(rows.Length);
            bosses.AddRange(rows.Select(row => (row.ID, boss)));
            entries.Add(new(boss.Name, "", "boss", boss.Name, rows.Select(row => row.ID).ToArray(), Resolution(rows.Length)));
            foreach (var phase in boss.Phases)
            foreach (var mechanic in phase.Mechanics)
            {
                var before = entries.Count;
                var advice = mechanic.Advice;
                if (advice is { Triggers.Length: > 0 })
                {
                    foreach (var trigger in advice.Triggers) Add(boss, phase, mechanic, trigger.Kind, trigger.Name, trigger);
                }
                else
                {
                    foreach (var kind in new[] { "cast", "status" })
                    {
                        var headings = kind == "cast" ? advice?.Evidence.Select(quote => quote.TrimStart())
                            .Select(quote => quote.IndexOf(':') is > 0 and <= 120 ? quote[..quote.IndexOf(':')] : "") ?? [] : [];
                        var names = new[] { mechanic.Name, advice?.TriggerKind == kind ? advice.TriggerName : "" }.Concat(headings);
                        foreach (var name in names.Where(name => name.Length > 0).Distinct())
                        {
                            var grounded = kind == "cast" ? GuideSynchronization.MatchesCast(mechanic, name)
                                : advice != null && (advice.TriggerKind == "status" && GuideNames.Normalize(advice.TriggerName) == GuideNames.Normalize(name)
                                    || advice.Evidence.Any(quote => GuideRules.Mentions(quote, name)));
                            if (grounded) Add(boss, phase, mechanic, kind, name, null);
                        }
                    }
                }
                if (entries.Count == before) entries.Add(new(boss.Name, mechanic.Name, "manual", mechanic.Name, [], "NoGroundedTrigger"));
            }
        }
        _bosses = bosses.GroupBy(entry => entry.ID).ToDictionary(group => group.Key, group => group.Select(entry => entry.Boss).Distinct().ToArray());
        Entries = entries.ToArray();
        Info = new(catalog.Fingerprint, Fingerprint, "Ready", Entries);

        void Add(GuideBoss boss, GuidePhase phase, GuideMechanic mechanic, string kind, string name, GuideTrigger? trigger)
        {
            if (entries.Count >= 32768) throw new System.IO.InvalidDataException("Guide ID plan exceeds the trigger limit.");
            var rows = catalog.Find(kind, name);
            Reserve(rows.Length);
            var disabled = mechanic.Advice is { Conflict.Length: > 0 } or { ContextOnly: true };
            entries.Add(new(boss.Name, mechanic.Name, kind, name, rows.Select(row => row.ID).ToArray(), disabled ? "ConflictingOrContextOnly" : Resolution(rows.Length)));
            foreach (var row in rows)
            {
                var key = (boss, kind, row.ID);
                if (!_events.TryGetValue(key, out var candidates)) _events[key] = candidates = [];
                candidates.Add(new(phase, mechanic, trigger));
            }
        }

        void Reserve(int count)
        {
            assignments += count;
            if (assignments > 131072) throw new System.IO.InvalidDataException("Guide ID plan exceeds the candidate assignment limit.");
        }
    }

    private static string Resolution(int count) => count == 0 ? "NameNotFound" : count == 1 ? "CandidateID" : "MultipleCandidateIDs";

    public GuideBoss? Boss(uint nameID) => _bosses.TryGetValue(nameID, out var bosses) && bosses.Length == 1 ? bosses[0] : null;

    public bool OwnsSource(GuideBoss boss, Actor source, Actor? owner)
    {
        var identity = source.OwnerID == 0 ? source : owner;
        return identity != null && ReferenceEquals(Boss(identity.NameID), boss)
            && ForetellEngine.GuideEventSource(source, owner, identity.NameID);
    }

    public GuideEventResolution Resolve(GuideBoss boss, GuidePhaseDefinition? phase, string kind, uint id,
        ulong targetID, ulong playerID, bool partyTarget, int stacks)
    {
        if (!Document.Bosses.Contains(boss)) return new(null, null, null, "ForeignGuideBoss", []);
        if (!_events.TryGetValue((boss, kind, id), out var candidates)) return new(null, null, null, "NoCandidateForObservedID", []);
        var result = GuideEventMatching.Choose(boss, phase, kind, candidates, targetID, playerID, partyTarget, stacks);
        return result.Mechanic == null ? result : result with { Reason = result.Reason == "MatchedPhaseTransition" ? "MatchedGameIDPhaseTransition" : "MatchedGameID" };
    }

    public bool AllowsInstant(GuideBoss boss, uint id) => _events.TryGetValue((boss, "cast", id), out var candidates)
        && candidates.All(candidate => candidate.Trigger == null || candidate.Trigger is { Target: "any", MinimumStacks: 0 });

    public uint[] Candidates(GuideBoss boss, string kind, GuideMechanic? mechanic)
        => mechanic == null ? [] : Entries.Where(entry => entry.Boss == boss.Name && entry.Mechanic == mechanic.Name && entry.Kind == kind)
            .SelectMany(entry => entry.IDs).Distinct().Order().ToArray();
}
