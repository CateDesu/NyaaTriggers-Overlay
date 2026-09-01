using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace NyaaTriggers.Plugin.Ui;

/// <summary>
/// The full screen alarm flash: while an alarm callout is live, the screen
/// edges glow in the alarm colour and pulse. A window of its own because the
/// alerts box is clipped to its own rect, and the flash is meant to read in
/// peripheral vision rather than behind the callout text.
/// </summary>
internal sealed class FlashWindow : Window
{
    private const ImGuiWindowFlags FlashFlags =
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
        ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBackground |
        ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoInputs |
        ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav |
        ImGuiWindowFlags.NoDocking;

    private readonly Configuration config;

    internal FlashWindow(Configuration config)
        : base("###nyaaFlash")
    {
        this.config = config;

        // Same reasoning as the overlay boxes: this is not a window the user
        // closes, and Escape must not swallow it mid-pull.
        this.RespectCloseHotkey = false;
        this.ShowCloseButton = false;
        this.DisableWindowSounds = true;
    }

    public override void PreDraw()
    {
        this.Flags = FlashFlags;

        // Track the viewport every frame: resolution changes and windowed
        // drags must not leave the glow covering a stale rect.
        var viewport = ImGui.GetMainViewport();
        this.Position = viewport.Pos;
        this.PositionCondition = ImGuiCond.Always;
        this.Size = viewport.Size;
        this.SizeCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();

        var pulse = (float)((Math.Sin(Environment.TickCount64 / 150.0) * 0.25) + 0.75);
        var color = this.config.ColorAlarm;
        var edge = ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, color.W * pulse));
        var clear = ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, 0.0f));

        // The configured share of the shorter side, clamped twice: the share
        // so a hand-edited config stays sane, then the pixels so ultrawide
        // and tiny windows both stay sensible.
        var share = Math.Clamp(this.config.AlarmScreenFlashSize, 0.02f, 0.50f);
        var depth = Math.Clamp(Math.Min(size.X, size.Y) * share, 60.0f, 220.0f);

        // Top and bottom first, then the sides between them so the corners
        // are not painted twice at double strength.
        drawList.AddRectFilledMultiColor(pos, pos + new Vector2(size.X, depth), edge, edge, clear, clear);
        drawList.AddRectFilledMultiColor(
            pos + new Vector2(0.0f, size.Y - depth), pos + size, clear, clear, edge, edge);
        drawList.AddRectFilledMultiColor(
            pos + new Vector2(0.0f, depth), pos + new Vector2(depth, size.Y - depth),
            edge, clear, clear, edge);
        drawList.AddRectFilledMultiColor(
            pos + new Vector2(size.X - depth, depth), pos + new Vector2(size.X, size.Y - depth),
            clear, edge, edge, clear);
    }
}
