using Dalamud.Bindings.ImGui;

namespace BossMod.Foretell;

internal sealed record ForetellDemoFrame(DecisionFrame Frame, GuideCombatFrame Guide, string Cue, float Remaining);

internal sealed class ForetellDemo
{
    internal const int DurationSeconds = 120;
    internal const int CastSeconds = 6;
    private readonly record struct Example(string Name, string Cue, GeometryKind Geometry, MechanicKind Kind, GuidanceKind Guidance, float Radius, float Secondary);
    private static readonly Example[] Examples =
    [
        new("Circle", "MOVE OUT", GeometryKind.Circle, MechanicKind.GroundAOE, GuidanceKind.Avoid, 6, 0),
        new("Donut", "MOVE IN", GeometryKind.Donut, MechanicKind.GroundAOE, GuidanceKind.Avoid, 5, 14),
        new("Frontal cone", "MOVE BEHIND", GeometryKind.Cone, MechanicKind.GroundAOE, GuidanceKind.Avoid, 14, MathF.PI / 4),
        new("Line attack", "STEP ASIDE", GeometryKind.Rectangle, MechanicKind.GroundAOE, GuidanceKind.Avoid, 18, 3),
        new("Stack marker", "STACK TOGETHER", GeometryKind.Circle, MechanicKind.Stack, GuidanceKind.Stack, 4, 0),
        new("Spread markers", "SPREAD OUT", GeometryKind.Circle, MechanicKind.Spread, GuidanceKind.Spread, 5, 0),
        new("Gaze", "LOOK AWAY", GeometryKind.Unknown, MechanicKind.Gaze, GuidanceKind.LookAway, 0, 0),
        new("Tankbuster", "TANK: MITIGATE", GeometryKind.Unknown, MechanicKind.Tankbuster, GuidanceKind.Tankbuster, 0, 0)
    ];
    private readonly int[] _order;
    private readonly GuideBoss _boss;
    private readonly DateTime _started;
    private readonly uint _territory;
    private readonly ulong _player;
    private int _offset;
    private DateTime _castStarted;

    public ForetellDemo(DateTime now, uint territory, ulong player, int seed)
    {
        _started = _castStarted = now;
        _territory = territory;
        _player = player;
        _order = Enumerable.Range(0, Examples.Length).ToArray();
        new Random(seed).Shuffle(_order);
        var mechanics = Examples.Select(example => new GuideMechanic(example.Name, example.Cue, "demo")
        {
            Advice = new(GuideLanguage.English, example.Name, example.Cue, "Demo · " + example.Cue, "manual", "", [])
        }).ToArray();
        _boss = new("DEMO", "demo", [new("", "", mechanics)]);
    }

    public void Next(DateTime now)
    {
        _offset = (_offset + (int)Math.Max(0, (now - _castStarted).TotalSeconds / CastSeconds) + 1) % _order.Length;
        _castStarted = now;
    }

    public ForetellDemoFrame? Frame(DateTime now, uint territory, ulong player, Vector2 position, float rotation, bool inCombat, bool dead)
    {
        if (inCombat || dead || territory != _territory || player == 0 || player != _player
            || now < _started || (now - _started).TotalSeconds >= DurationSeconds
            || !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(rotation)) return null;
        var elapsed = Math.Max(0, (now - _castStarted).TotalSeconds);
        var cycle = (int)(elapsed / CastSeconds);
        var index = _order[(_offset + cycle) % _order.Length];
        var example = Examples[index];
        var remaining = CastSeconds - (float)(elapsed % CastSeconds);
        var until = now.AddSeconds(remaining);
        var origin = position + new Vector2(MathF.Sin(rotation), MathF.Cos(rotation)) * 5;
        if (example.Guidance is GuidanceKind.Stack or GuidanceKind.Spread) origin = position;
        var prediction = new ActivePrediction(player, 0, example.Geometry, example.Kind, origin, origin, rotation + MathF.PI,
            example.Radius, example.Secondary, until, 1, "Demo", SignalKey: "demo", TargetID: player,
            Guidance: example.Guidance, Label: "DEMO · " + example.Name) { GuideLinked = true, Provenance = "Demo" };
        var phase = _boss.Phases[0];
        var signal = new GuideSignal(_boss, phase, phase.Mechanics[index], GuideSignalKind.Cast, player, 0, 0, 0, player, until, example.Guidance, "Demo");
        return new(new(now, [new(-1, prediction, until, example.Geometry != GeometryKind.Unknown, true, "Demo")], false, false),
            new(_boss, false, false, phase, [signal], 0), example.Cue, remaining);
    }
}

