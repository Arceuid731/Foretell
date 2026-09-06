using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BossMod.Foretell;

internal static class ForetellGuideParser
{
    public const int MaxResponseBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(250);
    private static readonly Regex Tokens = new("<!--[\\s\\S]*?-->|<![^>]*>|<[/]?[a-zA-Z][^>\"']*(?:(?:\"[^\"]*\"|'[^']*')[^>\"']*)*>|[^<]+|<", RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex TagName = new(@"^</?([a-zA-Z][a-zA-Z0-9]*)", RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex Anchor = new("\\bid\\s*=\\s*[\"']([^\"']*)[\"']", RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex Space = new(@"\s+", RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex PhaseLabel = new(@"^Phase\s+\d+\b[^\n]{0,100}:?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget);
    private static readonly Regex NamedSentence = new(@"^([\p{L}\p{N}'’ /-]{1,100}?) (is (?:an? |the )[\s\S]+)$", RegexOptions.CultureInvariant, RegexBudget);
    private static readonly HashSet<string> VoidTags = ["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"];
    private static readonly HashSet<string> IgnoredTags = ["script", "style", "iframe", "sup"];

    private sealed class Node(string tag, string anchor = "", string text = "")
    {
        public string Tag = tag;
        public string Anchor = anchor;
        public string Text = text;
        public List<Node> Children = [];
        public string Plain => IgnoredTags.Contains(Tag) ? "" : Text + string.Concat(Children.Select(child => child.Plain + (child.Tag is "li" or "p" or "br" or "td" or "th" ? "\n" : "")));
        public IEnumerable<Node> Walk()
        {
            yield return this;
            foreach (var child in Children)
                foreach (var nested in child.Walk()) yield return nested;
        }
    }

    public static GuideDocument Parse(string response, GuideDuty duty, DateTime retrievedAt)
    {
        if (!duty.Valid || Encoding.UTF8.GetByteCount(response) > MaxResponseBytes) throw new InvalidDataException("Guide response exceeds limits or duty identity is missing.");
        using var json = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 32 });
        if (!json.RootElement.TryGetProperty("parse", out var parsed)) throw new InvalidDataException("Wiki page unavailable.");
        var title = parsed.GetProperty("title").GetString() ?? "";
        var revision = parsed.GetProperty("revid").GetInt64();
        if (GuideNames.Normalize(title) != GuideNames.Normalize(duty.EnglishName) || revision <= 0)
            throw new InvalidDataException("Wiki page identity does not match this duty and variant.");
        var html = parsed.GetProperty("text").GetString() ?? "";
        var tree = ReadHtml(html);
        var blocks = Blocks(tree).ToArray();
        var bosses = new List<GuideBoss>();
        var phases = new List<GuidePhase>();
        var mechanics = new List<GuideMechanic>();
        var bossName = "";
        var bossAnchor = "";
        var phaseName = "";
        var context = "";
        var inBosses = false;
        var bossLevel = 3;

        void FinishPhase()
        {
            if (mechanics.Count != 0 || context.Length != 0) phases.Add(new(phaseName, context.Trim(), mechanics.ToArray()));
            mechanics.Clear(); context = ""; phaseName = "";
        }
        void FinishBoss()
        {
            FinishPhase();
            if (bossName.Length != 0 && phases.Count != 0) bosses.Add(new(bossName, bossAnchor, phases.ToArray()));
            phases.Clear(); bossName = "";
        }
        foreach (var block in blocks)
        {
            var text = Clean(block.Plain);
            if (text.Length == 0) continue;
            var level = block.Tag.Length == 2 && block.Tag[0] == 'h' ? block.Tag[1] - '0' : 0;
            if (level == 2)
            {
                FinishBoss();
                var label = GuideNames.Normalize(text);
                inBosses = label is "bosses" or "boss" or "boss encounters" or "encounters" or "strategy";
                bossLevel = 3;
                if (!inBosses && label.StartsWith("boss: ", StringComparison.Ordinal))
                {
                    inBosses = true; bossLevel = 2; bossName = text[6..]; bossAnchor = FindAnchor(block);
                }
                continue;
            }
            if (!inBosses) continue;
            if (bossName.Length != 0 && (level > bossLevel || (level == bossLevel || block.Tag == "p") && PhaseLabel.IsMatch(text)))
            {
                FinishPhase(); phaseName = text.TrimEnd(':'); continue;
            }
            if (level == bossLevel)
            {
                FinishBoss(); bossName = BossHeading(block, text); bossAnchor = FindAnchor(block); continue;
            }
            if (bossName.Length == 0) continue;
            if (block.Tag is "ul" or "ol")
            {
                foreach (var item in block.Children.Where(child => child.Tag == "li")) AddMechanic(Clean(item.Plain), bossAnchor, mechanics, ref context);
            }
            else if (block.Tag == "table")
            {
                foreach (var row in block.Walk().Where(node => node.Tag == "tr"))
                {
                    var cells = row.Children.Where(node => node.Tag is "td" or "th").ToArray();
                    if (cells.Length >= 2 && cells[0].Tag != "th") AddMechanic(Clean(cells[0].Plain) + ": " + Clean(string.Join("\n", cells.Skip(1).Select(cell => cell.Plain))), bossAnchor, mechanics, ref context);
                }
            }
            else if (block.Tag is "p" or "dl")
            {
                if (mechanics.Count != 0) mechanics[^1] = new(mechanics[^1].Name, mechanics[^1].Text + "\n" + text, mechanics[^1].Anchor);
                else context += text + "\n";
            }
        }
        FinishBoss();
        var document = new GuideDocument(GuideDocument.CurrentSchema, duty, title, revision, retrievedAt, GuideNames.Hash(html), bosses.ToArray());
        if (bosses.Count is 0 or > 32 || document.MechanicCount is 0 or > 512) throw new InvalidDataException("Wiki boss structure is unsupported or exceeds limits.");
        return document;
    }

    private static void AddMechanic(string text, string anchor, List<GuideMechanic> mechanics, ref string context)
    {
        if (text.Length > 24000) throw new InvalidDataException("Wiki mechanic exceeds preparation limits.");
        var colon = text.IndexOf(':');
        if (colon is > 0 and <= 120 && !text[..colon].Contains('\n')) mechanics.Add(new(text[..colon].Trim(), text[(colon + 1)..].Trim(), anchor));
        else if (NamedSentence.Match(text) is { Success: true } sentence) mechanics.Add(new(sentence.Groups[1].Value, sentence.Groups[2].Value, anchor));
        else if (mechanics.Count != 0) mechanics[^1] = new(mechanics[^1].Name, mechanics[^1].Text + "\n" + text, mechanics[^1].Anchor);
        else context += text + "\n";
    }

    private static string Clean(string value) => string.Join("\n", WebUtility.HtmlDecode(value).Split('\n').Select(line => Space.Replace(line, " ").Trim()).Where(line => line.Length != 0));
    private static string FindAnchor(Node node) => node.Walk().Select(child => child.Anchor).FirstOrDefault(anchor => anchor.Length != 0) ?? "";
    private static string BossHeading(Node node, string text)
    {
        var link = node.Walk().Where(child => child.Tag == "a").Select(child => Clean(child.Plain)).LastOrDefault(name => name.Length != 0);
        return link != null && text.EndsWith(": " + link, StringComparison.Ordinal) ? link : text;
    }
    private static IEnumerable<Node> Blocks(Node node)
    {
        foreach (var child in node.Children)
        {
            if (IgnoredTags.Contains(child.Tag)) continue;
            if (child.Tag is "h2" or "h3" or "h4" or "h5" or "h6" or "p" or "ul" or "ol" or "table" or "dl") yield return child;
            else foreach (var block in Blocks(child)) yield return block;
        }
    }

    private static Node ReadHtml(string html)
    {
        var root = new Node("root");
        var stack = new List<Node> { root };
        var count = 0;
        foreach (Match token in Tokens.Matches(html))
        {
            if (++count > 100000) throw new InvalidDataException("Wiki HTML exceeds token limits.");
            var value = token.Value;
            if (!value.StartsWith('<')) { stack[^1].Children.Add(new("text", text: value)); continue; }
            var name = TagName.Match(value);
            if (!name.Success) continue;
            var tag = name.Groups[1].Value.ToLowerInvariant();
            if (value.StartsWith("</", StringComparison.Ordinal))
            {
                var index = stack.FindLastIndex(node => node.Tag == tag);
                if (index > 0) stack.RemoveRange(index, stack.Count - index);
                continue;
            }
            var node = new Node(tag, WebUtility.HtmlDecode(Anchor.Match(value).Groups[1].Value));
            stack[^1].Children.Add(node);
            if (!VoidTags.Contains(tag) && !value.EndsWith("/>", StringComparison.Ordinal)) stack.Add(node);
            if (stack.Count > 64) throw new InvalidDataException("Wiki HTML nesting exceeds limits.");
        }
        return root;
    }
}
