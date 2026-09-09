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
            ScrollableList(io);
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

        }
        finally { ImGui.DestroyContext(context); }
        Console.WriteLine("Native ImGui compact row, transparent/locked flags, editing flags, hover and tooltip smoke passed.");
    }

    private static void ScrollableList(ImGuiIOPtr io)
    {
        io.DisplaySize = new(800, 450);
        foreach (var unlocked in new[] { false, true })
        foreach (var scale in new[] { 1f, 1.4f })
        {
            var lastScroll = 0f;
            for (var frame = 0; frame < 7; ++frame)
            {
                io.DeltaTime = frame == 0 ? 1 : 1f / 60;
                io.AddMousePosEvent(frame == 0 ? 700 : 100, frame == 0 ? 400 : 160);
                if (frame == 4) io.AddMouseWheelEvent(0, -3);
                ImGui.NewFrame();
                var padding = ImGui.GetStyle().WindowPadding;
                var heading = ImGui.GetTextLineHeight() * 2 + ImGui.GetStyle().ItemSpacing.Y * 2 + 6;
                var chrome = unlocked ? ImGui.GetFrameHeight() : 0;
                var layout = GuideListFlow.Build(23, 360, 180, new(760, 380), scale,
                    (_, width, size, compact) => ForetellEngine.MeasureGuideListRow("Long mechanic name", "If marked: move away; otherwise: stack.", 1, width, size, compact, 6),
                    ImGui.GetStyle().ScrollbarSize);
                Check(layout.Top.Length == 23 && layout.Height == 180 && layout.Scale == scale, "Scrollable list omitted or shrank rows");
                ImGui.SetNextWindowPos(new(20, 20));
                ImGui.SetNextWindowSize(new Vector2(layout.Width, layout.Height + heading + chrome) + padding * 2);
                ImGui.Begin("Scrollable boss list " + unlocked + scale, ForetellEngine.GuideChecklistFlags(unlocked));
                ForetellEngine.DrawGuideOverlayLine("Example boss", 0xFFFFFFFF);
                var headerY = ImGui.GetItemRectMin().Y;
                ForetellEngine.DrawGuideOverlayLine("23 mechanics", 0xFFFFFFFF);
                ImGui.Dummy(new(1, 6));
                ForetellEngine.BeginGuideListScroll(layout);
                var origin = ImGui.GetCursorScreenPos();
                var clip = GuideDrawBounds.From(ImGui.GetWindowDrawList().GetClipRectMin(), ImGui.GetWindowDrawList().GetClipRectMax());
                for (var index = 0; index < 23; ++index)
                {
                    var start = origin + new Vector2(0, layout.Top[index]);
                    ForetellEngine.DrawGuideListRow(new(), start, layout.ColumnWidth, layout.Heights[index], scale, false,
                        "Long mechanic name", "If marked: move away; otherwise: stack.", ["tank"], index == 22, index == 22 ? 3.5 : null);
                    if (frame == 2 && index == 22 || frame == 3 && index == 0)
                        Check(GuideDrawBounds.From(start, start + new Vector2(layout.ColumnWidth, layout.Heights[index])).Visibility(clip, 0xFFFFFFFF) == "Submitted",
                            $"First/last mechanic is clipped after scrolling: unlocked={unlocked}, scale={scale}, frame={frame}, start={start}, clip={clip}, width={layout.ColumnWidth}, height={layout.Heights[index]}, scroll={ImGui.GetScrollY()}, viewport={ImGui.GetWindowSize()}");
                }
                if (frame > 0) Check(ImGui.GetScrollMaxY() > 0, "Long locked list has no scroll range");
                if (frame == 1) ImGui.SetScrollY(GuideListFlow.ScrollToRow(layout, 22, ImGui.GetScrollY(), ImGui.GetWindowSize().Y));
                if (frame == 2) ImGui.SetScrollY(0);
                if (frame == 3) lastScroll = ImGui.GetScrollY();
                if (frame >= 5) Check(ImGui.GetScrollY() > lastScroll, $"Mouse wheel cannot scroll the locked or unlocked list: unlocked={unlocked}, scale={scale}, frame={frame}, scroll={ImGui.GetScrollY()}, hovered={ImGui.IsWindowHovered()}");
                ImGui.EndChild();
                Check(headerY < 70, "Scrolling moved the boss heading");
                ImGui.End();
                ImGui.Render();
                if (frame > 0) Check(ImGui.GetDrawData().TotalVtxCount > 0, "Scrollable rows submitted no drawing");
            }
        }
    }
}