public sealed partial class ForetellEngine
{
    private ForetellDemo? _demo;
    private ForetellDemoFrame? _demoFrame;
    private DecisionFrame OverlayFrame => _demoFrame?.Frame ?? PresentationFrame;

    private void UpdateDemo()
    {
        var player = _ws.Party[PartyState.PlayerSlot];
        _demoFrame = _demo?.Frame(_ws.CurrentTime, _territory, player?.InstanceID ?? 0,
            player == null ? Vector2.Zero : V(player.Position), player?.Rotation.Rad ?? 0, _guideCombat || _inPull, player?.IsDeadOrDestroyed ?? true);
        if (_demoFrame == null) _demo = null;
    }

    private void DrawDemoSettings()
    {
        var player = _ws.Party[PartyState.PlayerSlot];
        ImGui.BeginDisabled(_guideCombat || _inPull || player == null || player.IsDeadOrDestroyed);
        if (ImGui.Button(_demo == null ? GuideText("Start overlay demo", "Démarrer la démo des overlays", "Overlay-Demo starten", "表示デモを開始")
            : GuideText("Stop demo", "Arrêter la démo", "Demo stoppen", "デモを停止")))
        {
            _demo = _demo == null ? new(_ws.CurrentTime, _territory, player!.InstanceID, Random.Shared.Next()) : null;
            UpdateDemo();
        }
        if (_demo != null)
        {
            ImGui.SameLine();
            if (ImGui.Button(GuideText("Next mechanic", "Mécanique suivante", "Nächste Mechanik", "次のギミック"))) { _demo.Next(_ws.CurrentTime); UpdateDemo(); }
        }
        ImGui.EndDisabled();
        ImGui.TextDisabled(GuideText("2-minute preview · stops on entering combat or changing area.", "Aperçu de 2 minutes · arrêt au combat ou au changement de zone.",
            "2 Minuten Vorschau · stoppt bei Kampf oder Gebietswechsel.", "2分間のプレビュー・戦闘またはエリア移動で停止。"));
    }

    internal static float OverlayScale(float value, float fallback = 1) => float.IsFinite(value) ? Math.Clamp(value, .5f, 4) : fallback;
    internal static uint OverlayOpacity(uint color, float opacity)
        => (color & 0xFFFFFF) | (uint)MathF.Round((color >> 24) * (float.IsFinite(opacity) ? Math.Clamp(opacity, 0, 1) : 1)) << 24;

    internal static void DrawCentralAlert(ForetellConfig config, string instruction, string name, double remaining, float total)
    {
        var scale = OverlayScale(config.GuideAlertScale, 1.4f);
        var viewport = ImGui.GetMainViewport();
        var available = Math.Max(1, viewport.Pos.X + viewport.Size.X - ImGui.GetCursorScreenPos().X - ImGui.GetStyle().WindowPadding.X);
        var width = Math.Min(available, float.IsFinite(config.CentralAlertWidth) ? Math.Clamp(config.CentralAlertWidth, 260, 1400) : 620);
        ImGui.SetWindowFontScale(scale);
        ImGui.PushStyleColor(ImGuiCol.Text, GuideColor(config.CentralAlertColor));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        ImGui.TextWrapped(instruction);
        ImGui.PopTextWrapPos(); ImGui.PopStyleColor();
        ImGui.SetWindowFontScale(Math.Max(.7f, scale * .72f));
        ImGui.PushStyleColor(ImGuiCol.Text, GuideColor(config.CentralDetailColor));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        ImGui.TextWrapped(name);
        ImGui.PopTextWrapPos();
        if (double.IsFinite(remaining) && remaining >= 0)
        {
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, GuideColor(config.CentralBarColor));
            ImGui.ProgressBar(total > 0 && float.IsFinite(total) ? Math.Clamp((float)remaining / total, 0, 1) : 0, new(width, 0), $"{remaining:F1}s");
            ImGui.PopStyleColor();
        }
        ImGui.PopStyleColor();
        ImGui.SetWindowFontScale(1);
    }
}
