using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace BossMod.Foretell;

internal static class ForetellWorkbookGuide
{
    internal const int MaximumArchiveBytes = 8 * 1024 * 1024;
    internal const int MaximumPartBytes = 16 * 1024 * 1024;
    private const int MaximumEntries = 1024;
    private const int MaximumOutputBytes = 8 * 1024 * 1024;
    private const string WorkbookUrl = "https://docs.google.com/spreadsheets/d/1MX0RjPS4gtT6YI5Szxlsin9hcaohnEQQC7zNdrDHBrQ/edit";
    private static readonly XNamespace Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace StrictSpreadsheet = "http://purl.oclc.org/ooxml/spreadsheetml/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace StrictRelationships = "http://purl.oclc.org/ooxml/officeDocument/relationships";
    private static readonly JsonSerializerOptions PromptJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record Relationship(string ID, string Type, string Target, bool External);
    private sealed record NamedTarget(string Name, int? LocalSheetID, string Reference);
    private sealed record Cell(string Reference, int Row, int Column, int Style, string Type, string Value, string Text, string Formula, int? SharedString);
    private sealed record Link(string Reference, string Display, string Location, string Target, bool External, string Tooltip);
    private sealed record Sheet(int Index, string Name, string State, string Part, XElement Xml, Cell[] Cells, Link[] Links, string[] MergedRanges);
    private sealed record Target(Sheet Sheet, string Reference);
    private sealed record Label(string Stem, string Variant);
    private sealed record Candidate(string Label, string Match, string SourceSheet, string SourceCell, string Location, string TargetSheet, string TargetRange);

    public static GuideSourcePage? Read(byte[] archive, GuideDuty duty)
    {
        if (!duty.Valid) return null;
        if (archive.Length == 0 || archive.Length > MaximumArchiveBytes) throw new InvalidDataException("Workbook archive exceeds size limits or is empty.");
        try
        {
            using var input = new MemoryStream(archive, false);
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            return Read(new Package(zip), duty);
        }
        catch (Exception error) when (error is XmlException or ArgumentException or OverflowException or NotSupportedException)
        {
            throw new InvalidDataException("Workbook package is malformed or unsupported.", error);
        }
    }

