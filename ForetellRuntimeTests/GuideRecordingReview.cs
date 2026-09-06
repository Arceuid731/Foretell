using BossMod.Foretell;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class GuideRecordingReview
{
    public static void Run(string recording, string cache, string gameDirectory, string output)
    {
        using var archive = ZipFile.OpenRead(recording);
        using var analysis = JsonDocument.Parse(archive.GetEntry("foretell-analysis.json")!.Open());
        var root = analysis.RootElement;
        var territory = root.GetProperty("encounter").GetProperty("TerritoryID").GetUInt32();
        var content = root.GetProperty("encounter").GetProperty("ContentFinderConditionID").GetUInt32();
        using var data = new Lumina.GameData(gameDirectory);
        var duties = data.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>(Lumina.Data.Language.English)!;
        var duty = new GuideDuty(content, territory, duties.GetRow(content).Name.ToString());
        var document = new ForetellGuideCache(cache).Read(duty) ?? throw new InvalidDataException("Matching validated guide cache unavailable");
        var npcs = data.GetExcelSheet<Lumina.Excel.Sheets.BNpcName>(Lumina.Data.Language.English)!;
        var actions = data.GetExcelSheet<Lumina.Excel.Sheets.Action>(Lumina.Data.Language.English)!;
        var french = data.GetExcelSheet<Lumina.Excel.Sheets.Action>(Lumina.Data.Language.French)!;
        var reader = new ForetellRecordingReader(recording);
        var casts = new List<object>();
        var names = new Dictionary<ulong, uint>();
        var bosses = document.Bosses.Select(boss => GuideNames.Boss(boss.Name)).ToHashSet();
        var foreign = new Dictionary<uint, int>();
        var deaths = new List<object>();
        var completed = new List<DateTime>();
        foreach (var observation in reader.Read())
        {
            if (observation.TerritoryID != territory) { foreign[observation.TerritoryID] = foreign.GetValueOrDefault(observation.TerritoryID) + 1; continue; }
            var nameID = (uint)observation.Numeric.GetValueOrDefault("actor.nameID");
            if (nameID != 0 && observation.ActorID != 0) names[observation.ActorID] = nameID;
            var name = nameID == 0 ? "" : npcs.GetRow(nameID).Singular.ToString();
            if (observation.Kind == ObservationKind.DutyCompleted) completed.Add(observation.At);
            if (observation.Kind == ObservationKind.DeathChanged && observation.Flag && bosses.Contains(GuideNames.Boss(name)))
                deaths.Add(new { observation.At, Boss = name, observation.ActorID, observation.ActorOID });
            if (observation.Kind != ObservationKind.CastStart || observation.SourceKind != SourceKind.Enemy) continue;
            var action = actions.GetRow(observation.PrimaryID).Name.ToString();
            var ownerID = (ulong)observation.Numeric.GetValueOrDefault("actor.ownerID");
            var ownerName = names.TryGetValue(ownerID, out var ownerNameID) ? npcs.GetRow(ownerNameID).Singular.ToString() : "";
            var type = observation.Text.GetValueOrDefault("actor.type.name", "Unknown");
            var bossName = type == "Helper" ? ownerName : name;
            var match = observation.Value1 is > 0 and <= 120 ? GuideSynchronization.Match(document,
                new(duty, observation.ActorID, observation.ActorOID, nameID, bossName, observation.PrimaryID, action, observation.At.AddSeconds(observation.Value1)), observation.At) : null;
            casts.Add(new
            {
                observation.At, Caster = name, Type = type, NameID = nameID, observation.ActorOID, ActionID = observation.PrimaryID,
                Action = action, FrenchAction = french.GetRow(observation.PrimaryID).Name.ToString(), OwnerID = ownerID,
                DocumentedBoss = bosses.Contains(GuideNames.Boss(name)), UniqueNameMatch = match != null,
                Mechanic = match?.Mechanic.Name,
                CurrentPreparedGuidance = match == null ? GuidanceKind.None : GuideRules.LiveGuidance(match.Mechanic, match.Phase, match.Boss)
            });
        }
        var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
        var report = new
        {
            recording = Path.GetFileName(recording), recordingSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(recording))),
            provenance = "Retrospective unique-name comparison against current local guide cache and installed English/French client sheets. NOT historical guide state, actual live matches, or rendered pixels. CurrentPreparedGuidance uses the reviewing build, not capture-version rules.",
            historicalGuidesIncluded = archive.GetEntry("guides/index.json") != null,
            captureVersion = root.GetProperty("sessionPluginVersion").GetString(), selectedSession = root.GetProperty("selectedSession"),
            reader.Parsed, reader.Rejected, reader.Complete, foreignTerritoryEvents = foreign,
            guide = new { document.Title, document.Revision, document.SourceHash, document.RetrievedAt, document.MechanicCount, document.SourceUrl },
            bosses = document.Bosses.Select(boss => new { boss.Name, Mechanics = boss.Phases.SelectMany(phase => phase.Mechanics).Select(mechanic => mechanic.Name) }),
            casts, deaths, dutyCompleted = completed
        };
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "guide-comparison.json"), JsonSerializer.Serialize(report, options));
        File.WriteAllText(Path.Combine(output, "current-cache-source-not-historical.json"), JsonSerializer.Serialize(document, options));
        var summariesPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cache))!, "foretell-guide-summaries", duty.Key + "-French.json");
        if (File.Exists(summariesPath) && new FileInfo(summariesPath).Length <= 2 * 1024 * 1024)
        {
            var text = File.ReadAllText(summariesPath);
            using var summaries = JsonDocument.Parse(text);
            if (summaries.RootElement.GetProperty("SourceHash").GetString() == document.SourceHash)
                File.WriteAllText(Path.Combine(output, "current-cache-summaries-not-historical.json"), text);
        }
        Console.WriteLine($"Verified capture: {reader.Parsed} events; complete={reader.Complete}; {casts.Count} enemy/helper casts. Retrospective comparison: {Path.GetFullPath(output)}");
    }
}
