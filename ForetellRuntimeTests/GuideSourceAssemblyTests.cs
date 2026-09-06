using BossMod.Foretell;
using System.Text.Json;

internal static class GuideSourceAssemblyTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static void Run()
    {
        var duty = new GuideDuty(910, 911, "The Reference Chamber (Hard)");
        var first = new GuideSourcePage("Console Games Wiki", "https://ffxiv.consolegameswiki.com/wiki/Reference", "Sentinel\nHammer: Heavy damage to the tank.", "<p>Original wiki</p>", "html");
        var second = new GuideSourcePage("Community Workbook", "https://docs.google.com/spreadsheets/d/reference/edit", "A1: Sentinel\nB1: Hammer: Tank, mitigate this attack.", "<sheet>Original workbook</sheet>", "xlsx");
        var states = new[] { new GuideProviderState(first.Provider, "Ready", first.Url), new GuideProviderState(second.Provider, "Ready", second.Url), new GuideProviderState("Raven's Reminders", "Unavailable", "https://ravensreminders.com/") };
        var source = GuideSourceAssembly.Combine(duty, new([second, first], states), DateTime.UtcNow);
        var rechecked = GuideSourceAssembly.Combine(duty, new([first with { RetrievedAt = DateTime.UtcNow, FromCache = true }, second], states), DateTime.UtcNow);
        Check(source.SourceHash == rechecked.SourceHash && source.Page == rechecked.Page, "Rechecking unchanged sources invalidated prepared analysis");
        Check(source.Page!.Text.Contains(first.Text) && source.Page.Text.Contains(second.Text) && source.Bosses.Length == 0, "Assembly dropped or semantically parsed a source");
        Check(GuideSourceAssembly.Combine(duty, new([first, second with { Text = second.Text + "\nNew condition" }], states), DateTime.UtcNow).SourceHash != source.SourceHash,
            "Changed workbook did not invalidate analysis");
        var directory = Path.Combine(Path.GetTempPath(), "foretell-sources-" + Guid.NewGuid());
        try
        {
            var cache = new ForetellGuideCache(directory);
            cache.Write(source);
            Check(cache.Read(duty)?.Sources.Length == 2, "Combined source cache cannot round-trip");
            cache.Write(source with { Sources = [first, second with { Original = "tampered" }] });
            Check(cache.Read(duty) == null, "Source content changed without invalidating its provenance hash");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        var response = JsonSerializer.Serialize(new
        {
            summary = "Prepare mitigation.", bosses = new[] { new
            {
                name = "Sentinel", displayName = "Sentinel", summary = "Prepare mitigation.", mechanics = new[] { new
                {
                    name = "Hammer", displayName = "Hammer", cue = "Tank: mitigate", description = "The tank takes heavy damage.",
                    triggerKind = "cast", triggerName = "Hammer", evidence = new[] { "Hammer: Heavy damage to the tank.", "Hammer: Tank, mitigate this attack." },
                    responses = Array.Empty<GuideResponse>(), roles = new[] { "tank" }, conflict = ""
                } }
            } }
        });
        var profile = GuideModelCatalog.Get(GuideModelCatalog.DefaultID);
        var prepared = GuidePageAnalysis.Parse(source, source.Page.Text, response, GuideLanguage.English, profile);
        var mechanic = prepared.Bosses.Single().Phases.Single().Mechanics.Single();
        Check(GuideSourceAssembly.EvidenceSources(source, mechanic.Advice).Length == 2 && mechanic.Advice!.Roles.SequenceEqual(["tank"]), "Merged mechanic lost source attribution or role");
        var conflict = GuidePageAnalysis.Parse(source, source.Page.Text, response.Replace("\"conflict\":\"\"", "\"conflict\":\"Sources disagree.\""), GuideLanguage.English, profile);
        Check(conflict.Bosses[0].Phases[0].Mechanics[0].Advice!.TriggerKind == "manual", "Contradictory instructions became automatic alerts");
        Check(!GuideSynchronization.MatchesCast(conflict.Bosses[0].Phases[0].Mechanics[0], "Hammer"), "Observed cast reactivated a source conflict");
        var observed = new GuideMechanic("Hammer", mechanic.Text, "") { Advice = mechanic.Advice! with { TriggerKind = "manual", TriggerName = "" } };
        Check(GuideSynchronization.MatchesCast(observed, "Hammer") && !GuideSynchronization.MatchesCast(observed, "Pulse"), "Exact observed cast did not resolve a documentary trigger, or guessed another action");
        var renamed = new GuideMechanic("Heavy damage", mechanic.Text, "") { Advice = mechanic.Advice! with { TriggerKind = "manual", TriggerName = "", DisplayName = "Heavy damage" } };
        Check(GuideSynchronization.MatchesCast(renamed, "Hammer"), "Source-named observed ability lost its association after an AI display rename");
        Check(GuidePageAnalysis.GroundArenaReference("Leave the arena", "There is a poisonous field at the edge of the arena, damaging players until they leave.") == "Leave the poisonous field",
            "A damaging boundary field became an instruction to leave the arena");
        Check(GuidePageAnalysis.GroundArenaReference("Exit the arena", "Exit the arena before the explosion.") == "Exit the arena", "Explicit source movement was changed");
        var longText = new string('a', 2299) + char.ConvertFromUtf32(0x1F600) + new string('b', 2400);
        Check(string.Concat(GuidePageAnalysis.Paragraphs(longText)) == longText, "Physical citations cut source content or a surrogate pair");
        Check(GuidePageAnalysis.RestoreQuote("[\"B3\",1,\"Stay near.\\nIf marked, spread.\"]", "Stay near.\nIf marked, spread.") != null,
            "Decoded multiline workbook quotation lost its source grounding");
        Console.WriteLine("Multi-source refresh, cache integrity, full-content citations, attribution, roles and conflict abstention passed.");
    }
}