    private static GuideSourcePage? Read(Package package, GuideDuty duty)
    {
        var rootRelationships = package.ReadRelationships("");
        var workbookPart = RelatedPart(rootRelationships, "officeDocument") ?? "xl/workbook.xml";
        var workbook = package.ReadXml(workbookPart).Root ?? throw new InvalidDataException("Workbook is empty.");
        var spreadsheet = workbook.Name.Namespace;
        if (workbook.Name.LocalName != "workbook" || spreadsheet != Spreadsheet && spreadsheet != StrictSpreadsheet)
            throw new InvalidDataException("Unsupported workbook XML.");
        var relationships = package.ReadRelationships(workbookPart);
        var sharedPart = RelatedPart(relationships, "sharedStrings");
        var shared = sharedPart == null ? [] : package.ReadXml(sharedPart).Root!.Elements(spreadsheet + "si").ToArray();
        if (shared.Length > 100000) throw new InvalidDataException("Workbook has too many shared strings.");
        var stylesPart = RelatedPart(relationships, "styles");
        var styles = stylesPart == null ? null : package.ReadXml(stylesPart).Root;
        var styleCount = styles?.Element(spreadsheet + "cellXfs")?.Elements().Count() ?? 0;
        if (styleCount > 8192) throw new InvalidDataException("Workbook has too many cell styles.");
        var sheetNodes = workbook.Element(spreadsheet + "sheets")?.Elements(spreadsheet + "sheet").ToArray() ?? [];
        if (sheetNodes.Length is 0 or > 128) throw new InvalidDataException("Workbook sheet count is invalid.");
        var names = (workbook.Element(spreadsheet + "definedNames")?.Elements(spreadsheet + "definedName") ?? [])
            .Select(element => new NamedTarget(Attribute(element, "name"), OptionalInteger(element, "localSheetId"), element.Value)).ToArray();
        if (names.Length > 4096 || names.Any(name => name.LocalSheetID < 0 || name.LocalSheetID >= sheetNodes.Length)
            || names.GroupBy(name => (name.Name.ToUpperInvariant(), name.LocalSheetID)).Any(group => group.Count() != 1))
            throw new InvalidDataException("Workbook defined names are invalid or excessive.");
        List<Sheet> sheets = [];
        var cellBudget = 200000;
        for (var index = 0; index < sheetNodes.Length; ++index)
        {
            var node = sheetNodes[index];
            var relationship = relationships.SingleOrDefault(relationship => relationship.ID == RelationshipID(node));
            if (relationship == null || relationship.External || !relationship.Type.EndsWith("/worksheet", StringComparison.Ordinal))
                throw new InvalidDataException("Workbook sheet relationship is invalid.");
            var xml = package.ReadXml(relationship.Target).Root ?? throw new InvalidDataException("Worksheet is empty.");
            if (xml.Name != spreadsheet + "worksheet") throw new InvalidDataException("Worksheet namespace is invalid.");
            var cells = ReadCells(xml, spreadsheet, shared, styleCount, ref cellBudget);
            var sheetRelationships = package.ReadRelationships(relationship.Target);
            var links = (xml.Element(spreadsheet + "hyperlinks")?.Elements(spreadsheet + "hyperlink") ?? []).Select(element =>
            {
                var linkRelationship = sheetRelationships.SingleOrDefault(relationship => relationship.ID == RelationshipID(element));
                return new Link(Attribute(element, "ref"), Attribute(element, "display"), Attribute(element, "location"),
                    linkRelationship?.Target ?? "", linkRelationship?.External ?? false, Attribute(element, "tooltip"));
            }).ToArray();
            var merges = (xml.Element(spreadsheet + "mergeCells")?.Elements(spreadsheet + "mergeCell") ?? []).Select(element => Attribute(element, "ref")).ToArray();
            if (links.Length > 20000 || merges.Length > 20000 || links.Any(link => !TryRange(link.Reference, out _, out _))
                || merges.Any(merge => !TryRange(merge, out _, out _))) throw new InvalidDataException("Worksheet ranges are invalid or excessive.");
            sheets.Add(new(index, Attribute(node, "name"), Attribute(node, "state"), relationship.Target, xml, cells, links, merges));
        }
        if (sheets.Any(sheet => sheet.Name.Length == 0) || sheets.Select(sheet => sheet.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sheets.Count)
            throw new InvalidDataException("Workbook sheet names are invalid.");
        var requested = Normalize(duty.EnglishName);
        if (requested.Stem.Length == 0) return null;
        var candidates = FindCandidates(sheets, names, requested);
        var strongest = candidates.Where(candidate => candidate.Match == "exact").ToArray();
        if (strongest.Length == 0)
        {
            strongest = candidates.ToArray();
            if (strongest.Select(candidate => Normalize(candidate.Label).Stem).Distinct(StringComparer.Ordinal).Count() != 1) return null;
        }
        var selectedNames = strongest.Select(candidate => candidate.TargetSheet).Distinct(StringComparer.Ordinal).ToArray();
        if (selectedNames.Length != 1) return null;
        var selected = sheets.Single(sheet => sheet.Name == selectedNames[0]);
        var references = candidates.Where(candidate => candidate.TargetSheet == selected.Name).Distinct().ToArray();
        var selectedNamesData = names.Where(name => Resolve(name.Reference, name.LocalSheetID ?? selected.Index, sheets, names, 0)?.Sheet == selected).ToArray();
        var usedStrings = selected.Cells.Where(cell => cell.SharedString != null).Select(cell => cell.SharedString!.Value).Distinct().Order().ToArray();
        var rows = (selected.Xml.Element(spreadsheet + "sheetData")?.Elements(spreadsheet + "row") ?? [])
            .Select(row => row.Attributes().ToDictionary(attribute => attribute.Name.LocalName, attribute => attribute.Value)).ToArray();
        var columns = selected.Xml.Element(spreadsheet + "cols")?.ToString(SaveOptions.DisableFormatting) ?? "";
        var original = JsonSerializer.Serialize(new
        {
            Schema = 1, Duty = duty.EnglishName, Candidates = references, NamedTargets = selectedNamesData,
            Worksheets = new[] { new { selected.Name, selected.State, selected.Part, selected.Cells, selected.Links, selected.MergedRanges, Rows = rows, Columns = columns, Xml = selected.Xml.ToString(SaveOptions.DisableFormatting) } },
            SharedStrings = usedStrings.Select(index => new { Index = index, Text = RichText(shared[index], spreadsheet), Xml = shared[index].ToString(SaveOptions.DisableFormatting) }),
            Styles = styles?.ToString(SaveOptions.DisableFormatting) ?? "",
            Theme = ReadRelatedXml(package, relationships, "theme")
        });
        var text = new StringBuilder();
        text.AppendLine("Community workbook. Complete selected worksheet; candidate references are navigation evidence and may point to stale cells. Reconcile them with the worksheet labels, merged ranges, and style signals. Keep the requested duty variant.");
        text.AppendLine(JsonSerializer.Serialize(new { Duty = duty.EnglishName, Candidates = references, NamedTargets = selectedNamesData }, PromptJson));
        var styleMap = new Dictionary<int, int>();
        var styleKeys = new List<string>();
        foreach (var style in selected.Cells.Select(cell => cell.Style).Distinct().Order())
        {
            var key = JsonSerializer.Serialize(StyleSignals(styles, spreadsheet, style), PromptJson);
            var compactIndex = styleKeys.IndexOf(key);
            if (compactIndex < 0) { compactIndex = styleKeys.Count; styleKeys.Add(key); }
            styleMap.Add(style, compactIndex);
        }
        text.AppendLine(JsonSerializer.Serialize(new
        {
            Worksheet = selected.Name, selected.State, selected.MergedRanges, selected.Links,
            HiddenRows = rows.Where(row => row.TryGetValue("hidden", out var hidden) && hidden == "1").Select(row => row.GetValueOrDefault("r")),
            Columns = selected.Xml.Element(spreadsheet + "cols")?.Elements().Select(column => column.Attributes().ToDictionary(attribute => attribute.Name.LocalName, attribute => attribute.Value)).ToArray() ?? [],
            Styles = styleKeys.Select(key => JsonSerializer.Deserialize<JsonElement>(key)).ToArray(),
            EmptyCells = EmptyRanges(selected.Cells, styleMap),
            CellFields = new[] { "coordinate", "visualStyle", "text", "formula (if present)" }
        }, PromptJson));
        foreach (var cell in selected.Cells.Where(cell => cell.Text.Length > 0 || cell.Formula.Length > 0))
        {
            object[] values = cell.Formula.Length > 0 ? [cell.Reference, styleMap[cell.Style], cell.Text, cell.Formula]
                : [cell.Reference, styleMap[cell.Style], cell.Text];
            text.AppendLine(JsonSerializer.Serialize(values, PromptJson));
        }
        foreach (var index in usedStrings.Where(index => shared[index].Elements(spreadsheet + "r").Any()))
            text.AppendLine(JsonSerializer.Serialize(new { RichTextCells = selected.Cells.Where(cell => cell.SharedString == index).Select(cell => cell.Reference), Runs = RichSignals(shared[index], spreadsheet) }, PromptJson));
        foreach (var cell in selected.Xml.Descendants(spreadsheet + "c").Where(cell => cell.Element(spreadsheet + "is")?.Elements(spreadsheet + "r").Any() == true))
            text.AppendLine(JsonSerializer.Serialize(new { RichTextCells = new[] { Attribute(cell, "r") }, Runs = RichSignals(cell.Element(spreadsheet + "is")!, spreadsheet) }, PromptJson));
        foreach (var formatting in selected.Xml.Elements(spreadsheet + "conditionalFormatting"))
            text.AppendLine(JsonSerializer.Serialize(new { ConditionalFormatting = formatting.ToString(SaveOptions.DisableFormatting) }));
        if (Encoding.UTF8.GetByteCount(original) > MaximumOutputBytes || Encoding.UTF8.GetByteCount(text.ToString()) > MaximumOutputBytes)
            throw new InvalidDataException("Complete workbook worksheet exceeds source size limits.");
        return new("Community workbook", WorkbookUrl, text.ToString(), original, "xlsx") { RetrievedAt = DateTime.UtcNow };
    }

    private static Cell[] ReadCells(XElement sheet, XNamespace spreadsheet, XElement[] shared, int styleCount, ref int budget)
    {
        List<Cell> cells = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        var previousRow = 0;
        foreach (var row in sheet.Element(spreadsheet + "sheetData")?.Elements(spreadsheet + "row") ?? [])
        {
            var rowNumber = OptionalInteger(row, "r") ?? previousRow + 1;
            if (rowNumber <= previousRow || rowNumber > 1048576) throw new InvalidDataException("Worksheet row is invalid.");
            previousRow = rowNumber;
            var previousColumn = 0;
            foreach (var cell in row.Elements(spreadsheet + "c"))
            {
                if (--budget < 0) throw new InvalidDataException("Workbook has too many cells.");
                var reference = Attribute(cell, "r");
                if (reference.Length == 0) reference = Coordinate(previousColumn + 1, rowNumber);
                if (!TryCell(reference, out var position) || position.Row != rowNumber || !seen.Add(reference))
                    throw new InvalidDataException("Worksheet cell coordinate is invalid or duplicated.");
                previousColumn = position.Column;
                var type = Attribute(cell, "t");
                var value = cell.Element(spreadsheet + "v")?.Value ?? "";
                int? sharedIndex = null;
                var content = value;
                if (type == "s")
                {
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index >= shared.Length)
                        throw new InvalidDataException("Worksheet shared string is invalid.");
                    sharedIndex = index;
                    content = RichText(shared[index], spreadsheet);
                }
                else if (type == "inlineStr") content = cell.Element(spreadsheet + "is") is { } inline ? RichText(inline, spreadsheet) : "";
                var style = OptionalInteger(cell, "s") ?? 0;
                if (style < 0 || style >= Math.Max(1, styleCount)) throw new InvalidDataException("Worksheet cell style is invalid.");
                cells.Add(new(reference, position.Row, position.Column, style, type, value, content, cell.Element(spreadsheet + "f")?.Value ?? "", sharedIndex));
            }
        }
        return cells.OrderBy(cell => cell.Row).ThenBy(cell => cell.Column).ToArray();
    }

