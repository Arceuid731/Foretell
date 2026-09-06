using System.Net;
using System.Text;
using System.Text.Json;
using BossMod.Foretell;
using BossMod;
using System.Numerics;

internal static class GuideTests
{
    private static readonly GuideDuty Duty = new(17, 117, "the Test Chamber");
    private static readonly DateTime Now = DateTime.UtcNow;
    private const string Html = """
        <div class="mw-parser-output"><style>.ignore{}</style><h2><span id="Bosses">Bosses</span></h2>
        <div class="mw-heading mw-heading3"><h3 id="Sentinel"><a>Sentinel</a><img alt="boss"></h3></div>
        <p>Remain inside the arena.</p>
        <ul><li><b>Hammer:</b> Tankbuster.</li>
        <li><b>Judgment:</b> If marked, stay away.<ul><li>If the shield is broken, share the hit instead.</li></ul></li></ul>
        <p>After the second hit, return to the group.</p>
        <p><b>Phase 2:</b></p><ul><li><b>Hammer:</b> Stay behind the boss.</li>
        <li><b>Pulse:</b> Unavoidable raidwide damage that must be healed through.</li></ul>
        <h3><span id="Keeper">Keeper</span></h3><h4>Opening</h4>
        <table><tbody><tr><th>Ability</th><th>Response</th></tr><tr><td>Lance</td><td>Tankbuster.</td></tr></tbody></table>
        <script>Do not execute this. <h3>Fake boss</h3></script>
        <h2>Loot</h2><h3>Sentinel</h3><ul><li>Coin: Not a mechanic.</li></ul></div>
        """;

