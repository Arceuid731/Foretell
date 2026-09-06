using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using BossMod.Foretell;

internal static class WorkbookGuideTests
{
    private static readonly XNamespace Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace DocumentRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly GuideDuty Duty = new(123, 456, "The Silver Monastery");
    private sealed record Sheet(string Name, XElement[] Cells, string[]? Merges = null, XElement[]? Links = null, XElement[]? Relationships = null);

    public static void Run()
    {
        CompleteRowLayout();
        CompleteVerticalLayout();
        IndexedAndScopedNames();
        StaleTarget();
        VariantBoundaries();
        CautiousMatching();
        MalformedAndOversized();
        Console.WriteLine("Workbook acquisition: complete layouts, index/scoped names, stale targets, variants, ambiguity and bounded XML/ZIP checks passed.");
    }

    public static void ReadExport(string path)
    {
        var archive = File.ReadAllBytes(path);
        var orbonne = Require(ForetellWorkbookGuide.Read(archive, Duty with { EnglishName = "The Orbonne Monastery" }));
        VerifyPromptCells(orbonne);
        using (var data = JsonDocument.Parse(orbonne.Original))
        {
            var sheet = data.RootElement.GetProperty("Worksheets")[0];
            Check(sheet.GetProperty("Name").GetString() == "SB Ivalice", "Real export resolved Orbonne to a shadowed local defined name.");
            Check(sheet.GetProperty("Cells").EnumerateArray().Any(cell => cell.GetProperty("Text").GetString() == "The Orbonne Monastary"), "Real export lost the misspelled worksheet label.");
            Check(data.RootElement.GetProperty("Candidates").EnumerateArray().Any(candidate => candidate.GetProperty("Location").GetString() == "Orbonne"), "Real export lost the Orbonne index reference.");
        }
        var praetorium = Require(ForetellWorkbookGuide.Read(archive, Duty with { EnglishName = "The Praetorium" }));
        VerifyPromptCells(praetorium);
        using (var data = JsonDocument.Parse(praetorium.Original))
        {
            var candidates = data.RootElement.GetProperty("Candidates").EnumerateArray().ToArray();
            Check(candidates.Any(candidate => candidate.GetProperty("Location").GetString() == "Praetorium" && candidate.GetProperty("TargetRange").GetString() == "A3"), "Real export lost the stale Praetorium target.");
            Check(candidates.Any(candidate => candidate.GetProperty("TargetRange").GetString() == "A5"), "Real export did not reconcile the current Praetorium label.");
        }
        Check(ForetellWorkbookGuide.Read(archive, Duty with { EnglishName = "The Orbonne Monastery (Savage)" }) == null, "Real export invented an unavailable variant.");
        Console.WriteLine($"Read-only workbook export checks passed: Orbonne {orbonne.Text.Length} characters, Praetorium {praetorium.Text.Length} characters.");
    }

