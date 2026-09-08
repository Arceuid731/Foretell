using BossMod.Foretell;
using System.Diagnostics;
using System.Globalization;

internal static class GuideIdCatalogTests
{
    public static void Run()
    {
        MultilingualAliases();
        CanonicalConflicts();
        ImmutableSnapshots();
        Fingerprints();
        Bounds();
        Cancellation();
        Console.WriteLine("Guide ID catalogue: multilingual aliases, ambiguity, snapshots, fingerprint, bounds and cancellation passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static GuideGameRow Cast(uint id = 42) => new("cast", id, "King's Fire", ["Flamme du roi", "Königsfeuer", "王の炎"])
    {
        CastType = 2,
        EffectRange = 6.5f
    };

    private static void MultilingualAliases()
    {
        var cast = Cast();
        var catalogue = new GuideIdCatalog([cast, cast with { Names = ["王の炎", "King’s_Fire"] }, Cast(43),
            new("status", 42, "King's Fire", ["Flamme du roi"]), new("boss", 42, "The Keeper", ["Le Gardien", "守護者", "Der Wächter"])]);
        Check(catalogue.Count == 4, "Duplicate IDs did not merge or kinds were conflated.");
        foreach (var alias in new[] { "KING’S_FIRE", "  Flamme   du roi  ", "Königsfeuer", "Ko\u0308nigsfeuer", "王の炎" })
            Check(catalogue.Find("cast", alias).Select(row => row.ID).SequenceEqual(new uint[] { 42, 43 }), "Official alias lost an ambiguous ID: " + alias);
        Check(catalogue.Find("cast", "Fire").Length == 0 && catalogue.Find("cast", "king fire").Length == 0, "Lookup performed fuzzy or substring matching.");
        Check(catalogue.Find("status", "Flamme du roi").Single().ID == 42, "Status lookup crossed kinds.");
        Check(catalogue.Find("boss", "Protector: The Keeper").Single().ID == 42, "Boss heading/article normalization was not applied.");
        Check(catalogue.Find("boss", "守護者").Single().EnglishName == "The Keeper", "Localized boss alias lost its canonical name.");
        Check(catalogue.Find("cast", "The Keeper").Length == 0 && catalogue.Row("boss", 43) == null, "Lookup invented a row.");
        Check(catalogue.Row("cast", 42) is { CastType: 2, EffectRange: 6.5f }, "Action metadata was lost.");
        Check(catalogue.Find("cast", " ").Length == 0, "Blank query returned candidates.");
    }

    private static void CanonicalConflicts()
    {
        var original = Cast();
        foreach (var conflict in new[] { original with { EnglishName = "Other Fire" }, original with { CastType = 3 }, original with { EffectRange = 7 } })
        {
            Throws<InvalidDataException>(() => new GuideIdCatalog([original, conflict]));
            Throws<InvalidDataException>(() => new GuideIdCatalog([conflict, original]));
        }
        var split = new GuideIdCatalog([original with { Names = ["Flamme du roi"] }, original with { Names = ["Königsfeuer", "王の炎"] }]);
        Check(split.Fingerprint == new GuideIdCatalog([original]).Fingerprint, "Merging aliases depended on duplicate row representation.");
    }

    private static void ImmutableSnapshots()
    {
        var original = Cast();
        GuideGameRow[] input = [original];
        var catalogue = new GuideIdCatalog(input);
        var fingerprint = catalogue.Fingerprint;
        original.Names[0] = "Input mutation";
        input[0] = Cast(99);
        var found = catalogue.Find("cast", "Flamme du roi");
        found[0].Names[0] = "Result mutation";
        found[0] = Cast(100);
        catalogue.Row("cast", 42)!.Names[0] = "Row mutation";
        Check(catalogue.Find("cast", "Flamme du roi").Single().ID == 42, "Input/output array mutation changed the index.");
        Check(!catalogue.Row("cast", 42)!.Names.Any(name => name.Contains("mutation", StringComparison.Ordinal)), "Mutable arrays leaked into stored rows.");
        Check(catalogue.Fingerprint == fingerprint && catalogue.Row("cast", 99) == null, "Snapshot mutation changed catalogue identity.");
    }

    private static void Fingerprints()
    {
        var first = Cast();
        var second = new GuideGameRow("boss", 100, "Keeper", ["Gardien", "守護者"]);
        var expected = new GuideIdCatalog([first, second]).Fingerprint;
        var reordered = new GuideIdCatalog([second with { Names = second.Names.Reverse().ToArray() }, first with { Names = [.. first.Names.Reverse(), first.Names[0]] }, first]);
        Check(expected.Length == 64 && expected.All(character => char.IsAsciiHexDigit(character)), "Fingerprint is not SHA256 hex.");
        Check(expected == reordered.Fingerprint, "Fingerprint depends on input, alias or duplicate ordering.");
        foreach (var changed in new[] { first with { ID = 43 }, first with { Kind = "status" }, first with { EnglishName = "Other Fire" },
            first with { Names = ["Flamme différente", "Königsfeuer", "王の炎"] }, first with { Names = ["Flamme du roi", "Anderes Feuer", "王の炎"] },
            first with { Names = ["Flamme du roi", "Königsfeuer", "別の炎"] }, first with { CastType = 3 }, first with { EffectRange = 6.75f } })
            Check(expected != new GuideIdCatalog([changed, second]).Fingerprint, "Changed ID, name or metadata did not invalidate fingerprint.");
        Check(expected != new GuideIdCatalog([first]).Fingerprint, "Removed row did not change fingerprint.");
        Check(new GuideIdCatalog([new("cast", 1, "Same", ["ab", "c"])]).Fingerprint
            != new GuideIdCatalog([new("cast", 1, "Same", ["a", "bc"])]).Fingerprint, "Fingerprint omitted string boundaries.");
        Check(new GuideIdCatalog([first with { EffectRange = -0f }]).Fingerprint == new GuideIdCatalog([first with { EffectRange = 0f }]).Fingerprint,
            "Equivalent signed zero metadata changed fingerprint.");
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Check(expected == new GuideIdCatalog([second, first]).Fingerprint, "Fingerprint depends on process culture.");
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
        Check(new GuideIdCatalog([]).Count == 0 && new GuideIdCatalog([]).Fingerprint.Length == 64, "Empty catalogue was not deterministic.");
    }

    private static void Bounds()
    {
        var row = Cast();
        var oversized = new string('x', GuideIdCatalog.NameLengthLimit + 1);
        Throws<InvalidDataException>(() => new GuideIdCatalog([row with { EnglishName = oversized }]));
        Throws<InvalidDataException>(() => new GuideIdCatalog([row with { Names = [oversized] }]));
        Throws<InvalidDataException>(() => new GuideIdCatalog([row with { Names = Enumerable.Repeat("alias", GuideIdCatalog.NamesPerRowLimit + 1).ToArray() }]));
        var aliases = Enumerable.Range(0, GuideIdCatalog.NamesPerRowLimit).Select(index => "alias" + index).ToArray();
        Throws<InvalidDataException>(() => new GuideIdCatalog([row with { Names = aliases[..16] }, row with { Names = aliases[16..] }]));
        Throws<InvalidDataException>(() => new GuideIdCatalog(Enumerable.Repeat(row, GuideIdCatalog.RowLimit + 1)));
        var longRow = new GuideGameRow("cast", 1, new string('x', GuideIdCatalog.NameLengthLimit), []);
        Throws<InvalidDataException>(() => new GuideIdCatalog(Enumerable.Repeat(longRow, GuideIdCatalog.TotalNameLengthLimit / GuideIdCatalog.NameLengthLimit + 1)));
        var candidates = Enumerable.Range(1, GuideIdCatalog.CandidateLimit).Select(index => new GuideGameRow("cast", (uint)index, "Shared name", ["Shared_name"]));
        Check(new GuideIdCatalog(candidates).Find("cast", "Shared name").Length == GuideIdCatalog.CandidateLimit, "Candidate limit truncated valid ambiguity.");
        Throws<InvalidDataException>(() => new GuideIdCatalog(candidates.Append(new("cast", (uint)GuideIdCatalog.CandidateLimit + 1, "Shared name", []))));
        var catalogue = new GuideIdCatalog([longRow]);
        Check(catalogue.Find("cast", longRow.EnglishName).Length == 1, "Name exactly at the limit was rejected.");
        Throws<InvalidDataException>(() => catalogue.Find("cast", oversized));
        Throws<InvalidDataException>(() => new GuideIdCatalog([row with { Names = [new string('\uFDFA', 32)] }]));
        Throws<ArgumentException>(() => new GuideIdCatalog([row with { Kind = "action" }]));
        Throws<ArgumentException>(() => catalogue.Find("Cast", "name"));
        Throws<ArgumentException>(() => catalogue.Row("unknown", 1));
        Throws<ArgumentNullException>(() => new GuideIdCatalog(null!));
        Throws<ArgumentNullException>(() => new GuideIdCatalog([row with { Names = null! }]));
        Throws<ArgumentNullException>(() => new GuideIdCatalog([row with { Names = [null!] }]));
        foreach (var invalid in new[] { row with { CastType = -1 }, row with { EffectRange = -1 }, row with { EffectRange = float.NaN }, row with { EffectRange = float.PositiveInfinity } })
            Throws<InvalidDataException>(() => new GuideIdCatalog([invalid]));
        var unnamed = new GuideIdCatalog([new("cast", 0, "", []), new("cast", 1, "", [])]);
        Check(unnamed.Count == 2 && unnamed.Row("cast", 0) != null && unnamed.Find("cast", "").Length == 0, "Unnamed sheet rows were lost or indexed as aliases.");
    }

    private static void Cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Throws<OperationCanceledException>(() => new GuideIdCatalog([], cancelled.Token));
        Throws<OperationCanceledException>(() => GuideIdCatalog.LoadInstalled(cancelled.Token));
        Throws<OperationCanceledException>(() => GuideIdCatalog.FromGameData(null!, cancelled.Token));
        using var midLoad = new CancellationTokenSource();
        Throws<OperationCanceledException>(() => new GuideIdCatalog(Rows(), midLoad.Token));
        IEnumerable<GuideGameRow> Rows()
        {
            yield return Cast();
            midLoad.Cancel();
            yield return Cast(43);
        }
    }

    public static void SheetSmoke(string sqpackDirectory, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        using var data = new Lumina.GameData(sqpackDirectory);
        var catalogue = GuideIdCatalog.FromGameData(data, cancellation);
        Check(catalogue.Count > 0, "Installed catalogue was empty.");
        Console.WriteLine($"Guide ID catalogue: {catalogue.Count} installed rows loaded in {timer.Elapsed.TotalSeconds:F3}s.");
        timer.Restart();
        Verify<Lumina.Excel.Sheets.Action>("cast", row => row.Name.ToString());
        Verify<Lumina.Excel.Sheets.Status>("status", row => row.Name.ToString());
        Verify<Lumina.Excel.Sheets.BNpcName>("boss", row => row.Singular.ToString());
        Check(catalogue.Fingerprint == GuideIdCatalog.FromGameData(data, cancellation).Fingerprint, "Repeated installed load changed the fingerprint.");
        timer.Stop();
        Console.WriteLine($"Guide ID catalogue: verification completed in {timer.Elapsed.TotalSeconds:F3}s (including fingerprint reload), SHA256 {catalogue.Fingerprint}.");

        void Verify<TRow>(string kind, Func<TRow, string> name) where TRow : struct, Lumina.Excel.IExcelRow<TRow>
        {
            var candidatesByName = new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal);
            foreach (var language in new[] { Lumina.Data.Language.English, Lumina.Data.Language.French, Lumina.Data.Language.German, Lumina.Data.Language.Japanese })
            {
                var sheet = data.GetExcelSheet<TRow>(language) ?? throw new InvalidDataException($"Missing {kind} sheet for {language}.");
                var checkedNames = 0;
                foreach (var row in sheet)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var officialName = name(row);
                    Check(catalogue.Row(kind, row.RowId) != null, "Installed row was omitted.");
                    var normalizedName = kind == "boss" ? GuideNames.Boss(officialName) : GuideNames.Normalize(officialName);
                    if (normalizedName.Length == 0) continue;
                    if (!candidatesByName.TryGetValue(normalizedName, out var candidateIDs))
                    {
                        candidateIDs = catalogue.Find(kind, officialName).Select(candidate => candidate.ID).ToHashSet();
                        candidatesByName.Add(normalizedName, candidateIDs);
                    }
                    Check(candidateIDs.Contains(row.RowId), "Installed official alias omitted its ID.");
                    ++checkedNames;
                }
                Check(checkedNames > 0, $"No named {kind} rows for {language}.");
            }
        }
    }
}
