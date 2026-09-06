using BossMod.Foretell;
using Dalamud.Bindings.ImGui;
using System.Numerics;
using System.Runtime.InteropServices;

internal static class GuideOverlayTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static unsafe void Run()
    {
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
            foreach (var count in new[] { 15, 64 })
            {
                ImGui.NewFrame();
                ImGui.SetNextWindowPos(new(10, 10));
                ImGui.SetNextWindowSize(new(1260, 690));
                ImGui.Begin("Controller-friendly mechanic list", flags);
                var instruction = "If marked: move away from other players; otherwise: stay close to the boss.";
                var layout = GuideListFlow.Build(count, 380, 500, new(1230, 640), 1,
                    (_, width, scale, compact) => ForetellEngine.MeasureGuideListRow("A long mechanic name", instruction, 2, width, scale, compact, 6));
                Check(layout.Width <= 1230 && layout.Top.Length == count, "Native list layout loses mechanics");
                var origin = ImGui.GetCursorScreenPos();
                for (var index = 0; index < count; ++index)
                {
                    var position = origin + new Vector2(layout.Column[index] * (layout.ColumnWidth + GuideListFlow.Gap), layout.Top[index]);
                    ForetellEngine.DrawGuideListRow(new(), position, layout.ColumnWidth, layout.Heights[index], layout.Scale, layout.Compact,
                        "A long mechanic name", instruction, ["tank", "healer"], index == count - 1, index == count - 1 ? 3.5 : null);
                    var offset = GuideListFlow.Offset(layout, 640, 0, index);
                    Check(layout.Top[index] - offset >= -.1f && layout.Top[index] + layout.Heights[index] - offset <= 640.1f, "Active mechanic is not visible without mouse input");
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
        }
        finally { ImGui.DestroyContext(context); }
        Console.WriteLine("Native ImGui compact row, transparent/locked flags, editing flags, hover and tooltip smoke passed.");
    }
}