    private static void CompleteRowLayout()
    {
        var archive = Workbook([
            new("Horizontal", [Cell("A1", Duty.EnglishName, 1), Cell("B1", "Boss"), Cell("C1", "Mechanic"), Cell("N1", "Response"),
                Cell("B2", "Sentinel"), Cell("C2", "Pulse"), Cell("N2", "Heal after the second hit."), Cell("AA2", "FAR COLUMN FINAL RESPONSE"),
                Cell("A9", "Another duty"), Cell("AB10", "END OF COMPLETE WORKSHEET"), Cell("W7", "", 1),
                new(Spreadsheet + "c", new XAttribute("r", "AF4"), new XElement(Spreadsheet + "f", "SUM(1,2)"), new XElement(Spreadsheet + "v", "3"))],
                ["A1:F1"], [Link("AA2", id: "web")], [Relationship("web", "hyperlink", "https://example.org/strategy", true)]),
            new("Unrelated", [Cell("A1", "UNSELECTED WORKSHEET SECRET")])
        ]);
        var page = Require(ForetellWorkbookGuide.Read(archive, Duty));
        VerifyPromptCells(page);
        Check(page.Provider == "Community workbook" && page.Format == "xlsx"
            && page.Url == "https://docs.google.com/spreadsheets/d/1MX0RjPS4gtT6YI5Szxlsin9hcaohnEQQC7zNdrDHBrQ/edit", "Workbook source contract changed.");
        Check(page.RetrievedAt.Kind == DateTimeKind.Utc && page.Fingerprint.Length > 0, "Workbook provenance is missing.");
        Check(page.Text.Contains("FAR COLUMN FINAL RESPONSE") && page.Text.Contains("END OF COMPLETE WORKSHEET"), "Row layout was cut by duty boundaries or a fixed mechanic column.");
        Check(!page.Original.Contains("UNSELECTED WORKSHEET SECRET"), "Unselected worksheets leaked into the source.");
        using var data = JsonDocument.Parse(page.Original);
        var sheet = data.RootElement.GetProperty("Worksheets")[0];
        var cells = sheet.GetProperty("Cells").EnumerateArray().ToArray();
        Check(cells.Length == 12 && cells.Any(cell => cell.GetProperty("Reference").GetString() == "W7" && cell.GetProperty("Style").GetInt32() == 1), "Styled empty cells were discarded.");
        var far = cells.Single(cell => cell.GetProperty("Reference").GetString() == "AA2");
        Check(far.GetProperty("Column").GetInt32() == 27 && far.GetProperty("Row").GetInt32() == 2, "Cell coordinates were not preserved.");
        Check(cells.Single(cell => cell.GetProperty("Reference").GetString() == "AF4").GetProperty("Formula").GetString() == "SUM(1,2)", "Formula was executed or discarded.");
        Check(sheet.GetProperty("Links")[0].GetProperty("Target").GetString() == "https://example.org/strategy", "External link text was lost.");
        Check(page.Text.Contains("FFCC0000") && page.Text.Contains("font") && sheet.GetProperty("MergedRanges")[0].GetString() == "A1:F1", "Heading style signals or merged ranges are missing.");
        var reread = Require(ForetellWorkbookGuide.Read(archive, Duty));
        Check(page.Fingerprint == reread.Fingerprint && page.Original == reread.Original, "Canonical workbook content varies with retrieval time.");
    }

    private static void CompleteVerticalLayout()
    {
        var rich = new XElement(Spreadsheet + "si",
            new XElement(Spreadsheet + "r", new XElement(Spreadsheet + "rPr", new XElement(Spreadsheet + "b")), new XElement(Spreadsheet + "t", "Spread")),
            new XElement(Spreadsheet + "r", new XElement(Spreadsheet + "t", "\nthen return.")),
            new XElement(Spreadsheet + "rPh", new XAttribute("sb", "0"), new XAttribute("eb", "1"), new XElement(Spreadsheet + "t", "PHONETIC ANNOTATION")));
        var sharedCell = new XElement(Spreadsheet + "c", new XAttribute("r", "C4"), new XAttribute("t", "s"), new XElement(Spreadsheet + "v", 0));
        var inlineRich = new XElement(Spreadsheet + "c", new XAttribute("r", "D4"), new XAttribute("t", "inlineStr"), new XElement(Spreadsheet + "is", rich.Elements().Where(element => element.Name != Spreadsheet + "rPh")));
        var archive = Workbook([new("Vertical", [Cell("B3", Duty.EnglishName, 1), Cell("B4", "Sentinel", 1), sharedCell, inlineRich,
            Cell("B11", "Keeper", 1), Cell("C12", "Second boss response"), Cell("AC100", "LAST VERTICAL MECHANIC")], ["B3:E3", "B4:B10", "B11:B20"])], shared: [rich]);
        var page = Require(ForetellWorkbookGuide.Read(archive, Duty));
        VerifyPromptCells(page);
        using var data = JsonDocument.Parse(page.Original);
        var sheet = data.RootElement.GetProperty("Worksheets")[0];
        Check(sheet.GetProperty("MergedRanges").GetArrayLength() == 3 && page.Text.Contains("LAST VERTICAL MECHANIC") && page.Text.Contains("Second boss response"), "Vertical merged layout was semantically cut.");
        var cells = sheet.GetProperty("Cells").EnumerateArray().ToArray();
        Check(cells.Single(cell => cell.GetProperty("Reference").GetString() == "C4").GetProperty("Text").GetString() == "Spread\nthen return.", "Shared rich text order or line breaks changed.");
        Check(cells.Single(cell => cell.GetProperty("Reference").GetString() == "D4").GetProperty("Text").GetString() == "Spread\nthen return.", "Inline rich text changed.");
        Check(data.RootElement.GetProperty("SharedStrings")[0].GetProperty("Xml").GetString()!.Contains("PHONETIC ANNOTATION") && page.Text.Contains("RichText"), "Original rich string/style context was lost.");
    }

