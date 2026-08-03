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

        this.Size = new Vector2(420, 560);
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
        this.DrawLink();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        this.DrawWindows();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        this.DrawAppearance();
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
        ImGui.TextUnformatted("What to draw");
        ImGui.Spacing();

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
    }

    private void DrawAppearance()
    {
        ImGui.TextUnformatted("Appearance");
        ImGui.Spacing();

        // Backdrop behind each box's content, 0 = invisible. Stored 0..1 but
        // shown as a percent (SliderFloat formats the raw value).
        var bgPct = this.config.BgOpacity * 100.0f;
        if (ImGui.SliderFloat("Background", ref bgPct, 0.0f, 100.0f, "%.0f%%"))
        {
            this.config.BgOpacity = bgPct / 100.0f;
        }

        this.SaveIfDragEnded();

        // Every slider applies live but saves only when the drag ends: they
        // report a change every frame while held, and writing the config file
        // at frame rate would be absurd.
        var barScale = this.config.TimelineTextScale;
        if (ImGui.SliderFloat("Bar text size", ref barScale, 0.5f, 3.0f, "%.2fx"))
        {
            this.config.TimelineTextScale = barScale;
        }

        this.SaveIfDragEnded();

        var alertScale = this.config.AlertsTextScale;
        if (ImGui.SliderFloat("Alert text size", ref alertScale, 0.5f, 3.0f, "%.2fx"))
        {
            this.config.AlertsTextScale = alertScale;
        }

        this.SaveIfDragEnded();

        var barHeight = this.config.BarHeight;
        if (ImGui.SliderFloat("Bar height", ref barHeight, 12.0f, 48.0f, "%.0f px"))
        {
            this.config.BarHeight = barHeight;
        }

        this.SaveIfDragEnded();

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

        var seconds = this.config.AlertSeconds;
        if (ImGui.SliderFloat("Alert time", ref seconds, 0.5f, 15.0f, "%.1f s"))
        {
            this.config.AlertSeconds = seconds;
        }

        this.SaveIfDragEnded();

        ImGui.Spacing();

        this.ColorRow("Bar", () => this.config.ColorBar, v => this.config.ColorBar = v);
        this.ColorRow("Bar text", () => this.config.ColorBarText, v => this.config.ColorBarText = v);
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