    private static List<Candidate> FindCandidates(List<Sheet> sheets, NamedTarget[] names, Label requested)
    {
        List<Candidate> candidates = [];
        foreach (var sheet in sheets)
        {
            foreach (var cell in sheet.Cells)
            {
                if (cell.Text.Length is 0 or > 240) continue;
                var normalized = Normalize(cell.Text);
                var distance = MatchDistance(requested.Stem, normalized.Stem);
                var links = sheet.Links.Where(link => Contains(link.Reference, cell)).ToArray();
                foreach (var link in links)
                {
                    if (link.External || link.Location.Length == 0) continue;
                    var target = Resolve(link.Location, sheet.Index, sheets, names, 0);
                    if (target == null) continue;
                    Add(cell.Text, normalized, distance, link.Location, target);
                    if (link.Display.Length is > 0 and <= 240 && link.Display != cell.Text)
                    {
                        var display = Normalize(link.Display);
                        Add(link.Display, display, MatchDistance(requested.Stem, display.Stem), link.Location, target);
                    }
                }
                if (!links.Any(link => !link.External && link.Location.Length > 0)) Add(cell.Text, normalized, distance, "", new(sheet, cell.Reference));

                void Add(string label, Label normalizedLabel, int difference, string location, Target target)
                {
                    if (difference < 0) return;
                    var sheetVariant = Normalize(target.Sheet.Name).Variant;
                    if (normalizedLabel.Variant.Length > 0 && sheetVariant.Length > 0 && normalizedLabel.Variant != sheetVariant) return;
                    var variant = normalizedLabel.Variant.Length > 0 ? normalizedLabel.Variant : sheetVariant;
                    if (variant != requested.Variant) return;
                    foreach (var targetCell in target.Sheet.Cells.Where(targetCell => targetCell.Text.Length is > 0 and <= 240 && Contains(target.Reference, targetCell)))
                    {
                        var targetLabel = Normalize(targetCell.Text);
                        if (targetLabel.Variant.Length > 0 && targetLabel.Variant != requested.Variant && MatchDistance(requested.Stem, targetLabel.Stem) >= 0) return;
                    }
                    candidates.Add(new(label, difference == 0 ? "exact" : "near", sheet.Name, cell.Reference, location, target.Sheet.Name, target.Reference));
                }
            }
        }
        return candidates;
    }