    private static void IndexedAndScopedNames()
    {
        var archive = Workbook([
            new("Index", [Cell("A2", "Silver Monastery"), Cell("B8", "Silver Monastery")], Links: [Link("A2", "Entrance"), Link("B8", "Entrance")]),
            new("Guide's Hall", [Cell("B49", "The Silver Monastary", 1), Cell("F70", "COMPLETE INDEXED NOTES")], ["B49:F49"]),
            new("Other Guide", [Cell("A1", "Different dungeon")])
        ], [Name("Entrance", "'Guide''s Hall'!$B$49:$F$49"), Name("Entrance", "'Other Guide'!$A$1", 2)]);
        var page = Require(ForetellWorkbookGuide.Read(archive, Duty));
        using var data = JsonDocument.Parse(page.Original);
        Check(data.RootElement.GetProperty("Worksheets")[0].GetProperty("Name").GetString() == "Guide's Hall" && page.Text.Contains("COMPLETE INDEXED NOTES"), "Index did not resolve the global name through a quoted sheet name.");
        var candidates = data.RootElement.GetProperty("Candidates").EnumerateArray().ToArray();
        Check(candidates.Count(candidate => candidate.GetProperty("Location").GetString() == "Entrance") == 2, "Duplicate navigation evidence was lost or incorrectly made ambiguous.");
        Check(candidates.Any(candidate => candidate.GetProperty("Match").GetString() == "near"), "Worksheet typo evidence was not retained alongside exact index evidence.");
        var scoped = Workbook([
            new("Index", [Cell("A1", "Silver Monastery")], Links: [Link("A1", "Entry")]),
            new("Correct", [Cell("A1", "Selected notes")]), new("Wrong", [Cell("A1", "Wrong notes")])
        ], [Name("Entry", "'Wrong'!A1"), Name("Entry", "'Correct'!A1", 0)]);
        var scopedPage = Require(ForetellWorkbookGuide.Read(scoped, Duty));
        using var scopedData = JsonDocument.Parse(scopedPage.Original);
        Check(scopedData.RootElement.GetProperty("Worksheets")[0].GetProperty("Name").GetString() == "Correct", "Local defined name did not shadow the global name at the hyperlink's source sheet.");
    }

    private static void StaleTarget()
    {
        var archive = Workbook([
            new("Index", [Cell("A1", Duty.EnglishName)], Links: [Link("A1", "OldPosition")]),
            new("Guide", [Cell("A3", "Previous dungeon"), Cell("A5", Duty.EnglishName), Cell("Z9", "CURRENT COMPLETE NOTES")])
        ], [Name("OldPosition", "'Guide'!$A$3")]);
        var page = Require(ForetellWorkbookGuide.Read(archive, Duty));
        using var data = JsonDocument.Parse(page.Original);
        var references = data.RootElement.GetProperty("Candidates").EnumerateArray().Select(candidate => candidate.GetProperty("TargetRange").GetString()).ToArray();
        Check(references.Contains("A3") && references.Contains("A5") && page.Text.Contains("CURRENT COMPLETE NOTES"), "Stale named target prevented selecting or retaining the current complete worksheet.");
        Check(data.RootElement.GetProperty("NamedTargets")[0].GetProperty("Reference").GetString() == "'Guide'!$A$3", "Original stale named target was rewritten.");
    }

