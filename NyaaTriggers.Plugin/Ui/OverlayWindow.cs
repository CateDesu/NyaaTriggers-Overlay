using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace NyaaTriggers.Plugin.Ui;

/// <summary>
/// Shared behaviour for the two overlay boxes.
///
/// Locked is the raid-night state: no chrome, no background, no input, so the
/// box is invisible except for what it draws and clicks land on the game.
/// Unlocked gives back a frame and a title bar so it can be dragged, and the
/// owner fills it with sample content so it is never an invisible empty box.
/// </summary>
internal abstract class OverlayWindow : Window
{
    private const ImGuiWindowFlags LockedFlags =
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
        ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBackground |
        ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoInputs |
        ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav |
        ImGuiWindowFlags.NoDocking;

    private const ImGuiWindowFlags UnlockedFlags =
        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
        ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.NoDocking;

    /// <summary>Ignore sub-pixel jitter so a window that is merely being drawn
    /// does not rewrite the config file every frame.</summary>
    private const float GeometryEpsilon = 0.5f;

    protected OverlayWindow(string name, Configuration config)
        : base(name)
    {
        this.Config = config;

        // The overlay is not a window the user closes; it is turned off in
        // settings. Without these, one panic Escape mid-pull hides it, and the
        // unlocked box grows an X that cannot work: IsOpen is rewritten every
        // frame, so clicking it just flickers the window.
        this.RespectCloseHotkey = false;
        this.ShowCloseButton = false;
        this.DisableWindowSounds = true;
    }

    protected Configuration Config { get; }

    /// <summary>Where this window's geometry is stored, so the base class can
    /// persist a drag without each subclass repeating it.</summary>
    protected abstract Vector2 StoredPosition { get; set; }

    protected abstract Vector2 StoredSize { get; set; }

    /// <summary>Text scale for this box, so the timeline bars and the alerts
    /// are sized independently in settings.</summary>
    protected abstract float TextScale { get; }

    public override void PreDraw()
    {
        var locked = this.Config.Locked;
        this.Flags = locked ? LockedFlags : UnlockedFlags;

        // The window bg honors the configured backdrop opacity (0 = invisible).
        // The locked state stays NoBackground and gets a custom rect in Draw()
        // instead, so its click-through and chromeless shape are unaffected.
        ImGui.SetNextWindowBgAlpha(Math.Clamp(this.Config.BgOpacity, 0.0f, 1.0f));

        // Dalamud multiplies Size by GlobalScale on the way out but leaves
        // Position alone, so the stored size is divided back out here. Skipping
        // this draws the box GlobalScale times too big at any UI scale, and
        // compounds every session as the scaled size is captured and re-scaled.
        this.Position = this.StoredPosition;
        this.Size = this.StoredSize / ImGuiHelpers.GlobalScale;

        // Pinned while locked; while unlocked the stored value is only a
        // starting point, or dragging would snap straight back every frame.
        var condition = locked ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
        this.PositionCondition = condition;
        this.SizeCondition = condition;
    }

    public override void Draw()
    {
        var scale = Math.Clamp(this.TextScale, 0.5f, 3.0f);
        ImGui.SetWindowFontScale(scale);
        try
        {
            // Locked boxes are NoBackground by design: draw the configured
            // backdrop ourselves (a no-op at 0 opacity, which is the default).
            if (this.Config.Locked && this.Config.BgOpacity > 0.0f)
            {
                var drawList = ImGui.GetWindowDrawList();
                var pos = ImGui.GetWindowPos();
                var size = ImGui.GetWindowSize();
                drawList.AddRectFilled(
                    pos,
                    pos + size,
                    ImGui.GetColorU32(ImGuiCol.WindowBg, Math.Clamp(this.Config.BgOpacity, 0.0f, 1.0f)),
                    3.0f);
            }

            this.DrawContent();
        }
        finally
        {
            // Font scale is window state, not a stack: leaving it set would
            // scale the next thing drawn into this window too.
            ImGui.SetWindowFontScale(1.0f);
        }

        if (!this.Config.Locked)
        {
            this.CaptureGeometry();
        }
    }

    protected abstract void DrawContent();

    /// <summary>Remember where the user dragged this box to. Held in memory and
    /// written out when the overlay is locked or the plugin unloads, rather
    /// than rewriting the config file every frame of a drag.</summary>
    private void CaptureGeometry()
    {
        var position = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();

        if (Vector2.Distance(position, this.StoredPosition) > GeometryEpsilon)
        {
            this.StoredPosition = position;
        }

        if (Vector2.Distance(size, this.StoredSize) > GeometryEpsilon)
        {
            this.StoredSize = size;
        }
    }

    protected static uint ToColor(Vector4 rgba) => ImGui.ColorConvertFloat4ToU32(rgba);

    /// <summary>Fade a colour's alpha, for alerts on their way out.</summary>
    protected static Vector4 WithAlpha(Vector4 rgba, float alpha)
        => new(rgba.X, rgba.Y, rgba.Z, rgba.W * Math.Clamp(alpha, 0.0f, 1.0f));

    /// <summary>Draw text with a dark outline. Overlay text floats over the game
    /// with little or no backdrop, and unoutlined text washes out over bright
    /// arenas; the outline is what keeps a callout readable mid-pull.</summary>
    protected static void AddOutlinedText(ImDrawListPtr drawList, Vector2 pos, Vector4 color, string text)
    {
        var outline = ImGui.GetColorU32(new Vector4(0.0f, 0.0f, 0.0f, 0.9f * Math.Clamp(color.W, 0.0f, 1.0f)));
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                drawList.AddText(pos + new Vector2(dx, dy), outline, text);
            }
        }

        drawList.AddText(pos, ToColor(color), text);
    }
}
