using BossMod.Foretell;
using Dalamud.Bindings.ImGui;
using System.Numerics;
using System.Runtime.InteropServices;

internal static class GuideOverlayTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static unsafe void Run()
    {
        GuideListFlowTests.Run();
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "addon", "Hooks", "dev");
        NativeLibrary.Load(Path.Combine(directory, "cimgui.dll"));
        var context = ImGui.CreateContext();
        try
        {
            var io = ImGui.GetIO();
            io.IniFilename = null;
            io.DisplaySize = new(1280, 720);
            io.DeltaTime = 1f / 60;
            io.Fonts.AddFontDefault();
            io.Fonts.Build();
            var flags = ForetellEngine.GuideChecklistFlags(false);
            Check(flags.HasFlag(ImGuiWindowFlags.NoBackground) && flags.HasFlag(ImGuiWindowFlags.NoTitleBar)
                && flags.HasFlag(ImGuiWindowFlags.NoMove) && flags.HasFlag(ImGuiWindowFlags.NoResize)
                && flags.HasFlag(ImGuiWindowFlags.NoScrollbar), "Locked overlay has background, decorations or scrolling");
            Check(!flags.HasFlag(ImGuiWindowFlags.NoMouseInputs), "Locked overlay cannot receive hover");
            var editing = ForetellEngine.GuideChecklistFlags(true);
            Check(!editing.HasFlag(ImGuiWindowFlags.NoMove) && !editing.HasFlag(ImGuiWindowFlags.NoResize), "Unlocked overlay cannot move/resize");
            for (var frame = 0; frame < 3; ++frame)
            {
                io.AddMousePosEvent(35, 35);
                ImGui.NewFrame();
                ImGui.SetNextWindowPos(new(20, 20));
                ImGui.SetNextWindowSize(new(430, 90));
                ImGui.Begin("Guide overlay hover test", flags);
                ForetellEngine.DrawGuideOverlayLine("Breath — DODGE AOE", 0xFFFFFFFF);
                Check(ImGui.GetItemRectSize().Y <= ImGui.GetTextLineHeight() + 1, "Compact row occupies more than one line");
                if (frame > 0)
                {
                    Check(ImGui.IsItemHovered(), "Transparent locked row does not support tooltips");
                    ImGui.BeginTooltip(); ImGui.TextUnformatted("Source details"); ImGui.EndTooltip();
                }
                ImGui.End();
                ImGui.Render();
                if (frame > 0) Check(ImGui.GetDrawData().TotalVtxCount > 0, "Overlay submitted no text geometry");
            }
            Check(!ForetellEngine.GuideEntryFlags.HasFlag(ImGuiWindowFlags.NoSavedSettings), "Entry summary cannot retain its layout");
            for (var frame = 0; frame < 2; ++frame)
            {
                ImGui.NewFrame();
                ImGui.SetNextWindowPos(new(20, 20));
                ImGui.SetNextWindowSize(new(430, 220));
                ImGui.Begin("Highlight drawing evidence", flags);
                var draw = ImGui.GetWindowDrawList();
                var start = ImGui.GetCursorScreenPos();
                var config = new ForetellConfig { GuideActiveColor = 0xFF00FFFF };
                var before = draw.VtxBuffer.Size;
                ForetellEngine.DrawGuideListRow(config, start, 380, 70, 1, false, "Storm", "Move away", ["healer"], false, null);
                var inactiveVertices = draw.VtxBuffer.Size - before;
                before = draw.VtxBuffer.Size;
                var activeStart = start + new Vector2(0, 80);
                ForetellEngine.DrawGuideListRow(config, activeStart, 380, 70, 1, false, "Storm", "Move away", ["healer"], true, 4);
                if (frame > 0)
                {
                    Check(draw.VtxBuffer.Size - before > inactiveVertices, "Active row submitted no highlight/timer geometry");
                    var bounds = GuideDrawBounds.From(activeStart, activeStart + new Vector2(380, 70));
                    var clip = GuideDrawBounds.From(draw.GetClipRectMin(), draw.GetClipRectMax());
                    Check(bounds.Visibility(clip, config.GuideActiveColor) == "Submitted", "Headless highlighted row is clipped");
                }
                ImGui.End();
                ImGui.Render();
            }
            Check(ForetellEngine.GuideJournalFlags.HasFlag(ImGuiWindowFlags.NoFocusOnAppearing)
                && !ForetellEngine.GuideJournalFlags.HasFlag(ImGuiWindowFlags.AlwaysAutoResize)
                && !ForetellEngine.GuideJournalFlags.HasFlag(ImGuiWindowFlags.NoResize), "Analysis journal steals focus or prevents resizing");
            foreach (var scale in new[] { .7f, 1.4f, 3f })
            {
                ImGui.NewFrame();
                ImGui.SetNextWindowPos(new(10, 10));
                ImGui.SetNextWindowSize(new(750, 450));
                ImGui.Begin("Central alert appearance", flags);
                var config = new ForetellConfig { GuideAlertScale = scale, CentralAlertWidth = 620, CentralAlertColor = 0xFF123456, CentralBarColor = 0xFF654321 };
                ForetellEngine.DrawCentralAlert(config, "DEMO · MOVE BEHIND", "Frontal cone", 4.5, 6);
                Check(ImGui.GetCursorPosY() < 400, "Central alert overflows at a supported scale");
                Check(ImGui.GetItemRectSize().X <= 621, "Cast bar ignored the selected width");
                Check(Math.Abs(ImGui.GetFontSize() - io.Fonts.Fonts[0].FontSize) < .1f, "Central alert leaked font scale");
                ImGui.End();
                ImGui.Render();
                Check(ImGui.GetDrawData().TotalVtxCount > 0, "Central alert submitted no drawing");
            }
            ImGui.NewFrame();
            ImGui.SetNextWindowPos(new(950, 10));
            ImGui.SetNextWindowSize(new(300, 400));
            ImGui.Begin("Central alert at right edge", flags);
            ForetellEngine.DrawCentralAlert(new() { CentralAlertWidth = 1200, GuideAlertScale = 3 }, "DEMO · MOVE BEHIND", "Frontal cone", 4, 6);
            Check(ImGui.GetItemRectMax().X <= io.DisplaySize.X, "Central bar extends beyond viewport after moving");
            ImGui.End();
            ImGui.Render();
            foreach (var count in new[] { 23, 9 })
            foreach (var requestedScale in new[] { 1f, 1.4f })
            {
                ImGui.NewFrame();
                ImGui.SetNextWindowPos(new(10, 10));
                ImGui.SetNextWindowSize(new(1260, 690));
                ImGui.Begin("Controller-friendly mechanic list", flags);
                var instruction = "If marked: move away from other players; otherwise: stay close to the boss.";
                var activeCount = count == 9 ? count : 1;
                var layout = GuideListFlow.Build(count, 380, 500, new(1230, 640), requestedScale,
                    (_, width, scale, compact) => ForetellEngine.MeasureGuideListRow("A long mechanic name", instruction, 2, width, scale, compact, 6), activeCount);
                Check(layout.Width <= 1230 && layout.Height <= 640 && layout.Top.Length >= activeCount, "Native list layout loses active mechanics");
                Check(layout.Scale == requestedScale && !layout.Compact, "Native list silently reduced text size");
                if (count == 23) Check(layout.Top.Length <= 6 && layout.Columns == 1, "Native combat list displays the whole catalogue");
                var origin = ImGui.GetCursorScreenPos();
                for (var index = 0; index < layout.Top.Length; ++index)
                {
                    var position = origin + new Vector2(layout.Column[index] * (layout.ColumnWidth + GuideListFlow.Gap), layout.Top[index]);
                    ForetellEngine.DrawGuideListRow(new(), position, layout.ColumnWidth, layout.Heights[index], layout.Scale, layout.Compact,
                        "A long mechanic name", instruction, ["tank", "healer"], index < activeCount, index < activeCount ? 3.5 : null);
                    Check(layout.Top[index] >= 0 && layout.Top[index] + layout.Heights[index] <= 640.1f, "Active mechanic is not visible without mouse input");
                }
                ImGui.End();
                ImGui.Render();
                Check(ImGui.GetDrawData().TotalVtxCount > 0, "Mechanic list and role icons did not render");
            }
            for (var frame = 0; frame < 4; ++frame)
            {
                ImGui.NewFrame();
                ForetellEngine.SetGuideEntryLayout(Vector2.Zero, io.DisplaySize);
                ImGui.Begin("Entry summary resize test", ForetellEngine.GuideEntryFlags);
                if (frame == 1) ImGui.SetWindowSize(new(670, 410));
                if (frame > 1) Check(Vector2.Distance(ImGui.GetWindowSize(), new(670, 410)) < 1, "Entry popup snaps back after resizing");
                ImGui.End();
                ImGui.Render();
            }
            foreach (var unlocked in new[] { false, true })
            foreach (var activeCount in new[] { 1, 7, 9 })
            {
                io.DisplaySize = new(800, 450);
                ImGui.NewFrame();
                var padding = ImGui.GetStyle().WindowPadding;
                var heading = ImGui.GetTextLineHeight() * 2 + ImGui.GetStyle().ItemSpacing.Y * 2 + 6;
                var chrome = unlocked ? ImGui.GetFrameHeight() : 0;
                var available = io.DisplaySize - padding * 2 - new Vector2(16, 16 + heading + chrome);
                var layout = GuideListFlow.Build(23, 300, 180 - heading - chrome - padding.Y * 2, available, 1,
                    (_, width, scale, compact) => ForetellEngine.MeasureGuideListRow("Long mechanic name", "If marked: move away; otherwise: stack.", 1, width, scale, compact, 6), activeCount);
                Check(layout.Top.Length >= activeCount && layout.Height <= available.Y, "Small viewport crops active overflow");
                var dimensions = new Vector2(layout.Width, layout.Height + heading + chrome) + padding * 2;
                var position = Vector2.Clamp(new(740, 410), Vector2.Zero, Vector2.Max(Vector2.Zero, io.DisplaySize - dimensions));
                ImGui.SetNextWindowSize(dimensions);
                ImGui.SetNextWindowPos(position);
                ImGui.Begin("Small mechanic list bounds " + unlocked + activeCount, ForetellEngine.GuideChecklistFlags(unlocked));
                ForetellEngine.DrawGuideOverlayLine("Example boss", 0xFFFFFFFF);
                ForetellEngine.DrawGuideOverlayLine(activeCount + " mechanics", 0xFFFFFFFF);
                var origin = ImGui.GetCursorScreenPos() + new Vector2(0, 6);
                var contentEnd = ImGui.GetWindowPos() + ImGui.GetWindowSize() - padding;
                for (var index = 0; index < activeCount; ++index)
                {
                    var rowPosition = origin + new Vector2(layout.Column[index] * (layout.ColumnWidth + GuideListFlow.Gap), layout.Top[index]);
                    var rowEnd = rowPosition + new Vector2(layout.ColumnWidth, layout.Heights[index]);
                    Check(rowEnd.X <= contentEnd.X + 1 && rowEnd.Y <= contentEnd.Y + 1 && rowEnd.X <= io.DisplaySize.X && rowEnd.Y <= io.DisplaySize.Y,
                        "Active row is clipped after including heading, title bar or viewport edge");
                    ForetellEngine.DrawGuideListRow(new(), rowPosition, layout.ColumnWidth, layout.Heights[index], layout.Scale, layout.Compact,
                        "Long mechanic name", "If marked: move away; otherwise: stack.", ["tank"], true, 3.5);
                }
                ImGui.End();
                ImGui.Render();
                Check(ImGui.GetDrawData().TotalVtxCount > 0, "Small mechanic list submitted no drawing");
            }
        }
        finally { ImGui.DestroyContext(context); }
        Console.WriteLine("Native ImGui compact row, transparent/locked flags, editing flags, hover and tooltip smoke passed.");
    }
}