    private static void VariantBoundaries()
    {
        foreach (var variant in new[] { "Hard", "Extreme", "Savage", "Ultimate", "Unreal" })
        {
            var variantDuty = Duty with { EnglishName = Duty.EnglishName + " (" + variant + ")" };
            var qualified = Workbook([new("Guide", [Cell("A1", variantDuty.EnglishName)])]);
            Check(ForetellWorkbookGuide.Read(qualified, Duty) == null, "Variant guide matched the base duty: " + variant);
            Check(ForetellWorkbookGuide.Read(Workbook([new("Guide", [Cell("A1", Duty.EnglishName)])]), variantDuty) == null, "Base duty guide matched an unavailable variant: " + variant);
            Check(ForetellWorkbookGuide.Read(qualified, variantDuty) != null, "Exact variant failed to match: " + variant);
        }
        var extreme = Workbook([new("EX Trials", [Cell("A1", Duty.EnglishName)])]);
        Check(ForetellWorkbookGuide.Read(extreme, Duty) == null, "Unqualified title in an extreme worksheet matched a normal duty.");
        Check(ForetellWorkbookGuide.Read(extreme, Duty with { EnglishName = Duty.EnglishName + " (Extreme)" }) != null, "Worksheet variant context was ignored.");
        var indexedExtreme = Workbook([
            new("Index", [Cell("A1", Duty.EnglishName)], Links: [Link("A1", "Entry")]), new("Extreme Trials", [Cell("A1", "Mechanic notes")])
        ], [Name("Entry", "'Extreme Trials'!A1")]);
        Check(ForetellWorkbookGuide.Read(indexedExtreme, Duty) == null, "Index label bypassed conflicting target worksheet variant.");
        var wrongTarget = Workbook([
            new("Index", [Cell("A1", Duty.EnglishName)], Links: [Link("A1", "Entry")]), new("Mixed Trials", [Cell("A1", Duty.EnglishName + " (Hard)")])
        ], [Name("Entry", "'Mixed Trials'!A1")]);
        Check(ForetellWorkbookGuide.Read(wrongTarget, Duty) == null, "Index label bypassed a conflicting target-cell variant.");
        var hard = Workbook([new("Guide", [Cell("A1", "Silver Monastery (HM)")])]);
        Check(ForetellWorkbookGuide.Read(hard, Duty with { EnglishName = Duty.EnglishName + " (Hard)" }) != null, "Generic HM variant spelling did not match Hard.");
    }

    private static void CautiousMatching()
    {
        Check(ForetellWorkbookGuide.Read(Workbook([new("Guide", [Cell("A1", "The Silver Monastary")])]), Duty) != null, "A unique conservative typo was rejected.");
        var saint = Duty with { EnglishName = "Saint Azure's Arboretum" };
        Check(ForetellWorkbookGuide.Read(Workbook([new("Guide", [Cell("A1", "St. Azure’s Arboreteum")])]), saint) != null, "Generic saint/apostrophe normalization and a single typo failed.");
        Check(ForetellWorkbookGuide.Read(Workbook([new("Guide", [Cell("A1", "Silver Monastary"), Cell("A4", "Silver Monasterx")])]), Duty) == null, "Two near names were guessed even though ambiguous.");
        Check(ForetellWorkbookGuide.Read(Workbook([new("First", [Cell("A1", Duty.EnglishName)]), new("Second", [Cell("A1", Duty.EnglishName)])]), Duty) == null, "Conflicting exact worksheet candidates were guessed.");
        Check(ForetellWorkbookGuide.Read(Workbook([new("Guide", [Cell("A1", "Silver Monastery Annex")])]), Duty) == null, "Substring title matching accepted a different duty.");
        Check(ForetellWorkbookGuide.Read(Workbook([new("Guide", [Cell("A1", "Chamber 12")])]), Duty with { EnglishName = "Chamber 13" }) == null, "Numeric duty distinction was fuzzy matched.");
        var cycle = Workbook([new("Index", [Cell("A1", Duty.EnglishName)], Links: [Link("A1", "Loop")])], [Name("Loop", "Loop")]);
        Check(ForetellWorkbookGuide.Read(cycle, Duty) == null, "Cyclic named reference became a guide or did not terminate.");
        var missing = Workbook([new("Index", [Cell("A1", Duty.EnglishName)], Links: [Link("A1", "Missing")])]);
        Check(ForetellWorkbookGuide.Read(missing, Duty) == null, "An unresolved navigation entry became a standalone guide worksheet.");
    }

