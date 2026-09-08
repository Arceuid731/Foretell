using BossMod.Foretell;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class GuideIdReplayReview
{
    private const int ArtifactLimit = 24 * 1024 * 1024;
    private const int TotalArtifactLimit = 64 * 1024 * 1024;
    private const int VersionLimit = 32;
    private const int ObservationLimit = 500000;
    private const int GroupLimit = 8192;
    private const int ComparisonLimit = 32768;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private sealed record Historical(string File, string SourceFile, string Sha256, DateTime At, string Kind, GuideDocument Document);
    private sealed record ObservedKey(uint Territory, string Kind, uint ID, uint NameID, string ActorName, string ActorType);
    private sealed class Observed(ObservedKey key, DateTime at)
    {
        public ObservedKey Key { get; } = key;
        public long Count { get; set; } = 1;
        public DateTime First { get; set; } = at;
        public DateTime Last { get; set; } = at;
    }

    public static void Run(string zip, string gameDirectory, string output)
    {
        var outputFile = Path.Combine(Path.GetFullPath(output), "guide-id-review.json");
        if (string.Equals(Path.GetFullPath(zip), outputFile, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Report output would overwrite the input archive.");
        var warnings = new List<string>();
        using var archiveFile = File.OpenRead(zip);
        if (archiveFile.Length > 512L * 1024 * 1024) throw new InvalidDataException("ZIP exceeds the 512 MiB limit.");
        var archiveHash = Convert.ToHexString(SHA256.HashData(archiveFile));
        archiveFile.Position = 0;
        using var archive = new ZipArchive(archiveFile, ZipArchiveMode.Read);
        var historical = ReadHistorical(archive, warnings);
        if (historical.Count == 0) throw new InvalidDataException("ZIP contains no usable historical guide source; installed guide cache is never substituted.");
        using var gameData = new Lumina.GameData(gameDirectory);
        var catalog = GuideIdCatalog.FromGameData(gameData, CancellationToken.None);
        var reader = new ForetellRecordingReader(zip);
        var observed = new Dictionary<ObservedKey, Observed>();
        long examined = 0;
        long omittedGroups = 0;
        var exhausted = true;
        foreach (var observation in reader.Read())
        {
            if (++examined > ObservationLimit) { exhausted = false; warnings.Add("Observation work limit reached; recording completeness was not assessed."); break; }
            var kind = observation.Kind == ObservationKind.CastStart ? "cast" : observation.Kind == ObservationKind.StatusGain ? "status" : "";
            if (kind.Length == 0 || observation.SourceKind is SourceKind.Player or SourceKind.Pet) continue;
            var numericName = observation.Numeric.GetValueOrDefault("actor.nameID");
            var nameID = double.IsFinite(numericName) && numericName >= 1 && numericName <= uint.MaxValue && numericName == Math.Truncate(numericName) ? (uint)numericName : 0;
            var key = new ObservedKey(observation.TerritoryID, kind, observation.PrimaryID, nameID,
                Short(observation.Text.GetValueOrDefault("actor.name", "")), Short(observation.Text.GetValueOrDefault("actor.type.name", "")));
            if (observed.TryGetValue(key, out var group))
            {
                ++group.Count;
                if (observation.At < group.First) group.First = observation.At;
                if (observation.At > group.Last) group.Last = observation.At;
            }
            else if (observed.Count < GroupLimit) observed.Add(key, new(key, observation.At));
            else ++omittedGroups;
        }
        if (omittedGroups != 0) warnings.Add($"Distinct observation group limit reached; {omittedGroups} records omitted from aggregation.");
        var comparisons = new List<object>();
        var guides = new List<object>();
        long omittedComparisons = 0;
        foreach (var version in historical)
        {
            var plan = new GuideIdPlan(version.Document, catalog);
            var byID = plan.Entries.SelectMany(entry => entry.IDs.Select(identifier => (entry, identifier)))
                .ToLookup(pair => (pair.entry.Kind, pair.identifier), pair => pair.entry);
            guides.Add(new
            {
                version.File, version.SourceFile, version.Sha256, version.At, version.Kind,
                version.Document.Duty, version.Document.SourceHash, version.Document.Revision, version.Document.ModelRevision,
                idCandidatesOmitted = Math.Max(0, plan.Entries.Length - 4096),
                sourceUrl = Short(version.Document.SourceUrl),
                sources = version.Document.Sources.Take(16).Select(source => new { source.Provider, source.Url, text = Short(source.Text) }),
                evidence = version.Document.Bosses.SelectMany(boss => boss.Phases.SelectMany(phase => phase.Mechanics.Select(mechanic => new
                {
                    boss = Short(boss.Name), phase = Short(phase.Name), mechanic = Short(mechanic.Name), text = Short(mechanic.Text),
                    quotes = mechanic.Advice?.Evidence.Take(8).Select(Short).ToArray() ?? [],
                    triggers = mechanic.Advice?.Triggers.Take(16).Select(trigger => new
                    { trigger.Kind, name = Short(trigger.Name), cue = Short(trigger.Cue), trigger.Target, trigger.MinimumStacks, evidence = trigger.Evidence.Take(8).Select(Short).ToArray() }).ToArray()
                }))),
                idCandidates = plan.Entries.Take(4096).Select(entry => new
                { boss = Short(Convert.ToString(entry.Boss) ?? ""), mechanic = Short(Convert.ToString(entry.Mechanic) ?? ""), entry.Kind, name = Short(entry.Name), entry.IDs, entry.Reason })
            });
            foreach (var group in observed.Values.Where(group => group.Key.Territory == version.Document.Duty.TerritoryID))
            {
                if (comparisons.Count >= ComparisonLimit) { ++omittedComparisons; continue; }
                var key = group.Key;
                var row = catalog.Row(key.Kind, key.ID);
                var npc = catalog.Row("boss", key.NameID);
                var oldBossName = npc?.EnglishName ?? key.ActorName;
                var oldBosses = version.Document.Bosses.Where(boss => GuideNames.Boss(boss.Name) == GuideNames.Boss(oldBossName)).ToArray();
                var boss = key.ActorType == "Helper" ? null : plan.Boss(key.NameID);
                var oldResult = oldBosses.Length == 1 && key.ActorType != "Helper"
                    ? GuideEventMatching.Resolve(oldBosses[0], null, key.Kind, row?.EnglishName ?? "", 0, 0, false, 0) : null;
                var newResult = boss == null ? null : plan.Resolve(boss, null, key.Kind, key.ID, 0, 0, false, 0);
                var candidates = byID[(key.Kind, key.ID)].Take(32).Select(entry => new
                { boss = Short(Convert.ToString(entry.Boss) ?? ""), mechanic = Short(Convert.ToString(entry.Mechanic) ?? ""), name = Short(entry.Name), entry.Reason }).ToArray();
                comparisons.Add(new
                {
                    guideFile = version.File, key.Territory, key.Kind, key.ID, key.NameID, key.ActorName, key.ActorType,
                    group.Count, group.First, group.Last, officialName = row?.EnglishName,
                    oldExactCandidate = oldResult?.Mechanic?.Name, oldResult = oldResult?.Reason ?? "BossUnattributedOrAmbiguous",
                    newIdCandidate = newResult?.Mechanic?.Name, newResult = newResult?.Reason ?? "BossUnattributedOrAmbiguous",
                    unmappedID = row == null, noGuideIDCandidate = candidates.Length == 0, candidates,
                    candidatesOmitted = Math.Max(0, byID[(key.Kind, key.ID)].Count() - candidates.Length),
                    conditions = "No phase, player, target or stacks reconstructed; Resolve uses null/zero/false. Conditional rejections are not evidence of missed live guidance."
                });
            }
        }
        if (omittedComparisons > 0) warnings.Add($"Comparison limit reached; {omittedComparisons} guide/group pairs omitted.");
        var report = new
        {
            schema = 1, recording = Path.GetFileName(zip), recordingSha256 = archiveHash,
            provenance = "Retrospective candidate matching only: historical source/adapted guides from this ZIP and the explicitly supplied current offline game sheets. No installed guide cache, live/new-version success claim, rendered-pixel evidence, helper ownership or replay lifecycle reconstruction. Each historical version is independently compared to same-territory observations; no claim it was active at that event time. Counts are records, not unique casts, encounters, alerts or successful decisions. Candidate arrays may include other bosses and require source attribution.",
            comparison = "Old baseline is GuideEventMatching with current official English row names; new baseline is GuideIdPlan using IDs only. Both use the same historical guide. Unknown actor IDs remain unattributed; observed actor names are retained as evidence. Duty isolation uses captured territory because observations do not contain ContentFinderConditionID.",
            catalog = new { catalog.Fingerprint, catalog.Count },
            limits = new { artifactBytes = ArtifactLimit, totalArtifactBytes = TotalArtifactLimit, guideVersions = VersionLimit, observations = ObservationLimit, groups = GroupLimit, comparisons = ComparisonLimit, textCharacters = 512 },
            capture = new { reader.Parsed, reader.Rejected, complete = exhausted ? (bool?)reader.Complete : null, exhausted, omittedGroups, omittedComparisons },
            guides, observations = observed.Values,
            unmappedIDs = observed.Values.Where(group => catalog.Row(group.Key.Kind, group.Key.ID) == null).ToArray(),
            comparisons, warnings
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
        using (var outputStream = File.Create(outputFile)) JsonSerializer.Serialize(outputStream, report, Json);
        Console.WriteLine($"Retrospective ID candidates: {historical.Count} historical guide versions, {observed.Count} observed groups. {outputFile}");
    }

    private static List<Historical> ReadHistorical(ZipArchive archive, List<string> warnings)
    {
        if (archive.Entries.Count > 4096) throw new InvalidDataException("ZIP entry count exceeds 4096.");
        if (archive.Entries.GroupBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
            throw new InvalidDataException("Duplicate ZIP paths are ambiguous.");
        var indexEntry = archive.GetEntry("guides/index.json") ?? throw new InvalidDataException("Historical guides/index.json is missing.");
        using var indexStream = indexEntry.Open();
        using var index = JsonDocument.Parse(ReadLimited(indexStream, 256 * 1024));
        var root = index.RootElement;
        if (Property(root, "complete").ValueKind != JsonValueKind.True) warnings.Add("Guide index is incomplete or does not declare completeness.");
        foreach (var warning in Array(root, "warnings").Take(32)) warnings.Add(Short(warning.ToString()));
        var files = Array(root, "files").ToArray();
        if (files.Length > 256) throw new InvalidDataException("Guide index exceeds 256 artifacts.");
        if (files.GroupBy(file => Text(file, "path"), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
            throw new InvalidDataException("Duplicate guide manifest paths are ambiguous.");
        var sourceDocuments = new Dictionary<string, Historical>(StringComparer.Ordinal);
        var result = new List<Historical>();
        long expanded = 0;
        foreach (var file in files.OrderBy(file => Text(file, "Kind") == "source" ? 0 : 1))
        {
            var kind = Text(file, "Kind");
            if (kind is not ("source" or "adapted")) continue;
            if (result.Count >= VersionLimit) { warnings.Add("Historical version limit reached; remaining artifacts omitted."); break; }
            var path = Text(file, "path");
            var basename = path.StartsWith("guides/", StringComparison.Ordinal) ? path[7..] : "";
            if (!ForetellCapture.ValidGuideFilename(basename)
                || kind == "source" && !basename.StartsWith("guide-source-", StringComparison.Ordinal)
                || kind == "adapted" && !basename.StartsWith("guide-snapshot-", StringComparison.Ordinal))
                throw new InvalidDataException("Unsafe or mismatched guide manifest path.");
            var entry = archive.GetEntry(path) ?? throw new InvalidDataException("Missing guide artifact: " + path);
            if (entry.Length != Number(file, "Bytes") || entry.Length > ArtifactLimit + 65536)
                throw new InvalidDataException("Guide artifact size differs from its manifest: " + path);
            using var input = entry.Open();
            var compressed = ReadLimited(input, ArtifactLimit + 65536);
            var sha256 = Convert.ToHexString(SHA256.HashData(compressed));
            if (!string.Equals(sha256, Text(file, "Sha256"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Guide artifact integrity check failed: " + path);
            using var compressedStream = new MemoryStream(compressed);
            using var gzip = new GZipStream(compressedStream, CompressionMode.Decompress);
            var bytes = ReadLimited(gzip, (int)Math.Min(ArtifactLimit, TotalArtifactLimit - expanded));
            expanded += bytes.Length;
            var expected = Number(file, "ExpandedBytes");
            if (expected > 0 && expected != bytes.Length)
                throw new InvalidDataException("Guide expanded size differs from its manifest: " + path);
            using var artifact = JsonDocument.Parse(bytes);
            var at = Property(file, "At").ValueKind == JsonValueKind.String && Property(file, "At").TryGetDateTime(out var date) ? date : default;
            if (kind == "source")
            {
                ValidateShape(artifact.RootElement, "Bosses");
                var document = artifact.RootElement.Deserialize<GuideDocument>(Json) ?? throw new InvalidDataException("Historical source is empty.");
                if (!document.Duty.Valid || document.SourceHash != Text(file, "SourceHash") || basename != "guide-source-" + document.SourceHash + ".json.gz")
                    throw new InvalidDataException("Historical source identity mismatch.");
                var historical = new Historical(path, path, sha256, at, kind, document);
                sourceDocuments.Add(basename, historical);
                result.Add(historical);
            }
            else
            {
                var snapshot = artifact.RootElement;
                if (!sourceDocuments.TryGetValue(Text(snapshot, "sourceFile"), out var source))
                { warnings.Add("Adaptation has no retained historical source: " + path); continue; }
                var duty = Property(snapshot, "Duty").Deserialize<GuideDuty>(Json);
                if (source.Document.SourceHash != Text(snapshot, "sourceHash") || duty != source.Document.Duty)
                    throw new InvalidDataException("Historical adaptation/source identity mismatch.");
                ValidateShape(snapshot, "Adapted");
                var adapted = Property(snapshot, "Adapted").Deserialize<GuideAdaptedBoss[]>(Json) ?? [];
                var document = Adapt(source.Document, adapted) with { ModelRevision = Text(snapshot, "modelRevision") };
                result.Add(new(path, source.File, sha256, at, kind, document));
            }
        }
        return result;
    }

    private static GuideDocument Adapt(GuideDocument source, GuideAdaptedBoss[] adapted)
    {
        var text = GuideSourceAssembly.NormalizeEvidence(string.Join('\n', new[] { source.Page?.Text ?? "" }
            .Concat(source.Sources.Select(page => page.Text)).Concat(source.Bosses.SelectMany(boss =>
                new[] { boss.Name }.Concat(boss.Phases.SelectMany(phase => new[] { phase.Context }.Concat(phase.Mechanics.Select(mechanic => mechanic.Text))))))));
        var bosses = adapted.Select(boss =>
        {
            var originals = source.Bosses.Where(original => original.Name == boss.Name).ToArray();
            if (originals.Length > 1 || originals.Length == 0 && !GuideRules.Mentions(GuideNames.Normalize(text), GuideNames.Boss(boss.Name)))
                throw new InvalidDataException("Adapted boss has no unique historical source.");
            var phases = boss.Phases.Select(phase =>
            {
                var originalPhases = originals.FirstOrDefault()?.Phases.Where(original => original.Name == phase.Name).ToArray() ?? [];
                if (originalPhases.Length > 1) throw new InvalidDataException("Adapted phase has no unique historical source.");
                var mechanics = phase.Mechanics.Select(mechanic =>
                {
                    var originalsForMechanic = originalPhases.FirstOrDefault()?.Mechanics.Where(original => original.Name == mechanic.Name).ToArray() ?? [];
                    if (originalsForMechanic.Length > 1) throw new InvalidDataException("Adapted mechanic has no unique historical source.");
                    if (originalsForMechanic.Length == 0 && (mechanic.Advice?.Evidence is not { Length: > 0 } evidence
                        || evidence.Any(quote => !text.Contains(GuideSourceAssembly.NormalizeEvidence(quote), StringComparison.Ordinal))))
                        throw new InvalidDataException("Adapted mechanic citations do not match the captured full source.");
                    var original = originalsForMechanic.FirstOrDefault() ?? new GuideMechanic(mechanic.Name, string.Join('\n', mechanic.Advice!.Evidence), "");
                    return new GuideMechanic(original.Name, original.Text, original.Anchor)
                    { Advice = mechanic.Advice, PhaseMemberships = original.PhaseMemberships };
                }).ToArray();
                return (originalPhases.FirstOrDefault() ?? new GuidePhase(phase.Name, "", [])) with { Mechanics = mechanics };
            }).ToArray();
            return (originals.FirstOrDefault() ?? new GuideBoss(boss.Name, "", [])) with { Phases = phases, DisplayName = boss.DisplayName };
        }).ToArray();
        return source with { Bosses = bosses };
    }

    private static void ValidateShape(JsonElement root, string property)
    {
        var bosses = Property(root, property);
        if (bosses.ValueKind != JsonValueKind.Array || bosses.GetArrayLength() > 32) throw new InvalidDataException("Invalid/bounded historical boss array.");
        var count = 0;
        var triggerCount = 0;
        foreach (var boss in bosses.EnumerateArray())
        {
            var phases = Property(boss, "Phases");
            if (phases.ValueKind != JsonValueKind.Array || phases.GetArrayLength() > 64) throw new InvalidDataException("Invalid/bounded historical phase array.");
            foreach (var phase in phases.EnumerateArray())
            {
                var mechanics = Property(phase, "Mechanics");
                if (mechanics.ValueKind != JsonValueKind.Array || (count += mechanics.GetArrayLength()) > 2048)
                    throw new InvalidDataException("Historical mechanics exceed the 2048-row limit.");
                foreach (var mechanic in mechanics.EnumerateArray())
                {
                    var triggers = Property(Property(mechanic, "Advice"), "Triggers");
                    if (triggers.ValueKind == JsonValueKind.Array && (triggerCount += triggers.GetArrayLength()) > 16384)
                        throw new InvalidDataException("Historical triggers exceed the 16384-row limit.");
                }
            }
        }
    }

    private static byte[] ReadLimited(Stream input, int limit)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = input.Read(buffer, 0, Math.Min(buffer.Length, limit + 1 - (int)output.Length))) != 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Expanded artifact read limit exceeded.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;
    private static IEnumerable<JsonElement> Array(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];
    private static string Text(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.String } text ? text.GetString()! : "";
    private static long Number(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out var result) ? result : 0;
    private static string Short(string text) => text.Length <= 512 ? text : text[..512];

    internal static void RunTests()
    {
        using var exact = new MemoryStream(new byte[16]);
        if (ReadLimited(exact, 16).Length != 16) throw new InvalidOperationException("Exact read limit rejected.");
        using var oversized = new MemoryStream(new byte[17]);
        MustReject(() => ReadLimited(oversized, 16), "Read limit accepted an extra byte.");
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, true)) gzip.Write(new byte[4096]);
        compressed.Position = 0;
        using var expanding = new GZipStream(compressed, CompressionMode.Decompress, true);
        MustReject(() => ReadLimited(expanding, 32), "Gzip expansion bypassed the read limit.");
        using var zipBytes = new MemoryStream();
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, true))
        {
            zip.CreateEntry("guides/index.json");
            zip.CreateEntry("guides/index.json");
        }
        zipBytes.Position = 0;
        using var duplicate = new ZipArchive(zipBytes, ZipArchiveMode.Read);
        MustReject(() => ReadHistorical(duplicate, []), "Duplicate historical paths were accepted.");
        var mechanic = new GuideMechanic("Detonation", "Detonation: spread.", "");
        var source = GuideIdPlanTests.Document(new GuideBoss("Warden", "", [new("", "", [mechanic])]));
        var advice = new GuideAdvice(GuideLanguage.French, "Détonation", "Écarte-toi", "", "cast", "Detonation", ["Detonation: spread."]);
        var snapshot = new GuideAdaptedBoss("Warden", "Gardien", 10,
            [new("", null, false, [new("Detonation", "Détonation", 999, "", null, GuidanceKind.None, "", false, [], null) { Advice = advice }])]);
        var adapted = Adapt(source, [snapshot]);
        if (adapted.Bosses[0].Phases[0].Mechanics[0].Advice != advice || adapted.SourceHash != source.SourceHash)
            throw new InvalidOperationException("Historical adaptation/source grounding was lost.");
        MustReject(() => Adapt(source, [snapshot with { Name = "Unrelated" }]), "Adapted boss crossed source boundaries.");
        var rawSource = source with { Bosses = [], Page = new("fixture", "https://example.org", "Warden\nDetonation: spread.", "") };
        if (Adapt(rawSource, [snapshot]).Bosses[0].Phases[0].Mechanics[0].Advice != advice)
            throw new InvalidOperationException("Full-source capture could not retain AI-adapted mechanics.");
        MustReject(() => Adapt(rawSource, [snapshot with { Name = "Unrelated" }]), "Full-source adaptation invented another boss.");
        var sourceFile = "guide-source-" + source.SourceHash + ".json.gz";
        using var validZip = HistoricalZip();
        using (var validArchive = new ZipArchive(validZip, ZipArchiveMode.Read, true))
        {
            var versions = ReadHistorical(validArchive, []);
            if (versions.Count != 2 || versions[1].Document.Bosses[0].Phases[0].Mechanics[0].Advice?.Cue != advice.Cue)
                throw new InvalidOperationException("Historical ZIP source/adaptation round-trip failed.");
            var restored = versions[1].Document;
            var plan = new GuideIdPlan(restored, new GuideIdCatalog([new("cast", 101, "Detonation", [])]));
            if (plan.Resolve(restored.Bosses[0], null, "cast", 101, 0, 0, false, 0).Mechanic == null
                || plan.Resolve(restored.Bosses[0], null, "cast", 999, 0, 0, false, 0).Mechanic != null)
                throw new InvalidOperationException("Review reused a snapshot ActionID instead of current official name grounding.");
        }
        using var tamperedZip = HistoricalZip(badHash: true);
        using var tamperedArchive = new ZipArchive(tamperedZip, ZipArchiveMode.Read);
        MustReject(() => ReadHistorical(tamperedArchive, []), "Historical artifact hash mismatch was accepted.");
        using var unsafeZip = HistoricalZip(unsafePath: true);
        using var unsafeArchive = new ZipArchive(unsafeZip, ZipArchiveMode.Read);
        MustReject(() => ReadHistorical(unsafeArchive, []), "Historical manifest traversal path was accepted.");

        MemoryStream HistoricalZip(bool badHash = false, bool unsafePath = false)
        {
            var storage = new MemoryStream();
            using (var zip = new ZipArchive(storage, ZipArchiveMode.Create, true))
            {
                var metadata = new List<object>();
                void Artifact(string basename, string kind, object value)
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
                    using var compressedArtifact = new MemoryStream();
                    using (var compressor = new GZipStream(compressedArtifact, CompressionLevel.Fastest, true)) compressor.Write(bytes);
                    var packed = compressedArtifact.ToArray();
                    var path = "guides/" + basename;
                    using (var destination = zip.CreateEntry(path).Open()) destination.Write(packed);
                    metadata.Add(new
                    {
                        path = unsafePath && kind == "source" ? "guides/../" + basename : path,
                        Kind = kind, Sha256 = badHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(packed)),
                        Bytes = packed.Length, ExpandedBytes = bytes.Length, At = source.RetrievedAt, source.SourceHash
                    });
                }
                Artifact(sourceFile, "source", source);
                Artifact("guide-snapshot-000001.json.gz", "adapted", new
                { sourceFile, sourceHash = source.SourceHash, source.Duty, Adapted = new[] { snapshot }, modelRevision = "historical" });
                using var manifest = zip.CreateEntry("guides/index.json").Open();
                JsonSerializer.Serialize(manifest, new { complete = true, files = metadata }, Json);
            }
            storage.Position = 0;
            return storage;
        }
    }

    private static void MustReject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }
}
