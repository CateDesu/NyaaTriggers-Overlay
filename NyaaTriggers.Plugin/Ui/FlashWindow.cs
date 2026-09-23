using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace NyaaTriggers.Plugin.Ui;

/// <summary>Use a separate window so alert bounds do not clip the flash.</summary>
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

    internal float AlarmAlpha { get; set; } = 1.0f;

    internal FlashWindow(Configuration config)
        : base("###nyaaFlash")
    {
        this.config = config;

        // Overlay visibility is controlled by settings, so Escape must not close it.
        this.RespectCloseHotkey = false;
        this.ShowCloseButton = false;
        this.DisableWindowSounds = true;
    }

    public override void PreDraw()
    {
        this.Flags = FlashFlags;

        // Dalamud scales Size but leaves Position unchanged.
        var viewport = ImGui.GetMainViewport();
        this.Position = viewport.Pos;
        this.PositionCondition = ImGuiCond.Always;
        this.Size = viewport.Size / ImGuiHelpers.GlobalScale;
        this.SizeCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();

        var pulse = (float)((Math.Sin(Environment.TickCount64 / 150.0) * 0.25) + 0.75) * this.AlarmAlpha;
        var color = this.config.ColorAlarm;
        var edge = ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, color.W * pulse));
        var clear = ImGui.GetColorU32(new Vector4(color.X, color.Y, color.Z, 0.0f));

        var share = Math.Clamp(this.config.AlarmScreenFlashSize, 0.02f, 0.50f);
        var shortest = Math.Min(size.X, size.Y);
        var depth = Math.Min(Math.Clamp(shortest * share, 60.0f, 220.0f), shortest * 0.5f);

        // Draw sides between the top and bottom bands to avoid doubling corner opacity.
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
