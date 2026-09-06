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
