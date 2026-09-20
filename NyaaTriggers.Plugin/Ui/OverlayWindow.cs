using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace NyaaTriggers.Plugin.Ui;

/// <summary>Locked windows have no frame and pass clicks through to the game. Unlocked
/// windows can be moved and resized and show sample content when empty.</summary>
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

    /// <summary>Ignore small geometry changes caused by rendering jitter.</summary>
    private const float GeometryEpsilon = 0.5f;

    /// <summary>Apply reset geometry on the next PreDraw, including while unlocked. A
    /// hidden window keeps this pending until shown.</summary>
    private bool forceGeometry;

    protected OverlayWindow(string name, Configuration config, ScaledFonts fonts)
        : base(name)
    {
        this.Config = config;
        this.Fonts = fonts;

        // Settings control visibility. Escape and the close button must not hide a window
        // whose IsOpen is set each frame.
        this.RespectCloseHotkey = false;
        this.ShowCloseButton = false;
        this.DisableWindowSounds = true;
    }

    protected Configuration Config { get; }

    protected ScaledFonts Fonts { get; }

    protected abstract Vector2 StoredPosition { get; set; }

    protected abstract Vector2 StoredSize { get; set; }

    protected abstract float TextScale { get; }

    protected abstract float BgOpacity { get; }

    protected abstract TextEffectStyle TextEffect { get; }

    /// <summary>Applied to every colour through ToColor, including the backdrop.</summary>
    protected virtual float FadeOpacity => 1.0f;

    /// <summary>Keep a minimum opacity so the window remains visible for
    /// placement.</summary>
    private float Fade => Math.Clamp(this.FadeOpacity, 0.05f, 1.0f);

    protected abstract int EffectThickness { get; }

    protected abstract Vector4 EffectColor { get; }

    /// <summary>Effective text size in screen pixels, set before DrawContent. Smaller
    /// captions derive their size from it.</summary>
    protected float TextPx { get; private set; }

    /// <summary>Keep text within the settings range covered by the font atlas.</summary>
    protected static float ClampTextScale(float scale) => Math.Clamp(scale, 0.5f, 6.0f);

    public override void PreDraw()
    {
        var locked = this.Config.Locked;
        this.Flags = locked ? LockedFlags : UnlockedFlags;

        // Locked windows use NoBackground and draw their backdrop separately.
        ImGui.SetNextWindowBgAlpha(Math.Clamp(this.BgOpacity, 0.0f, 1.0f) * this.Fade);

        // Divide stored size by GlobalScale because Dalamud scales Size but leaves Position
        // unchanged. Otherwise captured sizes grow again on each reload.
        this.Position = this.StoredPosition;
        this.Size = this.StoredSize / ImGuiHelpers.GlobalScale;

        // Apply geometry every frame while locked and once after reset. Unlocked windows
        // must remain draggable.
        var condition = locked || this.forceGeometry ? ImGuiCond.Always : ImGuiCond.FirstUseEver;
        this.forceGeometry = false;
        this.PositionCondition = condition;
        this.SizeCondition = condition;
    }

    internal abstract void ResetGeometry();

    /// <summary>Apply the updated stored geometry on the next PreDraw.</summary>
    protected void ForceGeometry() => this.forceGeometry = true;

    public override void Draw()
    {
        var scale = ClampTextScale(this.TextScale);

        var targetPx = FontBasePixels() * scale;
        this.TextPx = targetPx;

        using (this.UseFont(targetPx))
        {
            // Each window reserves its own spacing.
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
            try
            {
                this.DrawBackdrop();
                this.DrawContent();
            }
            finally
            {
                ImGui.PopStyleVar();
            }
        }

        if (!this.Config.Locked)
        {
            this.CaptureGeometry();
        }
    }

    protected abstract void DrawContent();

    /// <summary>Keep pixel sizes stable as fonts load and restore the surrounding scale
    /// after drawing nested text.</summary>
    protected FontScope UseFont(float pixels) => new(this.Fonts.Get(pixels), pixels);

    private static float FontBasePixels()
    {
        var font = ImGui.GetFont();
        return Math.Max(1.0f, font.FontSize * font.Scale * ImGuiHelpers.GlobalScale);
    }

    protected readonly struct FontScope : IDisposable
    {
        private readonly IDisposable? pushed;
        private readonly float restore;

        internal FontScope(IFontHandle? handle, float pixels)
        {
            this.restore = ImGui.GetFontSize() / FontBasePixels();
            this.pushed = handle is { Available: true } ? handle.Push() : null;
            ImGui.SetWindowFontScale(pixels / FontBasePixels());
        }

        public void Dispose()
        {
            try
            {
                this.pushed?.Dispose();
            }
            finally
            {
                ImGui.SetWindowFontScale(this.restore);
            }
        }
    }

    /// <summary>Draw the configured backdrop because locked windows disable the ImGui
    /// background.</summary>
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

    /// <summary>Track geometry in memory. Persist it when locking or unloading, rather than
    /// on every drag frame.</summary>
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

    /// <summary>Apply the window fade when converting RGBA to an ImGui colour.</summary>
    protected uint ToColor(Vector4 rgba) => ImGui.ColorConvertFloat4ToU32(WithAlpha(rgba, this.Fade));

    protected static Vector4 WithAlpha(Vector4 rgba, float alpha)
        => new(rgba.X, rgba.Y, rgba.Z, rgba.W * Math.Clamp(alpha, 0.0f, 1.0f));

    /// <summary>Blend toward white while preserving alpha.</summary>
    protected static Vector4 Lighten(Vector4 rgba, float amount)
    {
        var t = Math.Clamp(amount, 0.0f, 1.0f);
        return new(
            rgba.X + ((1.0f - rgba.X) * t),
            rgba.Y + ((1.0f - rgba.Y) * t),
            rgba.Z + ((1.0f - rgba.Z) * t),
            rgba.W);
    }

    /// <summary>Draw a vertical gradient with a highlighted top edge. Rounded bars use a
    /// flat fill because this gradient cannot be rounded.</summary>
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

    /// <summary>Fit text with an ellipsis using the current font. Binary search avoids
    /// measuring every prefix of a long string.</summary>
    protected static string Elide(string text, float maxWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maxWidth)
        {
            return text;
        }

        const string Ellipsis = "…";
        var budget = Math.Max(maxWidth - ImGui.CalcTextSize(Ellipsis).X, 0.0f);

        // The empty prefix fits and the full text does not.
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

        // Keep surrogate pairs intact when truncating.
        if (fits > 0 && char.IsHighSurrogate(text[fits - 1]) && char.IsLowSurrogate(text[fits]))
        {
            fits--;
        }

        return string.Concat(text.AsSpan(0, fits), Ellipsis);
    }

    /// <summary>Draw text with its configured effect. Scale effect opacity by text alpha so
    /// both fade together.</summary>
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
                // Use circular stamps to keep outline corners rounded.
                case TextEffectStyle.Outline:
                {
                    var ink = ImGui.GetColorU32(new Vector4(effect.X, effect.Y, effect.Z, effectAlpha));
                    for (var radius = 1; radius <= thickness; radius++)
                    {
                        StampRing(drawList, pos, text, ink, radius);
                    }

                    break;
                }

                // Reduce opacity toward the outer rings for a soft glow.
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

    /// <summary>Increase stamp count with radius to avoid gaps in the ring.</summary>
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
