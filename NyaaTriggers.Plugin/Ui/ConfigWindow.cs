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

    private static readonly string[] EffectNames = { "None", "Shadow", "Outline" };
    private static readonly string[] FillNames = { "Deplete (time left)", "Fill (time elapsed)" };
    private static readonly string[] CountdownNames = { "Hidden", "Whole seconds", "Tenths" };
    private static readonly string[] OrderNames = { "Newest at top", "Oldest at top" };
    private static readonly string[] AlignNames = { "Left", "Center", "Right" };

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
        if (ImGui.CollapsingHeader("Link", ImGuiTreeNodeFlags.DefaultOpen))
        {
            this.DrawLink();
            ImGui.Spacing();
        }

        if (ImGui.CollapsingHeader("Boxes", ImGuiTreeNodeFlags.DefaultOpen))
        {
            this.DrawWindows();
            ImGui.Spacing();
        }

        if (ImGui.CollapsingHeader("Timeline bars"))
        {
            this.DrawTimeline();
            ImGui.Spacing();
        }

        if (ImGui.CollapsingHeader("Alerts"))
        {
            this.DrawAlerts();
            ImGui.Spacing();
        }

        if (ImGui.CollapsingHeader("Text"))
        {
            this.DrawText();
            ImGui.Spacing();
        }

        if (ImGui.CollapsingHeader("Colors"))
        {
            this.DrawColors();
            ImGui.Spacing();
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
                "one here and set the same port in the app.");
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

    private void DrawWindows()
    {
        var timeline = this.config.ShowTimeline;
        if (ImGui.Checkbox("Timeline bars", ref timeline))
        {
            this.config.ShowTimeline = timeline;
            this.config.Save();
        }

        var alerts = this.config.ShowAlerts;
        if (ImGui.Checkbox("Alert pop-ups", ref alerts))
        {
            this.config.ShowAlerts = alerts;
            this.config.Save();
        }

        var onlyInDuty = this.config.OnlyInDuty;
        if (ImGui.Checkbox("Only inside duties", ref onlyInDuty))
        {
            this.config.OnlyInDuty = onlyInDuty;
            this.config.Save();
        }

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

        ImGui.Spacing();

        // Backdrop behind each box's content, 0 = invisible. Stored 0..1 but
        // shown as a percent (SliderFloat formats the raw value).
        var timelineBg = this.config.TimelineBgOpacity * 100.0f;
        if (ImGui.SliderFloat("Timeline background", ref timelineBg, 0.0f, 100.0f, "%.0f%%"))
        {
            this.config.TimelineBgOpacity = timelineBg / 100.0f;
        }

        this.SaveIfDragEnded();

        var alertsBg = this.config.AlertsBgOpacity * 100.0f;
        if (ImGui.SliderFloat("Alerts background", ref alertsBg, 0.0f, 100.0f, "%.0f%%"))
        {
            this.config.AlertsBgOpacity = alertsBg / 100.0f;
        }

        this.SaveIfDragEnded();
    }

    private void DrawTimeline()
    {
        var barScale = this.config.TimelineTextScale;
        if (ImGui.SliderFloat("Bar text size", ref barScale, 0.5f, 3.0f, "%.2fx"))
        {
            this.config.TimelineTextScale = barScale;
        }

        this.SaveIfDragEnded();

        var barHeight = this.config.BarHeight;
        if (ImGui.SliderFloat("Bar height", ref barHeight, 12.0f, 48.0f, "%.0f px"))
        {
            this.config.BarHeight = barHeight;
        }

        this.SaveIfDragEnded();

        var spacing = this.config.BarSpacing;
        if (ImGui.SliderFloat("Bar spacing", ref spacing, 0.0f, 16.0f, "%.0f px"))
        {
            this.config.BarSpacing = spacing;
        }

        this.SaveIfDragEnded();

        var rounding = this.config.BarRounding;
        if (ImGui.SliderFloat("Corner rounding", ref rounding, 0.0f, 12.0f, "%.0f px"))
        {
            this.config.BarRounding = rounding;
        }

        this.SaveIfDragEnded();

        var border = this.config.BarBorderThickness;
        if (ImGui.SliderFloat("Border thickness", ref border, 0.0f, 4.0f, "%.0f px"))
        {
            this.config.BarBorderThickness = border;
        }

        this.SaveIfDragEnded();

        var track = this.config.BarTrackOpacity * 100.0f;
        if (ImGui.SliderFloat("Track opacity", ref track, 0.0f, 100.0f, "%.0f%%"))
        {
            this.config.BarTrackOpacity = track / 100.0f;
        }

        this.SaveIfDragEnded();
        ImGui.TextDisabled("The faint full-length bar under the fill.");

        this.Combo("Fill direction", FillNames, () => this.config.BarFill, v => this.config.BarFill = v);

        var rtl = this.config.BarRightToLeft;
        if (ImGui.Checkbox("Anchor fill to the right", ref rtl))
        {
            this.config.BarRightToLeft = rtl;
            this.config.Save();
        }

        this.Combo("Bar text alignment", AlignNames, () => this.config.BarTextAlign, v => this.config.BarTextAlign = v);

        this.Combo("Countdown", CountdownNames, () => this.config.Countdown, v => this.config.Countdown = v);

        var split = this.config.CountdownSplit;
        if (ImGui.Checkbox("Countdown on the right edge", ref split))
        {
            this.config.CountdownSplit = split;
            this.config.Save();
        }

        var imminent = this.config.ImminentSeconds;
        if (ImGui.SliderFloat("Imminent at", ref imminent, 0.0f, 15.0f, "%.0f s"))
        {
            this.config.ImminentSeconds = imminent;
        }

        this.SaveIfDragEnded();
        ImGui.TextDisabled("Bars this close to firing switch to the imminent colour.");

        var pulse = this.config.ImminentPulse;
        if (ImGui.Checkbox("Pulse imminent bars", ref pulse))
        {
            this.config.ImminentPulse = pulse;
            this.config.Save();
        }

        var window = this.config.TimelineWindow;
        if (ImGui.SliderFloat("Look ahead", ref window, 5.0f, 120.0f, "%.0f s"))
        {
            this.config.TimelineWindow = window;
        }

        this.SaveIfDragEnded();

        var rows = this.config.TimelineRows;
        if (ImGui.SliderInt("Max bars", ref rows, 1, 12))
        {
            this.config.TimelineRows = rows;
        }

        this.SaveIfDragEnded();
    }

    private void DrawAlerts()
    {
        var alertScale = this.config.AlertsTextScale;
        if (ImGui.SliderFloat("Alert text size", ref alertScale, 0.5f, 3.0f, "%.2fx"))
        {
            this.config.AlertsTextScale = alertScale;
        }

        this.SaveIfDragEnded();

        var seconds = this.config.AlertSeconds;
        if (ImGui.SliderFloat("Alert time", ref seconds, 0.5f, 15.0f, "%.1f s"))
        {
            this.config.AlertSeconds = seconds;
        }

        this.SaveIfDragEnded();

        var visible = this.config.AlertsMaxVisible;
        if (ImGui.SliderInt("Max visible", ref visible, 1, 8))
        {
            this.config.AlertsMaxVisible = visible;
        }

        this.SaveIfDragEnded();

        this.Combo("Stack order", OrderNames, () => this.config.AlertOrder, v => this.config.AlertOrder = v);

        this.Combo("Text alignment", AlignNames, () => this.config.AlertsAlign, v => this.config.AlertsAlign = v);

        var animate = this.config.AlertsAnimate;
        if (ImGui.Checkbox("Fade in and out", ref animate))
        {
            this.config.AlertsAnimate = animate;
            this.config.Save();
        }
    }

    private void DrawText()
    {
        var highQuality = this.config.HighQualityText;
        if (ImGui.Checkbox("High quality text", ref highQuality))
        {
            this.config.HighQualityText = highQuality;
            this.config.Save();
        }

        ImGui.TextDisabled(
            "Rasterizes text at its real size instead of stretching it. " +
            "Turn off if text ever looks wrong.");

        ImGui.Spacing();

        this.Combo("Text effect", EffectNames, () => this.config.TextEffect, v => this.config.TextEffect = v);

        var thickness = this.config.OutlineThickness;
        if (ImGui.SliderInt("Effect thickness", ref thickness, 0, 4))
        {
            this.config.OutlineThickness = thickness;
        }

        this.SaveIfDragEnded();

        this.ColorRow("Effect color", () => this.config.ColorOutline, v => this.config.ColorOutline = v);
    }

    private void DrawColors()
    {
        this.ColorRow("Bar", () => this.config.ColorBar, v => this.config.ColorBar = v);
        this.ColorRow("Bar text", () => this.config.ColorBarText, v => this.config.ColorBarText = v);
        this.ColorRow("Bar border", () => this.config.ColorBarBorder, v => this.config.ColorBarBorder = v);
        this.ColorRow("Imminent", () => this.config.ColorImminent, v => this.config.ColorImminent = v);
        this.ColorRow("Info", () => this.config.ColorInfo, v => this.config.ColorInfo = v);
        this.ColorRow("Alert", () => this.config.ColorAlert, v => this.config.ColorAlert = v);
        this.ColorRow("Alarm", () => this.config.ColorAlarm, v => this.config.ColorAlarm = v);

        ImGui.Spacing();
        if (ImGui.Button("Reset appearance"))
        {
            this.config.ResetAppearance();
            this.config.Save();
        }
    }

    private void Combo<T>(string label, string[] names, Func<T> get, Action<T> set)
        where T : struct, Enum
    {
        var index = Convert.ToInt32(get());
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