    private static void MalformedAndOversized()
    {
        Reject(() => ForetellWorkbookGuide.Read([1, 2, 3], Duty), "Malformed ZIP accepted.");
        Reject(() => ForetellWorkbookGuide.Read(new byte[ForetellWorkbookGuide.MaximumArchiveBytes + 1], Duty), "Oversized archive accepted.");
        var normal = Workbook([new("Guide", [Cell("A1", Duty.EnglishName)])]);
        Reject(() => ForetellWorkbookGuide.Read(ReplaceEntry(normal, "xl/workbook.xml", "<broken>"), Duty), "Malformed workbook XML accepted.");
        var entity = "<!DOCTYPE workbook [<!ENTITY outside SYSTEM 'file:///unavailable-entity'>]><workbook xmlns='" + Spreadsheet + "'>&outside;</workbook>";
        Reject(() => ForetellWorkbookGuide.Read(ReplaceEntry(normal, "xl/workbook.xml", entity), Duty), "External XML entity accepted.");
        var deep = "<workbook xmlns='" + Spreadsheet + "'>" + string.Concat(Enumerable.Repeat("<nested>", 70)) + string.Concat(Enumerable.Repeat("</nested>", 70)) + "</workbook>";
        Reject(() => ForetellWorkbookGuide.Read(ReplaceEntry(normal, "xl/workbook.xml", deep), Duty), "Excessive XML depth accepted.");
        Reject(() => ForetellWorkbookGuide.Read(ReplaceEntry(normal, "xl/oversized.xml", new string('x', ForetellWorkbookGuide.MaximumPartBytes + 1)), Duty), "Oversized expanded ZIP part accepted.");
        Reject(() => ForetellWorkbookGuide.Read(ReplaceEntry(normal, "../escaped.xml", "<root/>"), Duty), "ZIP traversal entry accepted.");
        var traversal = new XDocument(new XElement(PackageRelationships + "Relationships", Relationship("sheet1", "worksheet", "../../../escape.xml"))).ToString();
        Reject(() => ForetellWorkbookGuide.Read(ReplaceEntry(normal, "xl/_rels/workbook.xml.rels", traversal), Duty), "Relationship escaped the package root.");
        var shared = new XElement(Spreadsheet + "c", new XAttribute("r", "A1"), new XAttribute("t", "s"), new XElement(Spreadsheet + "v", 42));
        Reject(() => ForetellWorkbookGuide.Read(Workbook([new("Guide", [shared])]), Duty), "Out-of-range shared string accepted.");
        var count = Enumerable.Range(0, 129).Select(index => new Sheet("Sheet" + index, [])).ToArray();
        Reject(() => ForetellWorkbookGuide.Read(Workbook(count), Duty), "Excessive worksheet count accepted.");
        Check(ForetellWorkbookGuide.Read(normal, Duty with { ContentID = 0 }) == null, "Invalid duty was selected.");
    }

