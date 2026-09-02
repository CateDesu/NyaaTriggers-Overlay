using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using NyaaTriggers.Plugin.Bridge;
using NyaaTriggers.Plugin.Meter;

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
    private static readonly string[] DpsStyleNames = { "Bars", "Horizon Overlay", "Kagerou" };
    private static readonly string[] HorizonThemeNames = { "Color by role", "Black & white" };
    private static readonly string[] PrivacyNames = { "Shown", "Initials only", "Hidden" };
    private static readonly string[] DpsSortNames = { "By DPS", "Alphabetical", "Tanks, healers, DPS" };

    private readonly Configuration config;
    private readonly BridgeHost bridge;
    private readonly PluginUi ui;

    /// <summary>Edited separately from the live setting: rebinding the listener
    /// on every keystroke would thrash the socket while a port is typed.</summary>
    private int pendingPort;

    /// <summary>Edited separately from the live setting, same reason as the
    /// port: re-dialling IINACT on every keystroke would thrash the feed
    /// while an address is typed.</summary>
    private string pendingEndpoint;

    /// <summary>The profile name being typed. Kept out of the config itself:
    /// it is the editor's scratch, not a setting.</summary>
    private string profileName = string.Empty;

    /// <summary>Result of the last import attempt, so a clipboard that did
    /// not hold a profile says so instead of failing silently.</summary>
    private string? importNote;

    internal ConfigWindow(Configuration config, BridgeHost bridge, PluginUi ui)
        : base("NyaaTriggers###nyaaConfig")
    {
        this.config = config;
        this.bridge = bridge;
        this.ui = ui;
        this.pendingPort = config.Port;
        // A hand-edited config could carry a null; fall back to the default.
        this.pendingEndpoint = config.IinactEndpoint ?? "ws://127.0.0.1:10501/ws";

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

            if (ImGui.CollapsingHeader("Horizon Overlay"))
            {
                this.DrawHorizon();
                ImGui.Spacing();
            }

            if (ImGui.CollapsingHeader("Profiles"))
            {
                this.DrawProfiles();
                ImGui.Spacing();
            }
        }

        ImGui.EndChild();

        if (ImGui.Button("Reset appearance"))
        {
            this.config.ResetAppearance();
            this.config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset placement"))
        {
            this.ui.ResetPlacement();
        }

        ImGui.TextDisabled("Appearance is the palette and sizing. Placement is where the boxes sit.");
    }

    private void DrawLink()
    {
        var error = this.bridge.LastError;
        if (error != null)
        {
            ImGui.TextColored(Bad, $"Not listening: {error}");
            ImGui.TextWrapped(
                "Another program is probably already on this port. Pick a different " +
                "one here and set the same port in the program, on its Settings page " +
                "under In-Game Overlay.");
        }
        else if (this.bridge.IsConnected)
        {
            ImGui.TextColored(Good, "Connected to the program.");
        }
        else
        {
            ImGui.TextColored(Waiting, $"Listening on 127.0.0.1:{this.config.Port}, waiting for the program.");
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

        ImGui.Spacing();
        ImGui.Spacing();

        this.Check("Standalone meter", () => this.config.StandaloneMeter, v => this.config.StandaloneMeter = v);
        ImGui.TextDisabled("Run the meter solely from IINACT.");

        if (this.config.StandaloneMeter)
        {
            switch (this.bridge.StandaloneStatus)
            {
                case StandaloneState.Connected:
                    ImGui.TextColored(Good, "Connected to IINACT.");
                    break;
                case StandaloneState.Error:
                    ImGui.TextColored(Bad, this.bridge.StandaloneStatusText);
                    break;
                case StandaloneState.Paused:
                    ImGui.TextColored(Waiting, "Asleep: the program is connected, its feed wins.");
                    break;
                default:
                    ImGui.TextColored(Waiting, this.bridge.StandaloneStatusText);
                    break;
            }

            ImGui.SetNextItemWidth(260);
            ImGui.InputText("IINACT feed", ref this.pendingEndpoint, 256);

            var endpoint = this.pendingEndpoint.Trim();
            var valid = endpoint.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
                        endpoint.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);

            // Same shape as the port row: no silent rewrites while typing, a
            // bad address blocks Apply instead of being re-dialled.
            ImGui.SameLine();
            var endpointChanged = valid && endpoint != this.config.IinactEndpoint;
            if (!endpointChanged)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.Button("Apply##iinactEndpoint"))
            {
                this.config.IinactEndpoint = endpoint;
                this.config.Save();
                this.bridge.RestartStandalone();
            }

            if (!endpointChanged)
            {
                ImGui.EndDisabled();
            }

            if (!valid)
            {
                ImGui.TextColored(Bad, "Feed URL must start with ws:// or wss://.");
            }
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
    }

    private void DrawBoxes()
    {
        this.Check("Timeline bars", () => this.config.ShowTimeline, v => this.config.ShowTimeline = v);
        this.Check("Alert pop-ups", () => this.config.ShowAlerts, v => this.config.ShowAlerts = v);
        // Same label as the DPS meter section header. Without an id suffix
        // both share one ImGui id, and a click on the header's bar never
        // reaches it while this section is open, only the arrow worked.
        this.Check("DPS meter##showDps", () => this.config.ShowDps, v => this.config.ShowDps = v);

        ImGui.Spacing();

        ImGui.TextDisabled("Show a box only in a duty or only in combat, per box:");
        this.VisibilityRow("Timeline",
            () => this.config.TimelineOnlyInDuty, v => this.config.TimelineOnlyInDuty = v,
            () => this.config.TimelineOnlyInCombat, v => this.config.TimelineOnlyInCombat = v);
        this.VisibilityRow("Alerts",
            () => this.config.AlertsOnlyInDuty, v => this.config.AlertsOnlyInDuty = v,
            () => this.config.AlertsOnlyInCombat, v => this.config.AlertsOnlyInCombat = v);
        this.VisibilityRow("DPS meter",
            () => this.config.DpsOnlyInDuty, v => this.config.DpsOnlyInDuty = v,
            () => this.config.DpsOnlyInCombat, v => this.config.DpsOnlyInCombat = v);
        ImGui.TextDisabled("Both ticked on one box means it shows only for a fight inside a duty.");

        ImGui.Spacing();

        if (ImGui.Button("Test info"))
        {
            this.bridge.PushTestAlert(Severity.Info);
        }

        ImGui.SameLine();
        if (ImGui.Button("Test alert"))
        {
            this.bridge.PushTestAlert(Severity.Alert);
        }

        ImGui.SameLine();
        if (ImGui.Button("Test alarm"))
        {
            this.bridge.PushTestAlert(Severity.Alarm);
        }

        // The test only queues an alert. Saying so beats a button that looks
        // broken because the box it would draw into is currently suppressed.
        if (!this.config.ShowAlerts)
        {
            ImGui.SameLine();
            ImGui.TextColored(Waiting, "Alert pop-ups are off.");
        }
        else if (!this.config.AlertsShowInfo && !this.config.AlertsShowAlert && !this.config.AlertsShowAlarm)
        {
            ImGui.SameLine();
            ImGui.TextColored(Waiting, "All severities are filtered out.");
        }
        else if (this.config.Locked && !this.ui.AlertsVisible &&
            (this.config.AlertsOnlyInDuty || this.config.AlertsOnlyInCombat))
        {
            ImGui.SameLine();
            ImGui.TextColored(Waiting, "Hidden by the alerts box's visibility filters.");
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
        this.PercentSlider("Window fade",
            () => this.config.TimelineFade, v => this.config.TimelineFade = v, 5.0f, 100.0f);
        ImGui.TextDisabled("The whole box's opacity, backdrop included.");

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
        this.Check("Show the fight clock",
            () => this.config.TimelineShowClock, v => this.config.TimelineShowClock = v);
        ImGui.TextDisabled("A mm:ss line above the bars.");
        this.Check("Anchor bars to the bottom",
            () => this.config.TimelineAnchorBottom, v => this.config.TimelineAnchorBottom = v);
        this.Check("Flash bars as they fire",
            () => this.config.TimelineFireFlash, v => this.config.TimelineFireFlash = v);
        ImGui.TextDisabled("A cue that reaches zero stays a beat as a full flashing bar.");

        ImGui.Spacing();

        ImGui.TextDisabled("Which bars show at all:");
        this.Check("Tankbusters",
            () => this.config.TimelineShowTankbuster, v => this.config.TimelineShowTankbuster = v);
        ImGui.SameLine();
        this.Check("Raidwides",
            () => this.config.TimelineShowRaidwide, v => this.config.TimelineShowRaidwide = v);
        ImGui.SameLine();
        this.Check("Mechanics",
            () => this.config.TimelineShowMechanic, v => this.config.TimelineShowMechanic = v);
        ImGui.TextDisabled("Untagged labels count as mechanics. The sample bars ignore these while unlocked.");

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

        this.Check("Color bars by kind",
            () => this.config.TimelineKindColors, v => this.config.TimelineKindColors = v);
        ImGui.TextDisabled("The program tags every timeline label tankbuster, raidwide or mechanic. Untagged labels keep the Bar colour.");
        this.ColorRow("Tankbuster",
            () => this.config.TimelineTankbusterColor, v => this.config.TimelineTankbusterColor = v);
        this.ColorRow("Raidwide",
            () => this.config.TimelineRaidwideColor, v => this.config.TimelineRaidwideColor = v);
        this.ColorRow("Mechanic",
            () => this.config.TimelineMechanicColor, v => this.config.TimelineMechanicColor = v);

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
        this.PercentSlider("Window fade",
            () => this.config.AlertsFade, v => this.config.AlertsFade = v, 5.0f, 100.0f);
        ImGui.TextDisabled("The whole box's opacity, backdrop included.");

        ImGui.Spacing();

        this.Slider("Info time", 0.5f, 15.0f, "%.1f s",
            () => this.config.AlertSeconds, v => this.config.AlertSeconds = v);
        this.Slider("Alert time", 0.5f, 15.0f, "%.1f s",
            () => this.config.AlertSecondsAlert, v => this.config.AlertSecondsAlert = v);
        this.Slider("Alarm time", 0.5f, 15.0f, "%.1f s",
            () => this.config.AlertSecondsAlarm, v => this.config.AlertSecondsAlarm = v);
        ImGui.TextDisabled("How long a callout stays up when the program does not say.");
        this.SliderInt("Max visible", 1, 8,
            () => this.config.AlertsMaxVisible, v => this.config.AlertsMaxVisible = v);
        this.Combo("Stack order", OrderNames,
            () => this.config.AlertOrder, v => this.config.AlertOrder = v);
        this.Combo("Text alignment", AlignNames,
            () => this.config.AlertsAlign, v => this.config.AlertsAlign = v);
        this.Check("Fade in and out", () => this.config.AlertsAnimate, v => this.config.AlertsAnimate = v);
        this.Slider("Alarm scale", 1.0f, 2.0f, "%.2fx",
            () => this.config.AlertsAlarmScale, v => this.config.AlertsAlarmScale = v);
        ImGui.TextDisabled("Alarm callouts draw this much bigger than the rest. Preview with Test alarm.");
        this.Check("Lifeline under each callout",
            () => this.config.AlertsLifeline, v => this.config.AlertsLifeline = v);
        ImGui.TextDisabled("A thin strip that empties as the callout's time runs out.");
        this.Check("Anchor to the bottom",
            () => this.config.AlertsAnchorBottom, v => this.config.AlertsAnchorBottom = v);
        this.Check("Wrap long callouts",
            () => this.config.AlertsWrap, v => this.config.AlertsWrap = v);
        ImGui.TextDisabled("Off draws one line per callout, ending in an ellipsis.");
        this.Check("Merge repeats into a counter",
            () => this.config.AlertsCollapseDupes, v => this.config.AlertsCollapseDupes = v);
        ImGui.TextDisabled("A repeat of the callout on top becomes a ×2, ×3 and so on.");

        ImGui.Spacing();

        ImGui.TextDisabled("Which callouts show at all:");
        this.Check("Info", () => this.config.AlertsShowInfo, v => this.config.AlertsShowInfo = v);
        ImGui.SameLine();
        this.Check("Alert", () => this.config.AlertsShowAlert, v => this.config.AlertsShowAlert = v);
        ImGui.SameLine();
        this.Check("Alarm", () => this.config.AlertsShowAlarm, v => this.config.AlertsShowAlarm = v);

        ImGui.Spacing();

        this.Check("Tint behind each callout",
            () => this.config.AlertsSeverityTint, v => this.config.AlertsSeverityTint = v);
        this.PercentSlider("Tint opacity",
            () => this.config.AlertsSeverityTintOpacity, v => this.config.AlertsSeverityTintOpacity = v);
        this.Check("Flash the box on alarms",
            () => this.config.AlertsAlarmFlash, v => this.config.AlertsAlarmFlash = v);
        this.Check("Flash the screen edges on alarms",
            () => this.config.AlarmScreenFlash, v => this.config.AlarmScreenFlash = v);
        this.PercentSlider("Screen flash size",
            () => this.config.AlarmScreenFlashSize, v => this.config.AlarmScreenFlashSize = v, 2.0f, 50.0f);
        ImGui.TextDisabled("Only while locked, so it never fires over the settings boxes.");

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
        ImGui.TextDisabled("The Horizon Overlay style has its own section below.");
        this.Slider("Text size", 0.5f, 6.0f, "%.2fx",
            () => this.config.DpsTextScale, v => this.config.DpsTextScale = v);
        this.PercentSlider("Background",
            () => this.config.DpsBgOpacity, v => this.config.DpsBgOpacity = v);
        this.PercentSlider("Window fade",
            () => this.config.DpsFade, v => this.config.DpsFade = v, 5.0f, 100.0f);
        ImGui.TextDisabled("The whole box's opacity, backdrop included.");
        this.Check("Only show yourself",
            () => this.config.DpsSoloOnly, v => this.config.DpsSoloOnly = v);
        this.Check("Your own row first",
            () => this.config.DpsSelfFirst, v => this.config.DpsSelfFirst = v);
        ImGui.TextDisabled("Top of the Bars and Kagerou lists, left end of the Horizon Overlay strip.");
        this.Combo("Sort order", DpsSortNames,
            () => this.config.DpsSortOrder, v => this.config.DpsSortOrder = v);
        this.SliderInt("Max combatants", 1, 24,
            () => this.config.DpsMaxRows, v => this.config.DpsMaxRows = v);

        ImGui.Spacing();

        this.Check("Rank numbers",
            () => this.config.DpsRowsShowRank, v => this.config.DpsRowsShowRank = v);
        this.Check("Job icons",
            () => this.config.DpsRowsShowIcons, v => this.config.DpsRowsShowIcons = v);
        ImGui.TextDisabled("Both apply to the Bars and Kagerou rows; the Horizon strip has its own toggles.");
        this.Combo("Other players' names", PrivacyNames,
            () => this.config.DpsNamePrivacy, v => this.config.DpsNamePrivacy = v);
        this.Check("Call yourself YOU",
            () => this.config.DpsSelfNameYou, v => this.config.DpsSelfNameYou = v);
        this.Check("Show deaths",
            () => this.config.DpsShowDeaths, v => this.config.DpsShowDeaths = v);
        ImGui.TextDisabled("A red count beside the name. The Horizon strip needs names shown.");
        this.Check("Keep the last encounter on screen",
            () => this.config.DpsHoldLast, v => this.config.DpsHoldLast = v);
        ImGui.TextDisabled("The final meter stays up after the fight, until the next pull or a zone change.\nThis beats an only-in-combat filter on the meter.");

        ImGui.Spacing();

        ImGui.TextDisabled("The encounter line, above the rows and under the strip.");
        this.Check("Encounter line",
            () => this.config.DpsShowHeader, v => this.config.DpsShowHeader = v);
        this.Check("Duration",
            () => this.config.DpsHeaderDuration, v => this.config.DpsHeaderDuration = v);
        this.Check("Total DPS",
            () => this.config.DpsHeaderTotalDps, v => this.config.DpsHeaderTotalDps = v);
        this.TextInput("Line format", "{title} · {duration} · {dps}",
            () => this.config.DpsHeaderFormat, v => this.config.DpsHeaderFormat = v);
        ImGui.TextDisabled("Empty joins the parts with a dot. Tokens reorder or reword them.");

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
        this.Check("Job colored bars",
            () => this.config.DpsBarJobColors, v => this.config.DpsBarJobColors = v);
        this.Check("Show damage share",
            () => this.config.DpsBarsShowShare, v => this.config.DpsBarsShowShare = v);
        this.Check("Highlight your own bar",
            () => this.config.DpsBarSelfHighlight, v => this.config.DpsBarSelfHighlight = v);
        this.Check("Highlight the top DPS bar",
            () => this.config.DpsBarTopHighlight, v => this.config.DpsBarTopHighlight = v);
        ImGui.TextDisabled("These apply to the Bars style only.");
        this.Check("Show HPS",
            () => this.config.DpsRowsShowHps, v => this.config.DpsRowsShowHps = v);
        ImGui.TextDisabled("Bars and Kagerou rows: append the member's hps to the numbers.");
        this.Check("Compact numbers",
            () => this.config.DpsRowsCompact, v => this.config.DpsRowsCompact = v);
        ImGui.TextDisabled("10.2k instead of 10234.5, on Bars and Kagerou rows. The Horizon Overlay has its own compact knob.");
        this.Check("Alternate row tint",
            () => this.config.DpsRowStripes, v => this.config.DpsRowStripes = v);
        this.PercentSlider("Row tint strength",
            () => this.config.DpsRowStripeOpacity, v => this.config.DpsRowStripeOpacity = v, 0.0f, 50.0f);
        ImGui.TextDisabled("Bars and Kagerou: lighten every other row.");

        ImGui.Spacing();

        this.ColorRow("Bar", () => this.config.DpsBarColor, v => this.config.DpsBarColor = v);
        this.ColorRow("Bar track",
            () => this.config.DpsBarTrackColor, v => this.config.DpsBarTrackColor = v);
        this.ColorRow("Bar border",
            () => this.config.DpsBarBorderColor, v => this.config.DpsBarBorderColor = v);
        this.ColorRow("Bar text", () => this.config.DpsTextColor, v => this.config.DpsTextColor = v);
        this.ColorRow("Self bar",
            () => this.config.DpsBarSelfColor, v => this.config.DpsBarSelfColor = v);
        this.ColorRow("Top DPS bar",
            () => this.config.DpsBarTopColor, v => this.config.DpsBarTopColor = v);
        ImGui.TextDisabled("Bars style only; Horizon Overlay colours by role, Kagerou by job. Self bar needs Highlight your own bar, top DPS bar needs Highlight the top DPS bar.");

        ImGui.Spacing();

        this.EffectGroup(
            () => this.config.DpsTextEffect, v => this.config.DpsTextEffect = v,
            () => this.config.DpsEffectThickness, v => this.config.DpsEffectThickness = v,
            () => this.config.DpsEffectColor, v => this.config.DpsEffectColor = v);

        ImGui.PopID();
    }

    /// <summary>The Horizon Overlay's own section: the toggles, the geometry
    /// and text sizing, and the bar palette. Everything applies live to the
    /// sample strip an unlocked meter box draws.</summary>
    private void DrawHorizon()
    {
        ImGui.PushID("horizon");

        this.Combo("Colors", HorizonThemeNames,
            () => this.config.DpsHorizTheme, v => this.config.DpsHorizTheme = v);

        this.Check("Show names",
            () => this.config.DpsHorizShowNames, v => this.config.DpsHorizShowNames = v);
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

        ImGui.Spacing();

        this.Slider("Max bar width", 40.0f, 400.0f, "%.0f px",
            () => this.config.DpsHorizMaxBarWidth, v => this.config.DpsHorizMaxBarWidth = v);
        this.Slider("Bar height", 10.0f, 60.0f, "%.0f px",
            () => this.config.DpsHorizBarHeight, v => this.config.DpsHorizBarHeight = v);
        this.Slider("Skew", 0.0f, 45.0f, "%.0f°",
            () => this.config.DpsHorizSkew, v => this.config.DpsHorizSkew = v);
        this.Slider("Icon size", 8.0f, 64.0f, "%.0f px",
            () => this.config.DpsHorizIconSize, v => this.config.DpsHorizIconSize = v);
        this.Slider("Cell padding", 0.0f, 24.0f, "%.0f px",
            () => this.config.DpsHorizCellPadding, v => this.config.DpsHorizCellPadding = v);
        this.PercentSlider("Stat text size",
            () => this.config.DpsHorizStatScale, v => this.config.DpsHorizStatScale = v,
            40.0f, 150.0f);
        ImGui.TextDisabled("The hps and dps figures inside the bars, and the names above them.");
        this.PercentSlider("Percent text size",
            () => this.config.DpsHorizPercentScale, v => this.config.DpsHorizPercentScale = v,
            40.0f, 150.0f);
        ImGui.TextDisabled("The damage share figure under each bar.");
        this.SliderInt("DPS decimals", 0, 2,
            () => this.config.DpsHorizDecimals, v => this.config.DpsHorizDecimals = v);
        this.Check("Compact numbers",
            () => this.config.DpsHorizCompact, v => this.config.DpsHorizCompact = v);
        ImGui.TextDisabled("10.2k instead of 10234.50.");
        this.PercentSlider("Bar opacity",
            () => this.config.DpsHorizBarOpacity, v => this.config.DpsHorizBarOpacity = v);

        ImGui.Spacing();

        this.ColorRow("Self bar",
            () => this.config.DpsHorizSelfColor, v => this.config.DpsHorizSelfColor = v);
        this.ColorRow("Self bar text",
            () => this.config.DpsHorizSelfTextColor, v => this.config.DpsHorizSelfTextColor = v);
        this.ColorRow("DPS bars",
            () => this.config.DpsHorizDpsColor, v => this.config.DpsHorizDpsColor = v);
        this.ColorRow("Tank bars",
            () => this.config.DpsHorizTankColor, v => this.config.DpsHorizTankColor = v);
        this.ColorRow("Healer bars",
            () => this.config.DpsHorizHealerColor, v => this.config.DpsHorizHealerColor = v);
        this.ColorRow("Unknown jobs",
            () => this.config.DpsHorizDimColor, v => this.config.DpsHorizDimColor = v);
        ImGui.TextDisabled(
            "The role tints need the Color by role theme. Black & white uses " +
            "Self bar and Unknown jobs for everyone else.");

        ImGui.PopID();
    }

    /// <summary>Named snapshots of the whole appearance: save the current look
    /// under a name, apply or delete saved ones. Only the appearance knobs
    /// travel; the link, the placement and the visibility filters stay.</summary>
    private void DrawProfiles()
    {
        ImGui.TextDisabled("Snapshots of every appearance setting. Placement and the link stay as they are.");

        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##profileName", "Profile name", ref this.profileName, 64);

        var name = this.profileName.Trim();
        ImGui.SameLine();
        if (name.Length == 0)
        {
            ImGui.BeginDisabled();
        }

        // Saving over an existing name replaces it, the usual preset rule.
        if (ImGui.Button("Save current"))
        {
            this.config.AppearanceProfiles[name] = this.config.SnapshotAppearance();
            this.config.Save();
        }

        ImGui.SameLine();

        // The clipboard route shares a look between machines or with static
        // members: Copy on one end, a name and Import on the other.
        if (ImGui.Button("Import"))
        {
            var blob = Configuration.ValidateProfileBlob(ImGui.GetClipboardText());
            if (blob != null)
            {
                this.config.AppearanceProfiles[name] = blob;
                this.config.Save();
                this.importNote = $"Imported \"{name}\". Apply it below.";
            }
            else
            {
                this.importNote = "The clipboard does not hold a profile. Copy one first.";
            }
        }

        if (name.Length == 0)
        {
            ImGui.EndDisabled();
        }

        if (this.importNote != null)
        {
            ImGui.TextDisabled(this.importNote);
        }

        // A copy of the entries: Apply and Delete mutate the dictionary mid
        // enumeration otherwise.
        foreach (var (savedName, blob) in this.config.AppearanceProfiles.ToList())
        {
            ImGui.PushID(savedName);
            if (ImGui.Button("Apply"))
            {
                if (this.config.ApplyAppearanceProfile(blob))
                {
                    this.config.Save();
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Copy"))
            {
                ImGui.SetClipboardText(blob);
            }

            ImGui.SameLine();
            if (ImGui.Button("Delete"))
            {
                this.config.AppearanceProfiles.Remove(savedName);
                this.config.Save();
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(savedName);
            ImGui.PopID();
        }
    }

    /// <summary>One box's duty and combat filters on a single row. The fixed
    /// column keeps the pairs lined up across the boxes.</summary>
    private void VisibilityRow(
        string label,
        Func<bool> getDuty, Action<bool> setDuty,
        Func<bool> getCombat, Action<bool> setCombat)
    {
        ImGui.PushID(label);
        ImGui.TextUnformatted(label);
        ImGui.SameLine(110.0f);
        this.Check("in a duty", getDuty, setDuty);
        ImGui.SameLine();
        this.Check("in combat", getCombat, setCombat);
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
    /// raw value). Some knobs legitimately pass 100%, like a stat text
    /// larger than the body, so the range is a parameter.</summary>
    private void PercentSlider(
        string label, Func<float> get, Action<float> set, float min = 0.0f, float max = 100.0f)
    {
        var value = get() * 100.0f;
        if (ImGui.SliderFloat(label, ref value, min, max, "%.0f%%"))
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

    /// <summary>A free-text setting. Applies live but saves on deactivate, so
    /// a half-typed value never hits the config file.</summary>
    private void TextInput(string label, string hint, Func<string> get, Action<string> set)
    {
        var value = get();
        if (ImGui.InputTextWithHint(label, hint, ref value, 128))
        {
            set(value);
        }

        this.SaveIfDragEnded();
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
