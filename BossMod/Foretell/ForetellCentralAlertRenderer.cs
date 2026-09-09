using Dalamud.Bindings.ImGui;

namespace BossMod.Foretell;

public sealed partial class ForetellEngine
{
    internal static void ApplyCentralAlertStyle(ForetellConfig config)
    {
        config.CentralAlertColor = config.CentralBarColor = 0xFF36A6FF;
        config.CentralDetailColor = 0xFFE6E6E6;
        config.CentralAlertIcons = true;
        config.CentralOutlineThickness = 2;
        config.CentralBackgroundOpacity = .45f;
    }

    internal static GuideDrawBounds DrawCentralAlert(ForetellConfig config, string instruction, string name, double remaining, float total)
    {
        var start = ImGui.GetCursorScreenPos();
        var scale = OverlayScale(config.GuideAlertScale, 1.4f);
        var viewport = ImGui.GetMainViewport();
        var available = Math.Max(1, viewport.Pos.X + viewport.Size.X - start.X - ImGui.GetStyle().WindowPadding.X);
        var width = Math.Min(available, float.IsFinite(config.CentralAlertWidth) ? Math.Clamp(config.CentralAlertWidth, 260, 1400) : 620);
        var padding = Math.Min(12, width * .03f);
        var gap = 6f;
        var iconSize = config.CentralAlertIcons && width >= 100 ? Math.Min(24 * scale, width * .16f) : 0;
        var iconSpace = iconSize > 0 ? iconSize + gap : 0;
        var textWidth = Math.Max(1, width - padding * 2 - iconSpace * 2);
        var outline = float.IsFinite(config.CentralOutlineThickness) ? Math.Clamp(config.CentralOutlineThickness, 0, 4) : 2;
        var opacity = float.IsFinite(config.CentralBackgroundOpacity) ? Math.Clamp(config.CentralBackgroundOpacity, 0, 1) : .45f;
        var timed = double.IsFinite(remaining) && remaining >= 0;
        var timer = timed ? $"{remaining:F1}s" : "";
        var font = ImGui.GetFont();
        ImGui.SetWindowFontScale(scale);
        var fontSize = ImGui.GetFontSize();
        var titleSize = ImGui.CalcTextSize(instruction, false, textWidth);
        var titleHeight = Math.Max(iconSize, titleSize.Y);
        ImGui.SetWindowFontScale(Math.Max(.7f, scale * .72f));
        var detailFontSize = ImGui.GetFontSize();
        var timerSize = ImGui.CalcTextSize(timer);
        var detailWidth = Math.Max(1, width - padding * 2 - (timed ? timerSize.X + gap : 0));
        var nameSize = name.Length > 0 ? ImGui.CalcTextSize(name, false, detailWidth) : Vector2.Zero;
        var detailHeight = Math.Max(nameSize.Y, timed ? timerSize.Y : 0);
        ImGui.SetWindowFontScale(1);

        var barHeight = Math.Max(4, 4 * scale);
        var detailTop = padding + titleHeight + (detailHeight > 0 ? gap : 0);
        var barTop = detailTop + detailHeight + gap;
        var height = (timed ? barTop + barHeight : detailTop + detailHeight) + padding;
        var draw = ImGui.GetWindowDrawList();
        if (opacity > 0)
        {
            draw.AddRectFilled(start, start + new Vector2(width, height), OverlayOpacity(0xFF100F0D, opacity), 8);
            draw.AddRect(start + new Vector2(.5f), start + new Vector2(width - .5f, height - .5f), OverlayOpacity(config.CentralAlertColor, opacity * .35f), 8);
        }
        var titlePosition = start + new Vector2(Math.Max(padding + iconSpace, (width - titleSize.X) * .5f), padding + (titleHeight - titleSize.Y) * .5f);
        DrawOutlinedAlertText(draw, font, fontSize, titlePosition, config.CentralAlertColor, instruction, textWidth, outline);
        if (iconSize > 0)
        {
            var iconY = padding + (titleHeight - iconSize) * .5f;
            DrawAlertWarningIcon(draw, start + new Vector2(padding, iconY), iconSize, config.CentralAlertColor);
            DrawAlertWarningIcon(draw, start + new Vector2(width - padding - iconSize, iconY), iconSize, config.CentralAlertColor);
        }
        if (name.Length > 0)
            DrawOutlinedAlertText(draw, font, detailFontSize, start + new Vector2(padding, detailTop), config.CentralDetailColor, name, detailWidth, outline);
        if (timed)
        {
            DrawOutlinedAlertText(draw, font, detailFontSize, start + new Vector2(width - padding - timerSize.X, detailTop), config.CentralDetailColor, timer, 0, outline);
            var barStart = start + new Vector2(padding, barTop);
            var barWidth = Math.Max(1, width - padding * 2);
            var fraction = total > 0 && float.IsFinite(total) ? Math.Clamp((float)remaining / total, 0, 1) : 0;
            draw.AddRectFilled(barStart - Vector2.One, barStart + new Vector2(barWidth + 1, barHeight + 1), 0xE6000000, 3);
            draw.AddRectFilled(barStart, barStart + new Vector2(barWidth, barHeight), 0xB0404040, 2);
            if (fraction > 0) draw.AddRectFilled(barStart, barStart + new Vector2(barWidth * fraction, barHeight), config.CentralBarColor, 2);
        }
        ImGui.Dummy(new(width, height + gap));
        return GuideDrawBounds.From(start, start + new Vector2(width, height));
    }

    private static void DrawOutlinedAlertText(ImDrawListPtr draw, ImFontPtr font, float size, Vector2 position, uint color, string text, float wrap, float outline)
    {
        if (outline > 0)
            for (var x = -1; x <= 1; ++x)
                for (var y = -1; y <= 1; ++y)
                    if (x != 0 || y != 0) draw.AddText(font, size, position + new Vector2(x, y) * outline, color & 0xFF000000, text, wrap);
        draw.AddText(font, size, position, color, text, wrap);
    }

    private static void DrawAlertWarningIcon(ImDrawListPtr draw, Vector2 position, float size, uint color)
    {
        var top = position + new Vector2(size * .5f, 1);
        var left = position + new Vector2(1, size - 2);
        var right = position + new Vector2(size - 1, size - 2);
        draw.AddTriangleFilled(top, left, right, color);
        draw.AddTriangle(top, left, right, color & 0xFF000000, Math.Max(1.5f, size * .065f));
        var center = position.X + size * .5f;
        var thickness = Math.Max(2, size * .09f);
        draw.AddLine(new(center, position.Y + size * .33f), new(center, position.Y + size * .59f), color & 0xFF000000, thickness);
        draw.AddCircleFilled(new(center, position.Y + size * .76f), thickness * .55f, color & 0xFF000000, 12);
    }
}