    private static GuideSourcePage Require(GuideSourcePage? page) => page ?? throw new InvalidOperationException("Expected a unique workbook guide source.");
    private static void VerifyPromptCells(GuideSourcePage page)
    {
        using var original = JsonDocument.Parse(page.Original);
        var expected = original.RootElement.GetProperty("Worksheets")[0].GetProperty("Cells").EnumerateArray()
            .ToDictionary(cell => cell.GetProperty("Reference").GetString()!, cell => cell.GetProperty("Text").GetString()!);
        Dictionary<string, string> actual = [];
        foreach (var line in page.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            using var document = JsonDocument.Parse(line);
            var element = document.RootElement;
            if (element.ValueKind == JsonValueKind.Array)
                actual.Add(element[0].GetString()!, element[2].GetString()!);
            else if (element.TryGetProperty("EmptyCells", out var emptyGroups))
                foreach (var group in emptyGroups.EnumerateArray())
                    foreach (var range in group.GetProperty("Ranges").EnumerateArray())
                    {
                        var ends = range.GetString()!.Split(':');
                        var first = Position(ends[0]);
                        var last = Position(ends[^1]);
                        for (var row = first.Row; row <= last.Row; ++row)
                            for (var column = first.Column; column <= last.Column; ++column)
                            {
                                var remaining = column;
                                var letters = "";
                                while (remaining > 0) { letters = (char)('A' + (remaining - 1) % 26) + letters; remaining = (remaining - 1) / 26; }
                                actual.Add(letters + row, "");
                            }
                    }
        }
        Check(actual.Count == expected.Count && expected.All(cell => actual.TryGetValue(cell.Key, out var content) && content == cell.Value), "Compact prompt lost, duplicated, invented, or changed worksheet cells.");

        static (int Row, int Column) Position(string reference)
        {
            var column = reference.TakeWhile(char.IsLetter).Aggregate(0, (value, letter) => value * 26 + letter - 'A' + 1);
            var row = int.Parse(new string(reference.SkipWhile(char.IsLetter).ToArray()));
            return (row, column);
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static XElement Cell(string reference, string text, int style = 0) => new(Spreadsheet + "c", new XAttribute("r", reference), new XAttribute("s", style),
        new XAttribute("t", "inlineStr"), new XElement(Spreadsheet + "is", new XElement(Spreadsheet + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text)));
    private static XElement Link(string reference, string? location = null, string? id = null) => new(Spreadsheet + "hyperlink", new XAttribute("ref", reference),
        location == null ? null : new XAttribute("location", location), id == null ? null : new XAttribute(DocumentRelationships + "id", id));
    private static XElement Name(string name, string reference, int? scope = null) => new(Spreadsheet + "definedName", new XAttribute("name", name),
        scope == null ? null : new XAttribute("localSheetId", scope.Value), reference);
    private static XElement Relationship(string id, string kind, string target, bool external = false) => new(PackageRelationships + "Relationship", new XAttribute("Id", id),
        new XAttribute("Type", DocumentRelationships.NamespaceName + "/" + kind), new XAttribute("Target", target), external ? new XAttribute("TargetMode", "External") : null);

    private static byte[] Workbook(Sheet[] sheets, XElement[]? names = null, XElement[]? shared = null)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            Write(archive, "_rels/.rels", new XElement(PackageRelationships + "Relationships", Relationship("workbook", "officeDocument", "xl/workbook.xml")).ToString());
            Write(archive, "xl/workbook.xml", new XElement(Spreadsheet + "workbook", new XElement(Spreadsheet + "sheets", sheets.Select((sheet, index) =>
                new XElement(Spreadsheet + "sheet", new XAttribute("name", sheet.Name), new XAttribute("sheetId", index + 1), new XAttribute(DocumentRelationships + "id", "sheet" + (index + 1))))),
                new XElement(Spreadsheet + "definedNames", names ?? [])).ToString());
            Write(archive, "xl/_rels/workbook.xml.rels", new XElement(PackageRelationships + "Relationships",
                sheets.Select((sheet, index) => Relationship("sheet" + (index + 1), "worksheet", "worksheets/sheet" + (index + 1) + ".xml")),
                Relationship("styles", "styles", "styles.xml"), shared == null ? null : Relationship("strings", "sharedStrings", "sharedStrings.xml")).ToString());
            Write(archive, "xl/styles.xml", "<styleSheet xmlns='" + Spreadsheet + "'><fonts count='2'><font/><font><b/><sz val='16'/><color rgb='FFCC0000'/></font></fonts><fills><fill><patternFill patternType='solid'/></fill></fills><borders><border/></borders><cellXfs count='2'><xf fontId='0'/><xf fontId='1' fillId='0'><alignment wrapText='1'/></xf></cellXfs></styleSheet>");
            if (shared != null) Write(archive, "xl/sharedStrings.xml", new XElement(Spreadsheet + "sst", shared).ToString());
            for (var index = 0; index < sheets.Length; ++index)
            {
                var sheet = sheets[index];
                var rows = sheet.Cells.GroupBy(cell => int.Parse(new string(cell.Attribute("r")!.Value.Where(char.IsDigit).ToArray()))).OrderBy(group => group.Key)
                    .Select(group => new XElement(Spreadsheet + "row", new XAttribute("r", group.Key), new XAttribute("ht", "24"), group));
                Write(archive, "xl/worksheets/sheet" + (index + 1) + ".xml", new XElement(Spreadsheet + "worksheet",
                    new XElement(Spreadsheet + "cols", new XElement(Spreadsheet + "col", new XAttribute("min", 1), new XAttribute("max", 30), new XAttribute("width", "20"))),
                    new XElement(Spreadsheet + "sheetData", rows), new XElement(Spreadsheet + "mergeCells", (sheet.Merges ?? []).Select(range => new XElement(Spreadsheet + "mergeCell", new XAttribute("ref", range)))),
                    new XElement(Spreadsheet + "hyperlinks", sheet.Links ?? [])).ToString());
                if (sheet.Relationships != null) Write(archive, "xl/worksheets/_rels/sheet" + (index + 1) + ".xml.rels", new XElement(PackageRelationships + "Relationships", sheet.Relationships).ToString());
            }
        }
        return memory.ToArray();
    }

    private static byte[] ReplaceEntry(byte[] workbook, string path, string text)
    {
        using var memory = new MemoryStream();
        memory.Write(workbook);
        memory.Position = 0;
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Update, true))
        {
            archive.GetEntry(path)?.Delete();
            Write(archive, path, text);
        }
        return memory.ToArray();
    }

    private static void Write(ZipArchive archive, string path, string text)
    {
        using var stream = archive.CreateEntry(path, CompressionLevel.Fastest).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }
}