    private static Target? Resolve(string location, int scope, List<Sheet> sheets, NamedTarget[] names, int depth)
    {
        if (depth > 16 || location.Length > 1024) return null;
        location = location.Trim().TrimStart('#', '=');
        var separator = location.LastIndexOf('!');
        if (separator >= 0)
        {
            var sheetName = location[..separator];
            if (sheetName.StartsWith('\'') && sheetName.EndsWith('\'')) sheetName = sheetName[1..^1].Replace("''", "'", StringComparison.Ordinal);
            var sheet = sheets.SingleOrDefault(sheet => sheet.Name.Equals(sheetName, StringComparison.OrdinalIgnoreCase));
            if (sheet == null) return null;
            var reference = location[(separator + 1)..].Replace("$", "", StringComparison.Ordinal).ToUpperInvariant();
            if (TryRange(reference, out _, out _)) return new(sheet, reference);
            return Resolve(location[(separator + 1)..], sheet.Index, sheets, names, depth + 1);
        }
        var named = names.SingleOrDefault(name => name.LocalSheetID == scope && name.Name.Equals(location, StringComparison.OrdinalIgnoreCase))
            ?? names.SingleOrDefault(name => name.LocalSheetID == null && name.Name.Equals(location, StringComparison.OrdinalIgnoreCase));
        if (named != null) return Resolve(named.Reference, named.LocalSheetID ?? scope, sheets, names, depth + 1);
        var localReference = location.Replace("$", "", StringComparison.Ordinal).ToUpperInvariant();
        return TryRange(localReference, out _, out _) ? new(sheets[scope], localReference) : null;
    }

