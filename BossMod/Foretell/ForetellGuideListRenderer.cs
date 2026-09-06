using Dalamud.Bindings.ImGui;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    internal static float MeasureGuideListRow(string name, string instruction, int roles, float width, float scale, bool compact, float spacing)
    {
        var line = ImGui.GetTextLineHeight() * scale;
        var icons = Math.Max(1, roles) * (line + 3);
        var nameWidth = Math.Max(1, width - icons - 56 * scale - 14);
        var bodyWidth = Math.Max(1, width - 24);
        var nameHeight = compact ? line : ImGui.CalcTextSize(name, false, nameWidth / scale).Y * scale;
        var cueHeight = ImGui.CalcTextSize(instruction, false, bodyWidth / scale).Y * scale;
        return Math.Max(line, nameHeight) + Math.Max(line, cueHeight) + 7 + (float.IsFinite(spacing) ? Math.Clamp(spacing, 0, 18) : 6);
    }

    internal static void DrawGuideListRow(ForetellConfig config, Vector2 position, float width, float height, float scale, bool compact,
        string name, string instruction, string[] roles, bool active, double? remaining)
    {
        var draw = ImGui.GetWindowDrawList();
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize() * scale;
        var line = ImGui.GetTextLineHeight() * scale;
        var icons = Math.Max(1, roles.Length) * (line + 3);
        var nameWidth = Math.Max(1, width - icons - 56 * scale - 14);
        var bodyWidth = Math.Max(1, width - 24);
        var nameHeight = compact ? line : ImGui.CalcTextSize(name, false, nameWidth / scale).Y * scale;
        var namePosition = position + new Vector2(8 + icons, 3);
        var bodyPosition = position + new Vector2(12, 5 + Math.Max(line, nameHeight));
        if (active)
        {
            draw.AddRectFilled(position, position + new Vector2(width, height - 2), OverlayOpacity(config.GuideActiveColor, .12f), 5);
            draw.AddRectFilled(position, position + new Vector2(3, height - 2), config.GuideActiveColor, 1);
        }
        if (roles.Length == 0)
        {
            var center = position + new Vector2(8 + line * .5f, 3 + line * .5f);
            if (active) draw.AddTriangleFilled(center + new Vector2(-3, -4), center + new Vector2(-3, 4), center + new Vector2(4, 0), config.GuideActiveColor);
            else draw.AddCircle(center, 2.5f * scale, OverlayOpacity(config.GuideTextColor, .6f), 12, 1);
        }
        else
            for (var index = 0; index < roles.Length; ++index)
                DrawGuideRoleIcon(draw, position + new Vector2(8 + index * (line + 3), 3), line, roles[index]);
        if (compact)
        {
            name = GuideChecklistPresentation.Fit(name, nameWidth, text => ImGui.CalcTextSize(text).X * scale);
        }
        var titleColor = active ? config.GuideActiveColor : config.GuideTextColor;
        var cueColor = active ? config.GuideActiveColor : config.GuideInstructionColor;
        draw.AddText(font, fontSize, namePosition + Vector2.One, 0xDD000000, name, nameWidth);
        draw.AddText(font, fontSize, namePosition, titleColor, name, nameWidth);
        draw.AddText(font, fontSize, bodyPosition + Vector2.One, 0xDD000000, instruction, bodyWidth);
        draw.AddText(font, fontSize, bodyPosition, cueColor, instruction, bodyWidth);
        if (active && remaining is { } seconds)
        {
            var text = $"{seconds:F1}s";
            var textSize = ImGui.CalcTextSize(text) * scale;
            var timer = position + new Vector2(width - textSize.X - 8, 3);
            draw.AddText(font, fontSize, timer + Vector2.One, 0xDD000000, text);
            draw.AddText(font, fontSize, timer, config.GuideActiveColor, text);
        }
    }

    private static void DrawGuideRoleIcon(ImDrawListPtr draw, Vector2 position, float size, string role)
    {
        var center = position + new Vector2(size * .5f);
        var radius = size * .34f;
        var color = role == "tank" ? 0xFFFFBE72u : role == "healer" ? 0xFF8AD78Au : 0xFF9292F2u;
        var thickness = Math.Max(1.5f, size * .1f);
        if (role == "healer")
        {
            draw.AddRectFilled(center - new Vector2(radius, thickness), center + new Vector2(radius, thickness), color, 1);
            draw.AddRectFilled(center - new Vector2(thickness, radius), center + new Vector2(thickness, radius), color, 1);
        }
        else if (role == "tank")
        {
            var vertices = new[] { center + new Vector2(-radius, -radius), center + new Vector2(radius, -radius), center + new Vector2(radius, radius * .3f), center + new Vector2(0, radius), center + new Vector2(-radius, radius * .3f) };
            for (var index = 0; index < vertices.Length; ++index) draw.AddLine(vertices[index], vertices[(index + 1) % vertices.Length], color, thickness);
        }
        else if (role == "ranged")
        {
            draw.AddCircle(center, radius, color, 16, thickness);
            draw.AddCircleFilled(center, thickness, color, 12);
        }
        else
        {
            draw.AddLine(center - new Vector2(radius), center + new Vector2(radius), color, thickness);
            draw.AddLine(center + new Vector2(-radius, radius), center + new Vector2(radius, -radius), color, thickness);
            draw.AddLine(center + new Vector2(-radius, radius * .3f), center + new Vector2(-radius * .3f, radius), color, thickness);
            draw.AddLine(center + new Vector2(radius * .3f, radius), center + new Vector2(radius, radius * .3f), color, thickness);
        }
    }
}
