using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace NyaaTriggers.Plugin.Ui;

/// <summary>
/// Shared behaviour for the overlay boxes.
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

    /// <summary>Set by ResetGeometry so the next PreDraw force-applies the
    /// stored geometry even while unlocked, where FirstUseEver would keep the
    /// window wherever it currently sits. A closed window never runs PreDraw,
    /// so the flag simply waits there until the box is shown again.</summary>
    private bool forceGeometry;

    protected OverlayWindow(string name, Configuration config, ScaledFonts fonts)
        : base(name)
    {
        this.Config = config;
        this.Fonts = fonts;

        // The overlay is not a window the user closes; it is turned off in
        // settings. Without these, one panic Escape mid-pull hides it, and the
        // unlocked box grows an X that cannot work: IsOpen is rewritten every
        // frame, so clicking it just flickers the window.
        this.RespectCloseHotkey = false;
        this.ShowCloseButton = false;
        this.DisableWindowSounds = true;
    }

    protected Configuration Config { get; }

    /// <summary>Size-snapped fonts for crisp text, shared by both boxes.</summary>
    protected ScaledFonts Fonts { get; }

    /// <summary>Where this window's geometry is stored, so the base class can
    /// persist a drag without each subclass repeating it.</summary>
    protected abstract Vector2 StoredPosition { get; set; }

    protected abstract Vector2 StoredSize { get; set; }

    /// <summary>Text scale for this box, so the timeline bars and the alerts
    /// are sized independently in settings.</summary>
    protected abstract float TextScale { get; }

    /// <summary>Backdrop opacity for this box; the boxes are configured
    /// independently so a loud timeline can sit next to frameless alerts.</summary>
    protected abstract float BgOpacity { get; }

    /// <summary>What keeps this box's text readable over the game: nothing, an
    /// outline, or a soft glow. Each box carries its own so a loud alert
    /// effect does not force itself onto the timeline.</summary>
    protected abstract TextEffectStyle TextEffect { get; }

    /// <summary>Whole-box opacity multiplier. Every colour the box draws is
    /// folded through it in ToColor, the backdrop included. Clamped just
    /// above zero: a box faded to nothing could never be found again.</summary>
    protected virtual float FadeOpacity => 1.0f;

    /// <summary>The fade for this frame, clamped to the drawable range.</summary>
    private float Fade => Math.Clamp(this.FadeOpacity, 0.05f, 1.0f);

    /// <summary>Effect reach in pixels for this box.</summary>
    protected abstract int EffectThickness { get; }

    /// <summary>Effect colour for this box; the alpha is the effect's opacity.</summary>
    protected abstract Vector4 EffectColor { get; }

    /// <summary>The effective on-screen pixel size of this box's text for the
    /// frame being drawn, set in Draw before DrawContent. Sub-captions that
    /// should read smaller than the body text derive their font from it.</summary>
    protected float TextPx { get; private set; }

    /// <summary>Clamp a text scale to the range the settings sliders offer,
    /// 0.5x to 6x. Every window draws through this so a hand-edited config
    /// cannot push text past what the font buckets cover.</summary>
    protected static float ClampTextScale(float scale) => Math.Clamp(scale, 0.5f, 6.0f);

    public override void PreDraw()
    {
        var locked = this.Config.Locked;
        this.Flags = locked ? LockedFlags : UnlockedFlags;

        // The window bg honors the configured backdrop opacity (0 = invisible),
        // scaled by the box's fade like everything else it draws.
        // The locked state stays NoBackground and gets a custom rect in Draw()
        // instead, so its click-through and chromeless shape are unaffected.
        ImGui.SetNextWindowBgAlpha(Math.Clamp(this.BgOpacity, 0.0f, 1.0f) * this.Fade);

        // Dalamud multiplies Size by GlobalScale on the way out but leaves
        // Position alone, so the stored size is divided back out here. Skipping
        // this draws the box GlobalScale times too big at any UI scale, and
        // compounds every session as the scaled size is captured and re-scaled.
        this.Position = this.StoredPosition;
        this.Size = this.StoredSize / ImGuiHelpers.GlobalScale;

        // Pinned while locked; while unlocked the stored value is only a
        // starting point, or dragging would snap straight back every frame.
        // A geometry reset forces one Always pass so it also lands unlocked.
        var condition = locked || this.forceGeometry ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
        this.forceGeometry = false;
        this.PositionCondition = condition;
        this.SizeCondition = condition;
    }

    /// <summary>Back to the shipped position and size, for a box dragged off
    /// screen or stranded by a resolution change.</summary>
    internal abstract void ResetGeometry();

    /// <summary>Arm the one-shot force PreDraw honors. Called by the subclass
    /// once the stored geometry holds the fresh defaults.</summary>
    protected void ForceGeometry() => this.forceGeometry = true;

    public override void Draw()
    {
        var scale = ClampTextScale(this.TextScale);

        // The current font here is the window's default: its size already
        // includes Dalamud's UI scale, so it is the honest base for what the
        // plain bitmap-scaling path would have produced.
        var targetPx = ImGui.GetFont().FontSize * scale;
        this.TextPx = targetPx;

        // Crisp text at any size: push a font rasterized at (just above) the
        // target size and let the window scale cover only the few-percent
        // remainder. Only while the font is still building, or if the atlas
        // failed, fall back to stretching the default font for the frame.
        var handle = this.Fonts.Get(targetPx);
        if (handle is { Available: true })
        {
            using (handle.Push())
            {
                var actualPx = ImGui.GetFont().FontSize;
                ImGui.SetWindowFontScale(actualPx > 0.0f ? targetPx / actualPx : 1.0f);
                try
                {
                    this.DrawBackdrop();
                    this.DrawContent();
                }
                finally
                {
                    // Font scale is window state, not a stack: leaving it set
                    // would scale the next thing drawn into this window too.
                    ImGui.SetWindowFontScale(1.0f);
                }
            }
        }
        else
        {
            ImGui.SetWindowFontScale(scale);
            try
            {
                this.DrawBackdrop();
                this.DrawContent();
            }
            finally
            {
                ImGui.SetWindowFontScale(1.0f);
            }
        }

        if (!this.Config.Locked)
        {
            this.CaptureGeometry();
        }
    }

    protected abstract void DrawContent();

    /// <summary>Locked boxes are NoBackground by design: draw the configured
    /// backdrop ourselves (a no-op at 0 opacity, which is the default).</summary>
    private void DrawBackdrop()
    {
        if (!this.Config.Locked || this.BgOpacity <= 0.0f)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        drawList.AddRectFilled(
            pos,
            pos + size,
            ImGui.GetColorU32(ImGuiCol.WindowBg, Math.Clamp(this.BgOpacity, 0.0f, 1.0f) * this.Fade),
            3.0f);
    }

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

    /// <summary>Float rgba to a draw-list colour with the box's fade folded
    /// into the alpha, so everything drawn through here answers the fade knob.
    /// </summary>
    protected uint ToColor(Vector4 rgba) => ImGui.ColorConvertFloat4ToU32(WithAlpha(rgba, this.Fade));

    /// <summary>Fade a colour's alpha, for alerts on their way out.</summary>
    protected static Vector4 WithAlpha(Vector4 rgba, float alpha)
        => new(rgba.X, rgba.Y, rgba.Z, rgba.W * Math.Clamp(alpha, 0.0f, 1.0f));

    /// <summary>Blend a colour toward white, keeping its alpha. The lit top
    /// edge of a bar fill, so the fill reads lit rather than flat.</summary>
    protected static Vector4 Lighten(Vector4 rgba, float amount)
    {
        var t = Math.Clamp(amount, 0.0f, 1.0f);
        return new(
            rgba.X + ((1.0f - rgba.X) * t),
            rgba.Y + ((1.0f - rgba.Y) * t),
            rgba.Z + ((1.0f - rgba.Z) * t),
            rgba.W);
    }

    /// <summary>Draw a fill with a lit top edge settling to the base colour at
    /// the bottom, plus a hairline highlight along the top. ImGui cannot round
    /// a gradient, so a rounded bar keeps the plain flat fill.</summary>
    protected void AddBarFill(
        ImDrawListPtr drawList, Vector2 min, Vector2 max, Vector4 fill, float rounding)
    {
        if (rounding >= 0.5f)
        {
            drawList.AddRectFilled(min, max, ToColor(fill), rounding);
            return;
        }

        var top = ToColor(Lighten(fill, 0.30f));
        var bottom = ToColor(fill);
        drawList.AddRectFilledMultiColor(min, max, top, top, bottom, bottom);

        var sheen = ToColor(WithAlpha(Lighten(fill, 0.75f), fill.W * 0.5f));
        drawList.AddLine(min + new Vector2(0.0f, 0.5f), new Vector2(max.X, min.Y + 0.5f), sheen);
    }

    /// <summary>Trim text to fit a width, ending it with an ellipsis when it
    /// had to be cut. Measured with the current font. Width only grows as
    /// characters are appended, so the fitting prefix is bisected: a
    /// one-character walk is quadratic on long strings, and wire text can be
    /// long even after the bridge's clamp.</summary>
    protected static string Elide(string text, float maxWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maxWidth)
        {
            return text;
        }

        const string Ellipsis = "…";
        var budget = Math.Max(maxWidth - ImGui.CalcTextSize(Ellipsis).X, 0.0f);

        // The empty prefix always fits; the full text is known not to.
        var fits = 0;
        var tooLong = text.Length;
        while (tooLong - fits > 1)
        {
            var mid = fits + ((tooLong - fits) / 2);
            if (ImGui.CalcTextSize(text[..mid]).X <= budget)
            {
                fits = mid;
            }
            else
            {
                tooLong = mid;
            }
        }

        // One char less always fits, so a cut that would split a surrogate
        // pair backs off the leading half rather than draw a lone one as a
        // replacement glyph.
        if (fits > 0 && char.IsHighSurrogate(text[fits - 1]) && char.IsLowSurrogate(text[fits]))
        {
            fits--;
        }

        return string.Concat(text.AsSpan(0, fits), Ellipsis);
    }

    /// <summary>Draw text with the box's configured effect. Overlay text floats
    /// over the game with little or no backdrop, and unadorned text washes out
    /// over bright arenas; the outline or glow is what keeps a callout readable
    /// mid-pull. The colour's own alpha carries any fade, and the effect's
    /// opacity is scaled by it so the two never split visually.</summary>
    protected void DrawStyledText(ImDrawListPtr drawList, Vector2 pos, Vector4 color, string text)
    {
        var alpha = Math.Clamp(color.W, 0.0f, 1.0f);
        var thickness = Math.Clamp(this.EffectThickness, 0, 4);
        var effect = this.EffectColor;
        var effectAlpha = effect.W * alpha * this.Fade;

        if (thickness > 0 && effectAlpha > 0.0f)
        {
            switch (this.TextEffect)
            {
                // Stamps on concentric rings: a filled circle stays round
                // where a filled square grid leaves blocky corners.
                case TextEffectStyle.Outline:
                {
                    var ink = ImGui.GetColorU32(new Vector4(effect.X, effect.Y, effect.Z, effectAlpha));
                    for (var radius = 1; radius <= thickness; radius++)
                    {
                        StampRing(drawList, pos, text, ink, radius);
                    }

                    break;
                }

                // Wider, fainter rings stacked outward: the overlap reads as a
                // soft halo rather than a hard edge.
                case TextEffectStyle.Glow:
                {
                    for (var radius = thickness + 2; radius >= 1; radius--)
                    {
                        var fade = 1.0f - ((float)radius / (thickness + 3.0f));
                        var ink = ImGui.GetColorU32(
                            new Vector4(effect.X, effect.Y, effect.Z, effectAlpha * fade * 0.4f));
                        StampRing(drawList, pos, text, ink, radius);
                    }

                    break;
                }
            }
        }

        drawList.AddText(pos, ToColor(color), text);
    }

    /// <summary>Stamp the text around a circle of the given radius. The stamp
    /// count grows with the radius so wider rings have no gaps.</summary>
    private static void StampRing(ImDrawListPtr drawList, Vector2 pos, string text, uint color, int radius)
    {
        var steps = Math.Max(8, radius * 8);
        for (var i = 0; i < steps; i++)
        {
            var angle = (Math.PI * 2.0 * i) / steps;
            var offset = new Vector2(
                (float)Math.Round(Math.Cos(angle) * radius),
                (float)Math.Round(Math.Sin(angle) * radius));
            drawList.AddText(pos + offset, color, text);
        }
    }
}