    private static string Response(GuideDuty duty, string html = Html, long revision = 123)
        => JsonSerializer.Serialize(new { parse = new { title = duty.EnglishName, revid = revision, text = html } });
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception error) when (error is InvalidDataException or JsonException) { return; }
        throw new InvalidOperationException(message);
    }

    public static void Run()
    {
        var document = ForetellGuideParser.Parse(Response(Duty), Duty, Now);
        Check(document.Bosses.Length == 2 && document.MechanicCount == 5, "Boss parsing included loot, script or nested conditions as bosses/mechanics");
        var sentinel = document.Bosses[0];
        Check(sentinel.Phases.Length == 2 && sentinel.Phases[1].Name == "Phase 2", "Paragraph phase labels were lost");
        Check(sentinel.Phases[0].Context == "Remain inside the arena.", "Boss context was lost");
        var judgment = sentinel.Phases[0].Mechanics[1];
        Check(judgment.Text.Contains("If the shield is broken") && judgment.Text.Contains("After the second hit"), "Conditional child or continuation paragraph was dropped");
        Check(GuidePreparation.Instruction(judgment) == GuideInstruction.Unprepared, "Complex conditional prose became a generic instruction");
        Check(document.Bosses[1].Phases[0].Mechanics.Single().Name == "Lance", "Table action parsing lost context");
        Check(GuidePreparation.Instruction(sentinel.Phases[0].Mechanics[0]) == GuideInstruction.Tankbuster, "Simple response preparation failed");
        Check(GuidePreparation.Instruction(new("Pulse", "Raidwide damage, unless the shield is active.", "")) == GuideInstruction.Unprepared, "Condition was discarded during preparation");
        Check(GuidePreparation.Instruction(new("Pulse", "Raidwide damage. Then spread out.", "")) == GuideInstruction.Unprepared, "Sequence was discarded during preparation");
        foreach (var language in Enum.GetValues<GuideLanguage>())
            Check(GuidePreparation.Text(GuideInstruction.Tankbuster, language).Length > 10, "Prepared instruction lacks client language translation");
        Check(GuidePreparation.Text(GuideInstruction.Tankbuster, GuideLanguage.French).StartsWith("Si tu es ciblé"), "Personal target condition is missing in French");
        Reject(() => ForetellGuideParser.Parse(Response(Duty with { EnglishName = "the Test Chamber (Extreme)" }), Duty, Now), "Different duty variant accepted");
        Reject(() => ForetellGuideParser.Parse(Response(Duty, "<h2>Loot</h2><ul><li>Hammer: Coin.</li></ul>"), Duty, Now), "Unsupported page became a ready guide");
        Reject(() => ForetellGuideParser.Parse(Response(Duty, string.Concat(Enumerable.Repeat("<div>", 70))), Duty, Now), "Unbounded HTML nesting accepted");
        Reject(() => ForetellGuideParser.Parse(new string('x', ForetellGuideParser.MaxResponseBytes + 1), Duty, Now), "Oversized guide accepted");
        Check(document.Revision == 123 && document.SourceHash.Length == 64 && document.SourceUrl.Contains("oldid=123"), "Revision provenance is missing");
        var trial = ForetellGuideParser.Parse(Response(Duty, "<h2>Strategy</h2><h3>Lord of tests: <a>Keeper</a></h3><h3>Phase 1</h3><ul><li>Lance is an attack that hits the target.</li></ul><h3>Phase 2</h3><p>If marked, move outside.</p><h2>Loot</h2>"), Duty, Now);
        Check(trial.Bosses.Single().Name == "Keeper" && trial.Bosses[0].Phases.Length == 2 && trial.MechanicCount == 1, "Single-boss trial headings and sibling phase headings were misclassified");
        Synchronization(document);
        AsyncChecks(document).GetAwaiter().GetResult();
        Console.WriteLine("Guide parsing, preserved conditions, ambiguous contexts, localization, bounded HTTP, cache and cancellation tests passed.");
    }

    private static void Synchronization(GuideDocument document)
    {
        var cast = new GuideCast(Duty, 100, 200, 300, "Sentinel", 400, "Pulse", Now.AddSeconds(5));
        var matched = GuideSynchronization.Match(document, cast, Now);
        Check(matched is { Phase.Name: "Phase 2", Mechanic.Name: "Pulse" }, "Unique live cast failed to identify its documented phase group");
        Check(GuideSynchronization.Match(document, cast with { ActionName = "Hammer" }, Now) == null, "Repeated name across phases incorrectly synchronized");
        Check(GuideSynchronization.Match(document, cast with { Duty = Duty with { ContentID = 18 } }, Now) == null, "Cast from another content synchronized");
        Check(GuideSynchronization.Match(document, cast with { Duty = Duty with { TerritoryID = 118 } }, Now) == null, "Cast from another territory synchronized");
        Check(GuideSynchronization.Match(document, cast with { Duty = Duty with { EnglishName = "the Test Chamber (Extreme)" } }, Now) == null, "Wrong variant synchronized");
        Check(GuideSynchronization.Match(document, cast with { BossName = "Keeper" }, Now) == null, "Another boss with a resembling action synchronized");
        Check(GuideSynchronization.Match(document, cast with { ActionName = "Pulse II" }, Now) == null, "Fuzzy action name synchronized");
        Check(GuideSynchronization.Match(document, cast with { NameID = 0 }, Now) == null, "Unidentified actor synchronized");
        Check(GuideSynchronization.Match(document, cast, Now.AddSeconds(5)) == null, "Finished cast remains highlighted");
        var duplicate = document with { Bosses = [document.Bosses[0], document.Bosses[0]] };
        Check(GuideSynchronization.Match(duplicate, cast, Now) == null, "Ambiguous boss identity synchronized");
        var actor = new Actor(100, 200, 0, 0, "localized actor", 300, ActorType.Enemy, Class.None, 50, Vector4.Zero, resolveGameMetadata: false)
        { CastInfo = new() { Action = new(ActionType.Spell, 400), TotalTime = 5, TargetID = 1 } };
        string Name(string sheet, uint id) => sheet == "BNpcName" && id == 300 ? "Sentinel" : sheet == "Action" && id == 400 ? "Pulse" : "";
        var runtime = ForetellEngine.MatchGuideActor(document, Duty, actor, Now, Name);
        Check(runtime != null && ForetellEngine.GuideCastIsCurrent(runtime, Duty, actor, Now), "Typed WorldState cast did not synchronize");
        var tankbuster = runtime! with { Mechanic = new("Pulse", "Tankbuster.", "") };
        Check(ForetellEngine.GuidePersonalResponse(tankbuster, 1, actor) == GuideInstruction.Tankbuster
            && ForetellEngine.GuidePersonalResponse(tankbuster, 2, actor) == GuideInstruction.Unprepared, "Tankbuster instruction targeted the wrong player");
        var stack = runtime with { Mechanic = new("Pulse", "A stack marker on a random player.", "") };
        Check(ForetellEngine.GuidePersonalResponse(stack, 1, actor) == GuideInstruction.Unprepared, "Cast target was treated as a verified stack marker");
        actor.CastInfo = null;
        Check(!ForetellEngine.GuideCastIsCurrent(runtime!, Duty, actor, Now), "Interrupted live cast stayed active");
        actor.CastInfo = new() { Action = new(ActionType.Spell, 400), TotalTime = 10 };
        Check(!ForetellEngine.GuideCastIsCurrent(runtime!, Duty, actor, Now), "A replacement cast inherited the previous activation");
        actor.CastInfo.EventHappened = true;
        Check(ForetellEngine.MatchGuideActor(document, Duty, actor, Now, Name) == null, "Resolved live cast produced a guide alert");
        actor.CastInfo.EventHappened = false; actor.IsDead = true;
        Check(ForetellEngine.MatchGuideActor(document, Duty, actor, Now, Name) == null, "Dead boss synchronized");
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private static async Task AsyncChecks(GuideDocument document)
    {
        var directory = Path.Combine(Path.GetTempPath(), "foretell-guide-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var cache = new ForetellGuideCache(directory);
            cache.Write(document);
            Check(cache.Read(Duty)?.SourceHash == document.SourceHash, "Prepared cache did not round-trip");
            Check(cache.Read(Duty with { EnglishName = "wrong variant" }) == null, "Cache accepted wrong identity");
            using (var service = new ForetellGuideService(directory, (_, _) => throw new InvalidOperationException("Fresh cache requested network")))
            {
                service.RequestGuide(Duty);
                await WaitFor(() => service.Snapshot.State is GuideState.Ready or GuideState.Failed);
                Check(service.Snapshot.Document?.Revision == 123, "Cached guide unavailable without model/network");
            }
            cache.Write(document with { RetrievedAt = Now.AddDays(-8) });
            using (var service = new ForetellGuideService(directory, (_, _) => throw new HttpRequestException("offline")))
            {
                service.RequestGuide(Duty);
                await WaitFor(() => service.Snapshot.State == GuideState.Offline);
                Check(service.Snapshot.Document != null, "Network failure destroyed a usable stale guide");
            }
            var path = Path.Combine(directory, Duty.Key + ".json");
            File.WriteAllText(path, "{\"Payload\":\"{}\",\"Hash\":\"wrong\"}");
            Check(cache.Read(Duty) == null, "Corrupt cache accepted");
            using (var service = new ForetellGuideService(directory, (duty, _) => Task.FromResult(Response(duty))))
            {
                service.RequestGuide(Duty);
                await WaitFor(() => service.Snapshot.State is GuideState.Ready or GuideState.Failed);
                Check(service.Snapshot.State == GuideState.Ready && cache.Read(Duty) != null, "Corrupt cache could not recover from network");
            }
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = Duty with { ContentID = 18, TerritoryID = 118, EnglishName = "Another Chamber" };
            var last = Duty with { ContentID = 19, TerritoryID = 119, EnglishName = "Final Chamber" };
            using (var service = new ForetellGuideService(directory, async (duty, cancellation) =>
            {
                if (duty == second) { started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellation); }
                return Response(duty);
            }))
            {
                service.RequestGuide(second);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                service.RequestGuide(last);
                await WaitFor(() => service.Snapshot.State is GuideState.Ready or GuideState.Failed);
                Check(service.Snapshot.Document?.Duty == last, "Canceled duty request replaced the current guide");
                service.Cancel();
                Check(service.Snapshot.State == GuideState.Idle, "Leaving instance retained active guide state");
                service.Dispose();
                await service.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            }
            using (var source = new ForetellWikiSource(new ReplyHandler(request =>
            {
                Check(request.RequestUri?.Host == "ffxiv.consolegameswiki.com" && request.RequestUri.Query.Contains("Final%20Chamber"), "Source URL was not fixed/escaped");
                return new(HttpStatusCode.OK) { Content = new StringContent(Response(last), Encoding.UTF8, "application/json") };
            }))) Check((await source.Fetch(last, CancellationToken.None)).Contains("parse"), "Programmatic JSON retrieval failed");
            using (var source = new ForetellWikiSource(new ReplyHandler(_ => new(HttpStatusCode.OK)
            { Content = new StringContent(new string('x', ForetellGuideParser.MaxResponseBytes + 1), Encoding.UTF8, "application/json") })))
            {
                try { await source.Fetch(last, CancellationToken.None); throw new Exception("Oversized HTTP response accepted"); }
                catch (InvalidDataException) { }
            }
            using (var source = new ForetellWikiSource(new ReplyHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("challenge", Encoding.UTF8, "text/html") })))
            {
                try { await source.Fetch(last, CancellationToken.None); throw new Exception("HTML challenge treated as JSON guide"); }
                catch (InvalidDataException) { }
            }
            var blocked = Path.Combine(directory, "not-a-directory");
            File.WriteAllText(blocked, "test");
            using (var service = new ForetellGuideService(blocked, (duty, _) => Task.FromResult(Response(duty))))
            {
                service.RequestGuide(Duty);
                await WaitFor(() => service.Snapshot.State is GuideState.Ready or GuideState.Failed);
                Check(service.Snapshot.Document != null && service.Snapshot.Error.StartsWith("CacheWrite:"), "Cache write failure hid the prepared in-memory guide");
                service.Dispose();
                await service.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            }
            for (uint index = 100; index < 231; ++index) cache.Write(document with { Duty = Duty with { ContentID = index } });
            Check(Directory.GetFiles(directory, "C*-T*.json").Length <= 128 && cache.Read(Duty with { ContentID = 230 }) != null,
                "Cache quota was not enforced or removed the current document");
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
    }

    public static void Smoke(string[] args)
    {
        using var source = new ForetellWikiSource();
        foreach (var name in args.Skip(1))
        {
            var duty = new GuideDuty(1, 1, name);
            var response = source.Fetch(duty, CancellationToken.None).GetAwaiter().GetResult();
            var document = ForetellGuideParser.Parse(response, duty, DateTime.UtcNow);
            Console.WriteLine($"{document.Title}: revision {document.Revision}; {document.Bosses.Length} bosses; {document.MechanicCount} mechanics; {document.Bosses.Sum(boss => boss.Phases.Length)} phase groups; {document.Bosses.SelectMany(boss => boss.Phases).SelectMany(phase => phase.Mechanics).Count(mechanic => GuidePreparation.Instruction(mechanic) != GuideInstruction.Unprepared)} simple responses prepared.");
            foreach (var boss in document.Bosses) Console.WriteLine($"  {boss.Name}: {string.Join(", ", boss.Phases.Select(phase => $"{phase.Name} ({phase.Mechanics.Length})"))}");
        }
    }

    public static void SheetSmoke(string directory)
    {
        using var data = new Lumina.GameData(directory);
        var duties = data.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>(Lumina.Data.Language.English) ?? throw new InvalidDataException("Missing English duty sheet");
        var frenchDuties = data.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>(Lumina.Data.Language.French) ?? throw new InvalidDataException("Missing French duty sheet");
        foreach (var name in new[] { "The Praetorium", "The Orbonne Monastery", "Sastasha", "The Bowl of Embers (Hard)" })
        {
            var candidates = duties.Where(duty => GuideNames.Normalize(duty.Name.ToString()) == GuideNames.Normalize(name)).ToArray();
            Check(candidates.Length == 1, "Ambiguous or absent official content identity: " + name);
            var duty = candidates[0];
            Console.WriteLine($"CFC {duty.RowId} / Territory {duty.TerritoryType.RowId}: {duty.Name} → {frenchDuties.GetRow(duty.RowId).Name}");
        }
        var actions = data.GetExcelSheet<Lumina.Excel.Sheets.Action>(Lumina.Data.Language.English) ?? throw new InvalidDataException("Missing English action sheet");
        var frenchActions = data.GetExcelSheet<Lumina.Excel.Sheets.Action>(Lumina.Data.Language.French) ?? throw new InvalidDataException("Missing French action sheet");
        var repeated = actions.Where(action => GuideNames.Normalize(action.Name.ToString()) == "innocence").ToArray();
        Check(repeated.Length > 1, "Real data no longer exercises action name ambiguity; update the smoke test");
        Console.WriteLine("Action candidates only; no encounter binding from names: " + string.Join(", ", repeated.Select(action => $"{action.RowId} → {frenchActions.GetRow(action.RowId).Name}")));
        var npcs = data.GetExcelSheet<Lumina.Excel.Sheets.BNpcName>(Lumina.Data.Language.English) ?? throw new InvalidDataException("Missing English NPC sheet");
        var frenchNpcs = data.GetExcelSheet<Lumina.Excel.Sheets.BNpcName>(Lumina.Data.Language.French) ?? throw new InvalidDataException("Missing French NPC sheet");
        foreach (var name in new[] { "Mustadio", "Nero tol Scaeva" })
        {
            var candidates = npcs.Where(npc => GuideNames.Boss(npc.Singular.ToString()) == GuideNames.Boss(name)).ToArray();
            Check(candidates.Length > 0, "Official NPC name unavailable: " + name);
            Console.WriteLine("BNpcName candidates only: " + string.Join(", ", candidates.Select(npc => $"{npc.RowId} → {frenchNpcs.GetRow(npc.RowId).Singular}")));
        }
        var statuses = data.GetExcelSheet<Lumina.Excel.Sheets.Status>(Lumina.Data.Language.English) ?? throw new InvalidDataException("Missing English status sheet");
        var frenchStatuses = data.GetExcelSheet<Lumina.Excel.Sheets.Status>(Lumina.Data.Language.French) ?? throw new InvalidDataException("Missing French status sheet");
        var analysis = statuses.Where(status => GuideNames.Normalize(status.Name.ToString()) == "analysis").ToArray();
        Check(analysis.Length > 0, "Official status name unavailable");
        Console.WriteLine("Status candidates only: " + string.Join(", ", analysis.Select(status => $"{status.RowId} → {frenchStatuses.GetRow(status.RowId).Name}")));
    }
}
