using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using NyaaTriggers.Plugin.Bridge;

namespace NyaaTriggers.Plugin.Ui;

internal sealed class ConfigWindow : Window
{
    private static readonly Vector4 Good = new(0.45f, 0.85f, 0.50f, 1.0f);
    private static readonly Vector4 Waiting = new(0.85f, 0.75f, 0.40f, 1.0f);
    private static readonly Vector4 Bad = new(0.90f, 0.40f, 0.40f, 1.0f);

    private static readonly string[] EffectNames = { "None", "Outline", "Glow" };
    private static readonly string[] FillNames = { "Deplete (time left)", "Fill (time elapsed)" };
    private static readonly string[] CountdownNames = { "Hidden", "Whole seconds", "Tenths" };
    private static readonly string[] OrderNames = { "Newest at top", "Oldest at top" };
    private static readonly string[] AlignNames = { "Left", "Center", "Right" };
    private static readonly string[] DpsStyleNames = { "Bars", "Horizoverlay", "Kagerou" };
    private static readonly string[] HorizThemeNames = { "Color by role", "Black & white" };

    private readonly Configuration config;
    private readonly BridgeHost bridge;
    private readonly PluginUi ui;

    /// <summary>Edited separately from the live setting: rebinding the listener
    /// on every keystroke would thrash the socket while a port is typed.</summary>
    private int pendingPort;

    internal ConfigWindow(Configuration config, BridgeHost bridge, PluginUi ui)
        : base("NyaaTriggers###nyaaConfig")
    {
        this.config = config;
        this.bridge = bridge;
        this.ui = ui;
        this.pendingPort = config.Port;

        this.Size = new Vector2(460, 640);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(360, 320),
            MaximumSize = new Vector2(900, 1400),
        };