    private static Label Normalize(string text)
    {
        var cleaned = new StringBuilder();
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (character is '\'' or '\u2019' or '\u2018') continue;
            cleaned.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }
        var words = cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word => word == "st" ? "saint" : word).ToList();
        if (words.Count > 0 && words[0] == "the") words.RemoveAt(0);
        var variants = words.Select(Variant).Where(variant => variant.Length > 0).Distinct().Order(StringComparer.Ordinal).ToArray();
        return new(string.Join(' ', words.Where(word => Variant(word).Length == 0)), string.Join('+', variants));
    }

    private static string Variant(string word) => word switch
    {
        "hard" or "hm" => "hard",
        "extreme" or "ex" => "extreme",
        "savage" => "savage",
        "ultimate" => "ultimate",
        "unreal" => "unreal",
        "normal" => "normal",
        _ => ""
    };

    private static int MatchDistance(string requested, string candidate)
    {
        if (requested == candidate) return 0;
        if (requested.Length < 8 || candidate.Length < 8 || Math.Abs(requested.Length - candidate.Length) > 1) return -1;
        var wanted = requested.Split(' ');
        var offered = candidate.Split(' ');
        if (wanted.Length != offered.Length) return -1;
        var differences = 0;
        for (var index = 0; index < wanted.Length; ++index)
        {
            if (wanted[index] == offered[index]) continue;
            if (++differences > 1 || wanted[index].Length < 5 || offered[index].Length < 5
                || wanted[index].Any(char.IsDigit) || offered[index].Any(char.IsDigit) || !OneEdit(wanted[index], offered[index])) return -1;
        }
        return 1;
    }

    private static bool OneEdit(string wanted, string offered)
    {
        var common = 0;
        while (common < Math.Min(wanted.Length, offered.Length) && wanted[common] == offered[common]) ++common;
        if (wanted.Length == offered.Length)
            return wanted.AsSpan(common + 1).SequenceEqual(offered.AsSpan(common + 1))
                || common + 1 < wanted.Length && wanted[common] == offered[common + 1] && wanted[common + 1] == offered[common]
                && wanted.AsSpan(common + 2).SequenceEqual(offered.AsSpan(common + 2));
        return wanted.Length > offered.Length
            ? wanted.AsSpan(common + 1).SequenceEqual(offered.AsSpan(common))
            : wanted.AsSpan(common).SequenceEqual(offered.AsSpan(common + 1));
    }

    private static Dictionary<string, string> StyleSignals(XElement? styles, XNamespace spreadsheet, int index)
    {
        var format = ElementAt("cellXfs", index);
        var baseStyle = ElementAt("cellStyleXfs", OptionalInteger(format, "xfId") ?? 0);
        var signals = FontSignals(ElementAt("fonts", OptionalInteger(format, "fontId") ?? OptionalInteger(baseStyle, "fontId") ?? 0));
        var fill = ElementAt("fills", OptionalInteger(format, "fillId") ?? OptionalInteger(baseStyle, "fillId") ?? 0);
        foreach (var color in fill?.Descendants(spreadsheet + "fgColor") ?? []) signals["fill"] = Color(color);
        var alignment = format?.Element(spreadsheet + "alignment") ?? baseStyle?.Element(spreadsheet + "alignment");
        foreach (var attribute in alignment?.Attributes() ?? [])
            if (attribute.Name.LocalName is "horizontal" or "vertical" or "textRotation" or "indent") signals[attribute.Name.LocalName] = attribute.Value;
        return signals;

        XElement? ElementAt(string group, int elementIndex) => elementIndex < 0 ? null : styles?.Element(spreadsheet + group)?.Elements().ElementAtOrDefault(elementIndex);
    }

    private static object[] EmptyRanges(Cell[] cells, Dictionary<int, int> styles)
    {
        List<(int Style, int Row, int Start, int End)> spans = [];
        foreach (var cell in cells.Where(cell => cell.Text.Length == 0 && cell.Formula.Length == 0))
        {
            var style = styles[cell.Style];
            if (spans.Count > 0 && spans[^1] is var previous && previous.Style == style && previous.Row == cell.Row && previous.End + 1 == cell.Column)
                spans[^1] = previous with { End = cell.Column };
            else spans.Add((style, cell.Row, cell.Column, cell.Column));
        }
        List<(int Style, string Reference)> ranges = [];
        foreach (var group in spans.GroupBy(span => (span.Style, span.Start, span.End)))
        {
            var firstRow = group.First().Row;
            var lastRow = firstRow;
            foreach (var span in group.Skip(1))
            {
                if (span.Row != lastRow + 1) { AddRange(); firstRow = span.Row; }
                lastRow = span.Row;
            }
            AddRange();

            void AddRange()
            {
                var start = Coordinate(group.Key.Start, firstRow);
                var end = Coordinate(group.Key.End, lastRow);
                ranges.Add((group.Key.Style, start == end ? start : start + ":" + end));
            }
        }
        return ranges.GroupBy(range => range.Style).OrderBy(group => group.Key)
            .Select(group => (object)new { Style = group.Key, Ranges = group.Select(range => range.Reference).ToArray() }).ToArray();
    }

    private static Dictionary<string, string> FontSignals(XElement? font)
    {
        Dictionary<string, string> signals = [];
        foreach (var element in font?.Elements() ?? [])
        {
            var name = element.Name.LocalName;
            if (name == "color") signals["fontColor"] = Color(element);
            else if (name is "b" or "i" or "u" or "strike" or "sz" or "vertAlign") signals[name] = element.Attribute("val")?.Value ?? "1";
        }
        return signals;
    }

    private static string Color(XElement element) => string.Join(',', element.Attributes().Select(attribute => attribute.Name.LocalName + ":" + attribute.Value));

    private static object[] RichSignals(XElement rich, XNamespace spreadsheet)
    {
        List<object> runs = [];
        var offset = 0;
        foreach (var element in rich.Elements())
        {
            var content = element.Name == spreadsheet + "t" ? element.Value : element.Name == spreadsheet + "r" ? element.Element(spreadsheet + "t")?.Value ?? "" : "";
            var signals = FontSignals(element.Element(spreadsheet + "rPr"));
            if (signals.Count > 0) runs.Add(new { Offset = offset, Length = content.Length, Style = signals });
            offset += content.Length;
        }
        return runs.ToArray();
    }

    private static bool Contains(string range, Cell cell) => TryRange(range, out var first, out var last)
        && cell.Row >= first.Row && cell.Row <= last.Row && cell.Column >= first.Column && cell.Column <= last.Column;

    private static bool TryRange(string reference, out (int Row, int Column) first, out (int Row, int Column) last)
    {
        first = last = default;
        var parts = reference.Replace("$", "", StringComparison.Ordinal).Split(':');
        return parts.Length is 1 or 2 && TryCell(parts[0], out first) && TryCell(parts[^1], out last) && first.Row <= last.Row && first.Column <= last.Column;
    }

    private static bool TryCell(string reference, out (int Row, int Column) position)
    {
        position = default;
        if (reference.Length is < 2 or > 10) return false;
        var letters = 0;
        var column = 0;
        while (letters < reference.Length && reference[letters] is >= 'A' and <= 'Z')
        {
            column = column * 26 + reference[letters++] - 'A' + 1;
            if (column > 16384) return false;
        }
        if (letters == 0 || !int.TryParse(reference.AsSpan(letters), NumberStyles.None, CultureInfo.InvariantCulture, out var row) || row is < 1 or > 1048576) return false;
        position = (row, column);
        return true;
    }

    private static string Coordinate(int column, int row)
    {
        var letters = "";
        while (column > 0) { --column; letters = (char)('A' + column % 26) + letters; column /= 26; }
        return letters + row.ToString(CultureInfo.InvariantCulture);
    }

    private static string RichText(XElement element, XNamespace spreadsheet) => string.Concat(element.Elements().Select(child =>
        child.Name == spreadsheet + "t" ? child.Value : child.Name == spreadsheet + "r" ? child.Element(spreadsheet + "t")?.Value ?? "" : ""));
    private static string Attribute(XElement element, string name) => element.Attribute(name)?.Value ?? "";
    private static string RelationshipID(XElement element) => element.Attribute(Relationships + "id")?.Value ?? element.Attribute(StrictRelationships + "id")?.Value ?? "";
    private static int? OptionalInteger(XElement? element, string name) => element?.Attribute(name) is { } attribute
        ? int.TryParse(attribute.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : throw new InvalidDataException("Workbook integer is invalid.") : null;
    private static string? RelatedPart(Relationship[] relationships, string kind) => relationships.SingleOrDefault(relationship => !relationship.External && relationship.Type.EndsWith('/' + kind, StringComparison.Ordinal))?.Target;
    private static string ReadRelatedXml(Package package, Relationship[] relationships, string kind) => RelatedPart(relationships, kind) is { } part ? package.ReadXml(part).ToString(SaveOptions.DisableFormatting) : "";

    private sealed class Package
    {
        private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, XDocument> _xml = new(StringComparer.Ordinal);
        private int _nodeBudget = 1000000;

        public Package(ZipArchive zip)
        {
            if (zip.Entries.Count > MaximumEntries) throw new InvalidDataException("Workbook ZIP has too many entries.");
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                if (name.Length > 512 || name.Contains('\\') || name.Contains(':') || name.StartsWith('/')
                    || name.TrimEnd('/').Split('/').Any(part => part is "" or "." or "..") || !_entries.TryAdd(name, entry))
                    throw new InvalidDataException("Workbook ZIP entry path is invalid or duplicated.");
                total += entry.Length;
                if (entry.Length > MaximumPartBytes || total > 64L * 1024 * 1024) throw new InvalidDataException("Workbook expanded ZIP exceeds size limits.");
            }
        }

        public XDocument ReadXml(string part)
        {
            if (_xml.TryGetValue(part, out var cached)) return cached;
            if (!_entries.TryGetValue(part, out var entry)) throw new InvalidDataException("Workbook part is missing: " + part);
            using var stream = entry.Open();
            var bytes = new byte[checked((int)entry.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Workbook part exceeds its declared size.");
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumPartBytes, MaxCharactersFromEntities = 1024 };
            using var memory = new MemoryStream(bytes, false);
            using (var validator = XmlReader.Create(memory, settings))
            {
                while (validator.Read())
                    if (validator.Depth > 64 || (_nodeBudget -= 1 + validator.AttributeCount) < 0) throw new InvalidDataException("Workbook XML nesting or node count exceeds limits.");
            }
            memory.Position = 0;
            using var reader = XmlReader.Create(memory, settings);
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            _xml.Add(part, document);
            return document;
        }

        public Relationship[] ReadRelationships(string part)
        {
            var separator = part.LastIndexOf('/');
            var path = part.Length == 0 ? "_rels/.rels" : part[..(separator + 1)] + "_rels/" + part[(separator + 1)..] + ".rels";
            if (!_entries.ContainsKey(path)) return [];
            var root = ReadXml(path).Root ?? throw new InvalidDataException("Workbook relationships are empty.");
            if (root.Name.LocalName != "Relationships") throw new InvalidDataException("Workbook relationships are invalid.");
            var relationships = root.Elements().Where(element => element.Name.LocalName == "Relationship").Select(element =>
            {
                var external = Attribute(element, "TargetMode").Equals("External", StringComparison.OrdinalIgnoreCase);
                var target = Attribute(element, "Target");
                return new Relationship(Attribute(element, "Id"), Attribute(element, "Type"), external ? target : ResolvePart(part, target), external);
            }).ToArray();
            if (relationships.Length > 20000 || relationships.Any(relationship => relationship.ID.Length == 0)
                || relationships.Select(relationship => relationship.ID).Distinct(StringComparer.Ordinal).Count() != relationships.Length)
                throw new InvalidDataException("Workbook relationships are duplicated or excessive.");
            return relationships;
        }

        private static string ResolvePart(string source, string target)
        {
            target = Uri.UnescapeDataString(target);
            if (target.Length is 0 or > 1024 || target.Contains('\\') || target.Contains(':') || target.Contains('?') || target.Contains('#'))
                throw new InvalidDataException("Workbook relationship target is invalid.");
            var separator = source.LastIndexOf('/');
            var combined = target.StartsWith('/') ? target[1..] : source[..(separator + 1)] + target;
            List<string> parts = [];
            foreach (var component in combined.Split('/'))
            {
                if (component == "..")
                {
                    if (parts.Count == 0) throw new InvalidDataException("Workbook relationship escapes the package.");
                    parts.RemoveAt(parts.Count - 1);
                }
                else if (component != ".")
                {
                    if (component.Length == 0) throw new InvalidDataException("Workbook relationship path is invalid.");
                    parts.Add(component);
                }
            }
            return string.Join('/', parts);
        }
    }
}