        // Explicit so it cannot drift with a Dalamud default: this window is
        // opened and closed by the user, unlike the overlay boxes (which hide
        // theirs because IsOpen is rewritten every frame).
        this.ShowCloseButton = true;
        this.DisableWindowSounds = true;
    }

    public override void Draw()
    {
        // The sections scroll in their own region, the reset button pinned
        // below it. Scrolling at the window level instead let a collapsed
        // header land on the window's bottom resize border, where most of
        // the bar dragged a resize and only the arrow toggled the section.
        if (ImGui.BeginChild("##sections", new Vector2(0.0f, -ImGui.GetFrameHeightWithSpacing())))
        {
            if (ImGui.CollapsingHeader("Link", ImGuiTreeNodeFlags.DefaultOpen))
            {
                this.DrawLink();
                ImGui.Spacing();
            }

            if (ImGui.CollapsingHeader("Boxes", ImGuiTreeNodeFlags.DefaultOpen))
            {
                this.DrawBoxes();
                ImGui.Spacing();
            }

            if (ImGui.CollapsingHeader("Timeline"))
            {
                this.DrawTimeline();
                ImGui.Spacing();
            }

            if (ImGui.CollapsingHeader("Alerts"))
            {
                this.DrawAlerts();
                ImGui.Spacing();
            }

            if (ImGui.CollapsingHeader("DPS meter"))
            {
                this.DrawDps();
                ImGui.Spacing();
            }
        }

        ImGui.EndChild();

        if (ImGui.Button("Reset appearance"))
        {
            this.config.ResetAppearance();
            this.config.Save();
        }
    }

    private void DrawLink()
    {
        var error = this.bridge.LastError;
        if (error != null)
        {
            ImGui.TextColored(Bad, $"Not listening: {error}");
            ImGui.TextWrapped(
                "Another program is probably already on this port. Pick a different " +
                "one here and set the same port in the app, on its Settings page " +
                "under In-Game Overlay.");
        }
        else if (this.bridge.IsConnected)
        {
            ImGui.TextColored(Good, "Connected to the app.");
        }
        else
        {
            ImGui.TextColored(Waiting, $"Listening on 127.0.0.1:{this.config.Port}, waiting for the app.");
        }

        ImGui.Spacing();

        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Port", ref this.pendingPort);

        // Clamping here every frame would fight the field's own text buffer: a
        // half-typed "80" or "99999" would be silently rewritten to 1024/65535
        // while the box still showed what was typed, and Apply would bind a
        // port the user never chose. Clamp on Apply instead.
        ImGui.SameLine();
        var changed = this.pendingPort != this.config.Port;
        if (!changed)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Apply"))
        {
            this.pendingPort = Math.Clamp(this.pendingPort, 1024, 65535);
            this.config.Port = this.pendingPort;
            this.config.Save();
            this.bridge.Restart();
        }

        if (!changed)
        {
            ImGui.EndDisabled();
        }

        if (this.pendingPort is < 1024 or > 65535)
        {
            ImGui.TextColored(Bad, "Port must be between 1024 and 65535.");
        }

        ImGui.TextDisabled("Loopback only. Nothing is reachable from outside this machine.");
    }

    private void DrawBoxes()
    {
        this.Check("Timeline bars", () => this.config.ShowTimeline, v => this.config.ShowTimeline = v);
        this.Check("Alert pop-ups", () => this.config.ShowAlerts, v => this.config.ShowAlerts = v);
        this.Check("DPS meter", () => this.config.ShowDps, v => this.config.ShowDps = v);
        this.Check("Only inside duties", () => this.config.OnlyInDuty, v => this.config.OnlyInDuty = v);

        ImGui.Spacing();

        var locked = this.config.Locked;
        if (ImGui.Checkbox("Lock", ref locked))
        {
            this.ui.SetLocked(locked);
        }

        ImGui.TextDisabled(
            locked
                ? "Locked: no frame, clicks pass through to the game."
                : "Unlocked: drag and resize the boxes. They show sample content while idle.");

        if (ImGui.Button("Test callout"))
        {
            this.bridge.ShowPlaceholder();
        }

        // The test only queues an alert. Saying so beats a button that looks
        // broken because the box it would draw into is currently suppressed.
        if (!this.config.ShowAlerts)
        {
            ImGui.SameLine();
            ImGui.TextColored(Waiting, "Alert pop-ups are off.");
        }
        else if (this.config.Locked && this.config.OnlyInDuty && !this.ui.OverlayVisible)
        {
            ImGui.SameLine();
            ImGui.TextColored(Waiting, "Hidden outside duties.");
        }
    }

    private void DrawTimeline()
    {
        // The same widget labels recur in every box's section; the pushed id
        // keeps ImGui from folding them into one widget.
        ImGui.PushID("timeline");

        this.Slider("Text size", 0.5f, 6.0f, "%.2fx",
            () => this.config.TimelineTextScale, v => this.config.TimelineTextScale = v);
        this.PercentSlider("Background",
            () => this.config.TimelineBgOpacity, v => this.config.TimelineBgOpacity = v);

        ImGui.Spacing();

        this.Slider("Bar height", 12.0f, 48.0f, "%.0f px",
            () => this.config.TimelineBarHeight, v => this.config.TimelineBarHeight = v);
        this.Slider("Bar spacing", 0.0f, 16.0f, "%.0f px",
            () => this.config.TimelineBarSpacing, v => this.config.TimelineBarSpacing = v);
        this.Slider("Corner rounding", 0.0f, 12.0f, "%.0f px",
            () => this.config.TimelineBarRounding, v => this.config.TimelineBarRounding = v);
        this.Slider("Border thickness", 0.0f, 4.0f, "%.0f px",
            () => this.config.TimelineBarBorderThickness, v => this.config.TimelineBarBorderThickness = v);
        this.PercentSlider("Track opacity",
            () => this.config.TimelineBarTrackOpacity, v => this.config.TimelineBarTrackOpacity = v);
        ImGui.TextDisabled("The full-length slot under the fill.");

        this.Combo("Fill direction", FillNames, () => this.config.BarFill, v => this.config.BarFill = v);
        this.Check("Anchor fill to the right",
            () => this.config.BarRightToLeft, v => this.config.BarRightToLeft = v);
        this.Combo("Bar text alignment", AlignNames,
            () => this.config.BarTextAlign, v => this.config.BarTextAlign = v);
        this.Combo("Countdown", CountdownNames,
            () => this.config.Countdown, v => this.config.Countdown = v);
        this.Check("Countdown on the right edge",
            () => this.config.CountdownSplit, v => this.config.CountdownSplit = v);

        this.Slider("Imminent at", 0.0f, 15.0f, "%.0f s",
            () => this.config.ImminentSeconds, v => this.config.ImminentSeconds = v);
        ImGui.TextDisabled("Bars this close to firing switch to the imminent colour.");
        this.Check("Pulse imminent bars",
            () => this.config.ImminentPulse, v => this.config.ImminentPulse = v);

        this.Slider("Look ahead", 5.0f, 120.0f, "%.0f s",
            () => this.config.TimelineWindow, v => this.config.TimelineWindow = v);
        this.SliderInt("Max bars", 1, 12,
            () => this.config.TimelineRows, v => this.config.TimelineRows = v);

        ImGui.Spacing();

        this.ColorRow("Bar", () => this.config.TimelineBarColor, v => this.config.TimelineBarColor = v);
        this.ColorRow("Bar track",
            () => this.config.TimelineBarTrackColor, v => this.config.TimelineBarTrackColor = v);
        this.ColorRow("Bar border",
            () => this.config.TimelineBarBorderColor, v => this.config.TimelineBarBorderColor = v);
        this.ColorRow("Bar text",
            () => this.config.TimelineTextColor, v => this.config.TimelineTextColor = v);
        this.ColorRow("Imminent", () => this.config.ColorImminent, v => this.config.ColorImminent = v);

        ImGui.Spacing();

        this.EffectGroup(
            () => this.config.TimelineTextEffect, v => this.config.TimelineTextEffect = v,
            () => this.config.TimelineEffectThickness, v => this.config.TimelineEffectThickness = v,
            () => this.config.TimelineEffectColor, v => this.config.TimelineEffectColor = v);

        ImGui.PopID();
    }

    private void DrawAlerts()
    {
        ImGui.PushID("alerts");

        this.Slider("Text size", 0.5f, 6.0f, "%.2fx",
            () => this.config.AlertsTextScale, v => this.config.AlertsTextScale = v);
        this.PercentSlider("Background",
            () => this.config.AlertsBgOpacity, v => this.config.AlertsBgOpacity = v);

        ImGui.Spacing();

        this.Slider("Alert time", 0.5f, 15.0f, "%.1f s",
            () => this.config.AlertSeconds, v => this.config.AlertSeconds = v);
        this.SliderInt("Max visible", 1, 8,
            () => this.config.AlertsMaxVisible, v => this.config.AlertsMaxVisible = v);
        this.Combo("Stack order", OrderNames,
            () => this.config.AlertOrder, v => this.config.AlertOrder = v);
        this.Combo("Text alignment", AlignNames,
            () => this.config.AlertsAlign, v => this.config.AlertsAlign = v);
        this.Check("Fade in and out", () => this.config.AlertsAnimate, v => this.config.AlertsAnimate = v);

        ImGui.Spacing();

        this.ColorRow("Info", () => this.config.ColorInfo, v => this.config.ColorInfo = v);
        this.ColorRow("Alert", () => this.config.ColorAlert, v => this.config.ColorAlert = v);
        this.ColorRow("Alarm", () => this.config.ColorAlarm, v => this.config.ColorAlarm = v);

        ImGui.Spacing();

        this.EffectGroup(
            () => this.config.AlertsTextEffect, v => this.config.AlertsTextEffect = v,
            () => this.config.AlertsEffectThickness, v => this.config.AlertsEffectThickness = v,
            () => this.config.AlertsEffectColor, v => this.config.AlertsEffectColor = v);

        ImGui.PopID();
    }

    private void DrawDps()
    {
        ImGui.PushID("dps");

        this.Combo("Style", DpsStyleNames, () => this.config.DpsStyle, v => this.config.DpsStyle = v);
        this.Combo("Horizoverlay colors", HorizThemeNames,
            () => this.config.DpsHorizTheme, v => this.config.DpsHorizTheme = v);
        this.Slider("Text size", 0.5f, 6.0f, "%.2fx",
            () => this.config.DpsTextScale, v => this.config.DpsTextScale = v);
        this.PercentSlider("Background",
            () => this.config.DpsBgOpacity, v => this.config.DpsBgOpacity = v);
        this.Check("Only show yourself",
            () => this.config.DpsSoloOnly, v => this.config.DpsSoloOnly = v);
        this.SliderInt("Max combatants", 1, 24,
            () => this.config.DpsMaxRows, v => this.config.DpsMaxRows = v);

        ImGui.Spacing();

        ImGui.TextDisabled("Horizoverlay style.");
        this.Check("Rank numbers",
            () => this.config.DpsHorizShowRank, v => this.config.DpsHorizShowRank = v);
        this.Check("Job icons",
            () => this.config.DpsHorizShowIcons, v => this.config.DpsHorizShowIcons = v);
        this.Check("HPS",
            () => this.config.DpsHorizShowHps, v => this.config.DpsHorizShowHps = v);
        ImGui.TextDisabled("Off shows the job acronym in its slot.");
        this.Check("Two-tone highlight",
            () => this.config.DpsHorizHighlight, v => this.config.DpsHorizHighlight = v);
        this.Check("Damage %",
            () => this.config.DpsHorizShowPercent, v => this.config.DpsHorizShowPercent = v);
        this.Slider("Cell padding", 0.0f, 24.0f, "%.0f px",
            () => this.config.DpsHorizCellPadding, v => this.config.DpsHorizCellPadding = v);
        this.PercentSlider("Bar opacity",
            () => this.config.DpsHorizBarOpacity, v => this.config.DpsHorizBarOpacity = v);

        ImGui.Spacing();

        ImGui.TextDisabled("The encounter line, above the rows and under the strip.");
        this.Check("Encounter line",
            () => this.config.DpsShowHeader, v => this.config.DpsShowHeader = v);
        this.Check("Duration",
            () => this.config.DpsHeaderDuration, v => this.config.DpsHeaderDuration = v);
        this.Check("Total DPS",
            () => this.config.DpsHeaderTotalDps, v => this.config.DpsHeaderTotalDps = v);

        ImGui.Spacing();

        this.Slider("Bar height", 12.0f, 48.0f, "%.0f px",
            () => this.config.DpsBarHeight, v => this.config.DpsBarHeight = v);
        this.Slider("Bar spacing", 0.0f, 16.0f, "%.0f px",
            () => this.config.DpsBarSpacing, v => this.config.DpsBarSpacing = v);
        this.Slider("Corner rounding", 0.0f, 12.0f, "%.0f px",
            () => this.config.DpsBarRounding, v => this.config.DpsBarRounding = v);
        this.Slider("Border thickness", 0.0f, 4.0f, "%.0f px",
            () => this.config.DpsBarBorderThickness, v => this.config.DpsBarBorderThickness = v);
        this.PercentSlider("Track opacity",
            () => this.config.DpsBarTrackOpacity, v => this.config.DpsBarTrackOpacity = v);
        ImGui.TextDisabled("The full-length slot under the fill.");
        this.Check("Anchor fill to the right",
            () => this.config.DpsBarRightToLeft, v => this.config.DpsBarRightToLeft = v);

        ImGui.Spacing();

        this.ColorRow("Bar", () => this.config.DpsBarColor, v => this.config.DpsBarColor = v);
        this.ColorRow("Bar track",
            () => this.config.DpsBarTrackColor, v => this.config.DpsBarTrackColor = v);
        this.ColorRow("Bar border",
            () => this.config.DpsBarBorderColor, v => this.config.DpsBarBorderColor = v);
        this.ColorRow("Bar text", () => this.config.DpsTextColor, v => this.config.DpsTextColor = v);
        ImGui.TextDisabled("Bars style only; Horizoverlay colours by role, Kagerou by job.");

        ImGui.Spacing();

        this.EffectGroup(
            () => this.config.DpsTextEffect, v => this.config.DpsTextEffect = v,
            () => this.config.DpsEffectThickness, v => this.config.DpsEffectThickness = v,
            () => this.config.DpsEffectColor, v => this.config.DpsEffectColor = v);

        ImGui.PopID();
    }

    /// <summary>The text readability knobs every box carries: which effect,
    /// how far it reaches, and its colour.</summary>
    private void EffectGroup(
        Func<TextEffectStyle> getEffect, Action<TextEffectStyle> setEffect,
        Func<int> getThickness, Action<int> setThickness,
        Func<Vector4> getColor, Action<Vector4> setColor)
    {
        this.Combo("Text effect", EffectNames, getEffect, setEffect);
        this.SliderInt("Effect thickness", 0, 4, getThickness, setThickness);
        this.ColorRow("Effect color", getColor, setColor);
    }

    private void Slider(string label, float min, float max, string format, Func<float> get, Action<float> set)
    {
        var value = get();
        if (ImGui.SliderFloat(label, ref value, min, max, format))
        {
            set(value);
        }

        this.SaveIfDragEnded();
    }

    private void SliderInt(string label, int min, int max, Func<int> get, Action<int> set)
    {
        var value = get();
        if (ImGui.SliderInt(label, ref value, min, max))
        {
            set(value);
        }

        this.SaveIfDragEnded();
    }

    /// <summary>Stored 0..1 but shown as a percent (SliderFloat formats the
    /// raw value).</summary>
    private void PercentSlider(string label, Func<float> get, Action<float> set)
    {
        var value = get() * 100.0f;
        if (ImGui.SliderFloat(label, ref value, 0.0f, 100.0f, "%.0f%%"))
        {
            set(value / 100.0f);
        }

        this.SaveIfDragEnded();
    }

    private void Check(string label, Func<bool> get, Action<bool> set)
    {
        var value = get();
        if (ImGui.Checkbox(label, ref value))
        {
            set(value);
            this.config.Save();
        }
    }

    private void Combo<T>(string label, string[] names, Func<T> get, Action<T> set)
        where T : struct, Enum
    {
        // Clamped rather than trusted: a hand-edited or downgraded config can
        // carry an enum value past the end of the names list.
        var index = Math.Clamp(Convert.ToInt32(get()), 0, names.Length - 1);
        if (ImGui.Combo(label, ref index, names, names.Length))
        {
            set((T)Enum.ToObject(typeof(T), index));
            this.config.Save();
        }
    }

    private void ColorRow(string label, Func<Vector4> get, Action<Vector4> set)
    {
        var value = get();
        if (ImGui.ColorEdit4(label, ref value, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
        {
            set(value);
        }

        this.SaveIfDragEnded();
    }

    /// <summary>Persist once the widget just above stopped being edited.</summary>
    private void SaveIfDragEnded()
    {
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            this.config.Save();
        }
    }
}
