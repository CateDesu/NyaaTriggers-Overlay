using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Configuration;

namespace NyaaTriggers.Plugin;

internal enum TextEffectStyle
{
    Off,
    Outline,
    Glow,
}

internal enum DpsMeterStyle
{
    Bars,
    HorizonOverlay,
    Kagerou,
}

internal enum HorizonColorTheme
{
    ByRole,
    BlackWhite,
}

/// <summary>Deplete reaches empty at the cue time. Fill reaches full.</summary>
internal enum BarFillMode
{
    Deplete,
    Fill,
}

internal enum CountdownStyle
{
    Hidden,
    Seconds,
    Tenths,
}

internal enum NamePrivacyStyle
{
    Shown,
    Initials,
    Hidden,
}

/// <summary>Sorting by name or role preserves each row's DPS rank.</summary>
internal enum DpsSortOrder
{
    ByDps,
    Alphabetical,
    ByRole,
}

internal enum AlertOrder
{
    NewestFirst,
    OldestFirst,
}

internal enum TextAlign
{
    Left,
    Center,
    Right,
}

[Serializable]
internal sealed class Configuration : IPluginConfiguration
{
    private bool saveDisabled;

    internal static Configuration Load(Func<object?> read, string path)
    {
        try
        {
            var config = read() as Configuration ?? new Configuration();
            config.Sanitize();
            return config;
        }
        catch (Exception ex)
        {
            Services.Log.Error($"Could not load NyaaTriggers settings, using defaults: {ex.GetBaseException().Message}");
            var config = new Configuration();
            try
            {
                var backup = $"{path}.broken.{DateTime.UtcNow:yyyyMMddHHmmss}.{Guid.NewGuid():N}";
                File.Move(path, backup);
                Services.Log.Warning($"Damaged NyaaTriggers settings preserved at {backup}");
            }
            catch (FileNotFoundException) when (!File.Exists(path) && !Directory.Exists(path))
            {
                // The file disappeared after the failed read.
            }
            catch (Exception backupError)
            {
                config.saveDisabled = true;
                Services.Log.Error($"Could not preserve damaged settings, saving disabled for this load: {backupError.Message}");
            }

            return config;
        }
    }

    internal void Sanitize()
    {
        var defaults = new Configuration();
        foreach (var property in typeof(Configuration).GetProperties())
        {
            var value = property.GetValue(this);
            if (value is float number && !float.IsFinite(number))
            {
                property.SetValue(this, property.GetValue(defaults));
            }
            else if (value is Vector2 point && (!float.IsFinite(point.X) || !float.IsFinite(point.Y)))
            {
                property.SetValue(this, property.GetValue(defaults));
            }
            else if (value is Vector4 color)
            {
                var fallback = (Vector4)property.GetValue(defaults)!;
                property.SetValue(this, new Vector4(
                    ColorPart(color.X, fallback.X), ColorPart(color.Y, fallback.Y),
                    ColorPart(color.Z, fallback.Z), ColorPart(color.W, fallback.W)));
            }
        }

        TimelinePos = BoundPosition(TimelinePos);
        AlertsPos = BoundPosition(AlertsPos);
        DpsPos = BoundPosition(DpsPos);
        TimelineSize = BoundSize(TimelineSize);
        AlertsSize = BoundSize(AlertsSize);
        DpsSize = BoundSize(DpsSize);
        TimelineTextScale = Math.Clamp(TimelineTextScale, 0.5f, 6.0f);
        TimelineBgOpacity = Math.Clamp(TimelineBgOpacity, 0.0f, 1.0f);
        TimelineFade = Math.Clamp(TimelineFade, 0.0f, 1.0f);
        TimelineBarHeight = Math.Clamp(TimelineBarHeight, 12.0f, 48.0f);
        TimelineBarSpacing = Math.Clamp(TimelineBarSpacing, 0.0f, 16.0f);
        TimelineBarRounding = Math.Clamp(TimelineBarRounding, 0.0f, 12.0f);
        TimelineBarBorderThickness = Math.Clamp(TimelineBarBorderThickness, 0.0f, 4.0f);
        TimelineBarTrackOpacity = Math.Clamp(TimelineBarTrackOpacity, 0.0f, 1.0f);
        ImminentSeconds = Math.Clamp(ImminentSeconds, 0.0f, 15.0f);
        TimelineWindow = Math.Clamp(TimelineWindow, 5.0f, 120.0f);
        AlertsTextScale = Math.Clamp(AlertsTextScale, 0.5f, 6.0f);
        AlertsBgOpacity = Math.Clamp(AlertsBgOpacity, 0.0f, 1.0f);
        AlertsFade = Math.Clamp(AlertsFade, 0.0f, 1.0f);
        AlertSeconds = Math.Clamp(AlertSeconds, 0.5f, 15.0f);
        AlertSecondsAlert = Math.Clamp(AlertSecondsAlert, 0.5f, 15.0f);
        AlertSecondsAlarm = Math.Clamp(AlertSecondsAlarm, 0.5f, 15.0f);
        AlertsAlarmScale = Math.Clamp(AlertsAlarmScale, 1.0f, 2.0f);
        AlertsSeverityTintOpacity = Math.Clamp(AlertsSeverityTintOpacity, 0.0f, 1.0f);
        AlarmScreenFlashSize = Math.Clamp(AlarmScreenFlashSize, 0.0f, 1.0f);
        DpsTextScale = Math.Clamp(DpsTextScale, 0.5f, 6.0f);
        DpsBgOpacity = Math.Clamp(DpsBgOpacity, 0.0f, 1.0f);
        DpsFade = Math.Clamp(DpsFade, 0.0f, 1.0f);
        DpsRowStripeOpacity = Math.Clamp(DpsRowStripeOpacity, 0.0f, 1.0f);
        DpsBarHeight = Math.Clamp(DpsBarHeight, 12.0f, 48.0f);
        DpsBarSpacing = Math.Clamp(DpsBarSpacing, 0.0f, 16.0f);
        DpsBarRounding = Math.Clamp(DpsBarRounding, 0.0f, 12.0f);
        DpsBarBorderThickness = Math.Clamp(DpsBarBorderThickness, 0.0f, 4.0f);
        DpsBarTrackOpacity = Math.Clamp(DpsBarTrackOpacity, 0.0f, 1.0f);
        DpsHorizMaxBarWidth = Math.Clamp(DpsHorizMaxBarWidth, 40.0f, 400.0f);
        DpsHorizBarHeight = Math.Clamp(DpsHorizBarHeight, 10.0f, 60.0f);
        DpsHorizSkew = Math.Clamp(DpsHorizSkew, 0.0f, 45.0f);
        DpsHorizIconSize = Math.Clamp(DpsHorizIconSize, 8.0f, 64.0f);
        DpsHorizCellPadding = Math.Clamp(DpsHorizCellPadding, 0.0f, 24.0f);
        DpsHorizStatScale = Math.Clamp(DpsHorizStatScale, 0.4f, 1.5f);
        DpsHorizPercentScale = Math.Clamp(DpsHorizPercentScale, 0.4f, 1.5f);
        DpsHorizBarOpacity = Math.Clamp(DpsHorizBarOpacity, 0.0f, 1.0f);
        BarHeight = Math.Clamp(BarHeight, 12.0f, 48.0f);
        BarSpacing = Math.Clamp(BarSpacing, 0.0f, 16.0f);
        BarRounding = Math.Clamp(BarRounding, 0.0f, 12.0f);
        BarBorderThickness = Math.Clamp(BarBorderThickness, 0.0f, 4.0f);
        BgOpacity = Math.Clamp(BgOpacity, 0.0f, 1.0f);
        TimelineEffectThickness = Math.Clamp(TimelineEffectThickness, 0, 4);
        AlertsEffectThickness = Math.Clamp(AlertsEffectThickness, 0, 4);
        DpsEffectThickness = Math.Clamp(DpsEffectThickness, 0, 4);
        OutlineThickness = Math.Clamp(OutlineThickness, 0, 4);
        TimelineRows = Math.Clamp(TimelineRows, 1, 12);
        AlertsMaxVisible = Math.Clamp(AlertsMaxVisible, 1, 8);
        DpsMaxRows = Math.Clamp(DpsMaxRows, 1, 24);
        DpsHorizDecimals = Math.Clamp(DpsHorizDecimals, 0, 2);
    }

    private static Vector2 BoundPosition(Vector2 point)
        => Vector2.Clamp(point, new Vector2(-32768), new Vector2(32768));

    private static Vector2 BoundSize(Vector2 size)
        => Vector2.Clamp(size, new Vector2(1), new Vector2(32768));

    private static float ColorPart(float value, float fallback)
        => float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : fallback;

    /// <summary>Increment when stored fields change meaning and provide a migration. Enums
    /// serialize as integers, so reordering or inserting members also requires
    /// migration.</summary>
    public int Version { get; set; } = 5;

    // Connection
    /// <summary>The program connects to this port. The listener binds only to IPv4 and IPv6
    /// loopback.</summary>
    public int Port { get; set; } = 27080;

    /// <summary>Read IINACT directly while the program is disconnected. A program
    /// connection takes priority.</summary>
    public bool StandaloneMeter { get; set; }

    /// <summary>IINACT WebSocket endpoint for the standalone meter.</summary>
    public string IinactEndpoint { get; set; } = "ws://127.0.0.1:10501/ws";

    // Displayed windows
    public bool ShowTimeline { get; set; } = true;
    public bool ShowAlerts { get; set; } = true;
    public bool ShowDps { get; set; } = true;

    public DpsMeterStyle DpsStyle { get; set; } = DpsMeterStyle.Bars;

    /// <summary>Used only by the Horizon Overlay style.</summary>
    public HorizonColorTheme DpsHorizTheme { get; set; } = HorizonColorTheme.ByRole;

    /// <summary>Locked windows have no frame and pass clicks through. Unlocking adds frames
    /// and sample content for placement.</summary>
    public bool Locked { get; set; }

    // Visibility filters apply separately to each window. Enabling both requires combat
    // inside a duty.
    public bool TimelineOnlyInDuty { get; set; }
    public bool TimelineOnlyInCombat { get; set; }
    public bool AlertsOnlyInDuty { get; set; }
    public bool AlertsOnlyInCombat { get; set; }
    public bool DpsOnlyInDuty { get; set; }
    public bool DpsOnlyInCombat { get; set; }

    // Positions and sizes are stored in screen pixels. NoSavedSettings disables ImGui
    // persistence.
    public Vector2 TimelinePos { get; set; } = new(80, 200);
    public Vector2 TimelineSize { get; set; } = new(320, 220);
    public Vector2 AlertsPos { get; set; } = new(80, 440);
    public Vector2 AlertsSize { get; set; } = new(420, 160);
    public Vector2 DpsPos { get; set; } = new(80, 620);
    public Vector2 DpsSize { get; set; } = new(320, 240);

    // Timeline appearance
    /// <summary>Scales bar labels, countdowns and row heights together.</summary>
    public float TimelineTextScale { get; set; } = 1.0f;

    public float TimelineBgOpacity { get; set; }

    /// <summary>Scales every timeline colour, including the backdrop.</summary>
    public float TimelineFade { get; set; } = 1.0f;

    public TextEffectStyle TimelineTextEffect { get; set; } = TextEffectStyle.Outline;

    /// <summary>Outline radius or glow spread in pixels.</summary>
    public int TimelineEffectThickness { get; set; } = 1;

    /// <summary>Effect opacity also follows the text fade.</summary>
    public Vector4 TimelineEffectColor { get; set; } = new(0.0f, 0.0f, 0.0f, 0.9f);

    public Vector4 TimelineTextColor { get; set; } = new(0.95f, 0.95f, 0.98f, 1.00f);

    /// <summary>Row height before text scaling.</summary>
    public float TimelineBarHeight { get; set; } = 22.0f;

    public float TimelineBarSpacing { get; set; } = 4.0f;

    public float TimelineBarRounding { get; set; } = 3.0f;

    /// <summary>Zero disables the border.</summary>
    public float TimelineBarBorderThickness { get; set; }

    public float TimelineBarTrackOpacity { get; set; } = 1.0f;

    public Vector4 TimelineBarColor { get; set; } = new(0.55f, 0.44f, 0.78f, 0.85f);

    public Vector4 TimelineBarTrackColor { get; set; } = new(0.0f, 0.0f, 0.0f, 0.60f);

    public Vector4 TimelineBarBorderColor { get; set; } = new(0.00f, 0.00f, 0.00f, 0.80f);

    // Timeline behavior
    public BarFillMode BarFill { get; set; } = BarFillMode.Deplete;

    public bool BarRightToLeft { get; set; }

    public TextAlign BarTextAlign { get; set; } = TextAlign.Left;

    /// <summary>Seconds before a cue when its bar takes the imminent colour.</summary>
    public float ImminentSeconds { get; set; } = 5.0f;

    public bool ImminentPulse { get; set; } = true;

    public CountdownStyle Countdown { get; set; } = CountdownStyle.Tenths;

    /// <summary>Place the countdown at the right edge instead of beside the
    /// label.</summary>
    public bool CountdownSplit { get; set; }

    /// <summary>Seconds ahead of the fight clock to show timeline entries.</summary>
    public float TimelineWindow { get; set; } = 45.0f;

    public int TimelineRows { get; set; } = 6;

    /// <summary>Show the fight clock above the bars as mm:ss.</summary>
    public bool TimelineShowClock { get; set; }

    /// <summary>Stack bars upward from the bottom of the window.</summary>
    public bool TimelineAnchorBottom { get; set; }

    /// <summary>Briefly show a full bar when a cue reaches zero.</summary>
    public bool TimelineFireFlash { get; set; } = true;

    /// <summary>Missing and unknown kinds use the mechanic filter. Placement samples ignore
    /// these filters.</summary>
    public bool TimelineShowTankbuster { get; set; } = true;
    public bool TimelineShowRaidwide { get; set; } = true;
    public bool TimelineShowMechanic { get; set; } = true;

    public Vector4 ColorImminent { get; set; } = new(0.90f, 0.28f, 0.28f, 0.95f);

    /// <summary>Use the tagged kind colour, or the shared colour for missing and unknown
    /// kinds. The imminent colour takes priority.</summary>
    public bool TimelineKindColors { get; set; }

    public Vector4 TimelineTankbusterColor { get; set; } = new(0.92f, 0.48f, 0.20f, 0.85f);

    public Vector4 TimelineRaidwideColor { get; set; } = new(0.35f, 0.62f, 0.92f, 0.85f);

    /// <summary>Applies only to explicit mechanic tags. Missing and unknown kinds keep the
    /// shared colour.</summary>
    public Vector4 TimelineMechanicColor { get; set; } = new(0.55f, 0.44f, 0.78f, 0.85f);

    // Alert appearance
    public float AlertsTextScale { get; set; } = 1.0f;

    public float AlertsBgOpacity { get; set; }

    /// <summary>Scales every alert colour, including the backdrop.</summary>
    public float AlertsFade { get; set; } = 1.0f;

    public TextEffectStyle AlertsTextEffect { get; set; } = TextEffectStyle.Outline;

    /// <summary>Outline radius or glow spread in pixels.</summary>
    public int AlertsEffectThickness { get; set; } = 1;

    /// <summary>Effect opacity also follows the callout fade.</summary>
    public Vector4 AlertsEffectColor { get; set; } = new(0.0f, 0.0f, 0.0f, 0.9f);

    /// <summary>Info duration in seconds when the program supplies no ttl. Keep the
    /// serialized name for compatibility with older configs.</summary>
    public float AlertSeconds { get; set; } = 4.0f;

    /// <summary>Alert duration in seconds when the program supplies no ttl.</summary>
    public float AlertSecondsAlert { get; set; } = 4.0f;

    /// <summary>Alarm duration in seconds when the program supplies no ttl.</summary>
    public float AlertSecondsAlarm { get; set; } = 6.0f;

    /// <summary>Limits visible callouts without removing retained alerts from the
    /// bridge.</summary>
    public int AlertsMaxVisible { get; set; } = 8;

    public AlertOrder AlertOrder { get; set; } = AlertOrder.NewestFirst;

    public TextAlign AlertsAlign { get; set; } = TextAlign.Left;

    /// <summary>Disabling animation keeps callouts at full opacity until expiry.</summary>
    public bool AlertsAnimate { get; set; } = true;

    /// <summary>Alarm text size relative to normal callout text.</summary>
    public float AlertsAlarmScale { get; set; } = 1.0f;

    /// <summary>Show remaining callout time as a strip below the text.</summary>
    public bool AlertsLifeline { get; set; }

    public bool AlertsShowInfo { get; set; } = true;
    public bool AlertsShowAlert { get; set; } = true;
    public bool AlertsShowAlarm { get; set; } = true;

    /// <summary>Stack callouts upward from the bottom of the window.</summary>
    public bool AlertsAnchorBottom { get; set; }

    /// <summary>When disabled, truncate each callout to one line with an
    /// ellipsis.</summary>
    public bool AlertsWrap { get; set; } = true;

    /// <summary>Merge repeats of the top callout and display a repeat count.</summary>
    public bool AlertsCollapseDupes { get; set; } = true;

    /// <summary>Draw a background in each callout's severity colour.</summary>
    public bool AlertsSeverityTint { get; set; }

    /// <summary>Also scaled by the callout fade.</summary>
    public float AlertsSeverityTintOpacity { get; set; } = 0.30f;

    public bool AlertsAlarmFlash { get; set; } = true;

    /// <summary>Flash screen edges during alarms only while the overlay is
    /// locked.</summary>
    public bool AlarmScreenFlash { get; set; }

    /// <summary>Flash depth as a fraction of the shorter screen dimension.</summary>
    public float AlarmScreenFlashSize { get; set; } = 0.15f;

    public Vector4 ColorInfo { get; set; } = new(0.89f, 0.74f, 0.42f, 1.00f);
    public Vector4 ColorAlert { get; set; } = new(0.98f, 0.62f, 0.35f, 1.00f);
    public Vector4 ColorAlarm { get; set; } = new(0.95f, 0.30f, 0.30f, 1.00f);

    // DPS appearance
    public float DpsTextScale { get; set; } = 1.0f;

    public float DpsBgOpacity { get; set; }

    /// <summary>Scales every meter colour, including the backdrop.</summary>
    public float DpsFade { get; set; } = 1.0f;

    public TextEffectStyle DpsTextEffect { get; set; } = TextEffectStyle.Outline;

    /// <summary>Outline radius or glow spread in pixels.</summary>
    public int DpsEffectThickness { get; set; } = 1;

    public Vector4 DpsEffectColor { get; set; } = new(0.0f, 0.0f, 0.0f, 0.9f);

    public Vector4 DpsTextColor { get; set; } = new(0.95f, 0.95f, 0.98f, 1.00f);

    public bool DpsSoloOnly { get; set; }

    /// <summary>Place the local player first while preserving their DPS rank.</summary>
    public bool DpsSelfFirst { get; set; }

    public DpsSortOrder DpsSortOrder { get; set; } = DpsSortOrder.ByDps;

    public NamePrivacyStyle DpsNamePrivacy { get; set; } = NamePrivacyStyle.Shown;

    public bool DpsSelfNameYou { get; set; }

    /// <summary>Applies to Bars and Kagerou. Horizon Overlay has a separate rank
    /// setting.</summary>
    public bool DpsRowsShowRank { get; set; } = true;

    /// <summary>Applies to Bars and Kagerou.</summary>
    public bool DpsRowsShowIcons { get; set; }

    /// <summary>Applies to Bars and Kagerou.</summary>
    public bool DpsRowStripes { get; set; }

    /// <summary>Stripe opacity from 0 to 0.5.</summary>
    public float DpsRowStripeOpacity { get; set; } = 0.08f;

    /// <summary>Bars style only. The local player highlight takes priority.</summary>
    public bool DpsBarTopHighlight { get; set; }

    public Vector4 DpsBarTopColor { get; set; } = new(0.98f, 0.80f, 0.25f, 0.85f);

    /// <summary>Horizon Overlay requires names to be visible to show death
    /// counts.</summary>
    public bool DpsShowDeaths { get; set; }

    /// <summary>Keep final rows until damage starts on the next pull or the zone changes.</summary>
    public bool DpsHoldLast { get; set; } = true;

    /// <summary>Supports {title}, {duration} and {dps}. Empty uses the default layout with
    /// dot separators.</summary>
    public string DpsHeaderFormat { get; set; } = string.Empty;

    /// <summary>Limits display rows in every style. The feed can supply a full
    /// alliance.</summary>
    public int DpsMaxRows { get; set; } = 8;

    /// <summary>Row height before text scaling.</summary>
    public float DpsBarHeight { get; set; } = 22.0f;

    public float DpsBarSpacing { get; set; } = 4.0f;

    public float DpsBarRounding { get; set; } = 3.0f;

    /// <summary>Zero disables the border.</summary>
    public float DpsBarBorderThickness { get; set; }

    public float DpsBarTrackOpacity { get; set; } = 1.0f;

    public Vector4 DpsBarColor { get; set; } = new(0.55f, 0.44f, 0.78f, 0.85f);

    public Vector4 DpsBarTrackColor { get; set; } = new(0.0f, 0.0f, 0.0f, 0.60f);

    public Vector4 DpsBarBorderColor { get; set; } = new(0.00f, 0.00f, 0.00f, 0.80f);

    public bool DpsBarRightToLeft { get; set; }

    /// <summary>Bars style only. Uses each job colour with the configured bar
    /// alpha.</summary>
    public bool DpsBarJobColors { get; set; }

    /// <summary>Bars style only. Show damage share beside DPS at the right edge.</summary>
    public bool DpsBarsShowShare { get; set; }

    /// <summary>Bars and Kagerou only. Omit HPS for rows with no healing.</summary>
    public bool DpsRowsShowHps { get; set; }

    /// <summary>Bars and Kagerou only. Show 10234.5 as 10.2k when enabled, or one decimal
    /// place when disabled. Horizon Overlay has a separate compact setting.</summary>
    public bool DpsRowsCompact { get; set; } = true;

    /// <summary>Bars style only. Overrides job colours and the top rank
    /// highlight.</summary>
    public bool DpsBarSelfHighlight { get; set; }

    public Vector4 DpsBarSelfColor { get; set; } = new(1.00f, 1.00f, 1.00f, 0.85f);

    // Horizon Overlay settings retain their serialized Horiz names for compatibility.

    /// <summary>Hiding names also removes their reserved space above the bars.</summary>
    public bool DpsHorizShowNames { get; set; } = true;

    public bool DpsHorizShowRank { get; set; } = true;

    public bool DpsHorizShowIcons { get; set; } = true;

    /// <summary>When disabled, show the job acronym in place of HPS.</summary>
    public bool DpsHorizShowHps { get; set; } = true;

    /// <summary>Emphasize the half containing the role's main stat. Disable for a uniform
    /// tint.</summary>
    public bool DpsHorizHighlight { get; set; } = true;

    /// <summary>Show a damage share strip and percentage below each bar.</summary>
    public bool DpsHorizShowPercent { get; set; } = true;

    /// <summary>Maximum cell width before text scaling. Narrow windows shrink all cells
    /// equally.</summary>
    public float DpsHorizMaxBarWidth { get; set; } = 140.0f;

    /// <summary>Bar height before text scaling.</summary>
    public float DpsHorizBarHeight { get; set; } = 32.0f;

    /// <summary>Skew angle in degrees. Zero produces a rectangle.</summary>
    public float DpsHorizSkew { get; set; } = 30.0f;

    /// <summary>Icon edge length before text scaling.</summary>
    public float DpsHorizIconSize { get; set; } = 20.0f;

    /// <summary>Space on each side of a cell.</summary>
    public float DpsHorizCellPadding { get; set; } = 6.0f;

    /// <summary>Scale names and in-bar stats relative to body text, from 0.4 to
    /// 1.5.</summary>
    public float DpsHorizStatScale { get; set; } = 0.80f;

    /// <summary>Scale percentage text relative to body text, from 0.4 to 1.5.</summary>
    public float DpsHorizPercentScale { get; set; } = 0.45f;

    /// <summary>DPS decimal places from 0 to 2.</summary>
    public int DpsHorizDecimals { get; set; } = 2;

    /// <summary>Show 10234.50 as 10.2k.</summary>
    public bool DpsHorizCompact { get; set; }

    /// <summary>Opacity of the emphasized half. The other half uses one third of this
    /// value.</summary>
    public float DpsHorizBarOpacity { get; set; } = 0.30f;

    public Vector4 DpsHorizSelfColor { get; set; } = new(1.000f, 1.000f, 1.000f, 0.80f);

    /// <summary>Local player stat text is drawn without a text effect.</summary>
    public Vector4 DpsHorizSelfTextColor { get; set; } = new(0.000f, 0.000f, 0.000f, 1.00f);

    public Vector4 DpsHorizDpsColor { get; set; } = new(0.957f, 0.263f, 0.212f, 1.00f);

    public Vector4 DpsHorizTankColor { get; set; } = new(0.129f, 0.588f, 0.953f, 1.00f);

    public Vector4 DpsHorizHealerColor { get; set; } = new(0.545f, 0.765f, 0.290f, 1.00f);

    /// <summary>Used for unknown jobs and other players in the black &amp; white
    /// theme.</summary>
    public Vector4 DpsHorizDimColor { get; set; } = new(0.000f, 0.000f, 0.000f, 0.30f);

    // Encounter header
    public bool DpsShowHeader { get; set; } = true;

    public bool DpsHeaderDuration { get; set; } = true;

    public bool DpsHeaderTotalDps { get; set; } = true;

    // Appearance profiles
    /// <summary>Named JSON snapshots containing only appearance settings.</summary>
    public Dictionary<string, string> AppearanceProfiles
    {
        get;
        set => field = value ?? new();
    } = new();

    /// <summary>Include Vector4 fields so profile colours survive serialization.</summary>
    private static readonly JsonSerializerOptions ProfileOptions = new()
    {
        IncludeFields = true,
    };

    /// <summary>Serialize only fields that an appearance profile can apply.</summary>
    public string SnapshotAppearance()
    {
        this.Sanitize();
        var node = new JsonObject();
        foreach (var property in AppearanceProperties)
        {
            node[property.Name] = JsonSerializer.SerializeToNode(property.GetValue(this), property.PropertyType, ProfileOptions);
        }
        return node.ToJsonString();
    }

    /// <summary>Return false for invalid profile JSON without changing the current
    /// appearance.</summary>
    public bool ApplyAppearanceProfile(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        Configuration? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<Configuration>(json, ProfileOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (snapshot == null)
        {
            return false;
        }

        this.CopyAppearanceFrom(snapshot);
        return true;
    }

    /// <summary>Validate imported JSON and serialize only supported appearance fields.
    /// Reject oversized input before parsing.</summary>
    public static string? ValidateProfileBlob(string? json)
    {
        const int MaxBlobChars = 64 * 1024;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxBlobChars)
        {
            return null;
        }

        Configuration? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<Configuration>(json, ProfileOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        return snapshot?.SnapshotAppearance();
    }

    // Legacy appearance fields retained for MigrateFromV2.
    public TextEffectStyle TextEffect { get; set; } = TextEffectStyle.Outline;
    public int OutlineThickness { get; set; } = 1;
    public Vector4 ColorOutline { get; set; } = new(0.0f, 0.0f, 0.0f, 0.9f);
    public Vector4 ColorBar { get; set; } = new(0.55f, 0.44f, 0.78f, 0.85f);
    public Vector4 ColorBarText { get; set; } = new(0.95f, 0.95f, 0.98f, 1.00f);
    public Vector4 ColorBarBorder { get; set; } = new(0.00f, 0.00f, 0.00f, 0.80f);
    public float BarHeight { get; set; } = 22.0f;
    public float BarSpacing { get; set; } = 4.0f;
    public float BarRounding { get; set; } = 3.0f;
    public float BarBorderThickness { get; set; }

    /// <summary>Legacy opacity retained for MigrateFromV1.</summary>
    public float BgOpacity { get; set; }

    /// <summary>Legacy visibility filters retained for MigrateFromV3.</summary>
    public bool OnlyInDuty { get; set; }
    public bool OnlyInCombat { get; set; }

    /// <summary>Serialize writes from settings, commands and unload to avoid concurrent
    /// file writes.</summary>
    public void Save()
    {
        lock (SaveLock)
        {
            if (this.saveDisabled)
            {
                return;
            }

            Services.PluginInterface.SavePluginConfig(this);
        }
    }

    private static readonly object SaveLock = new();

    public void MigrateFromV1()
    {
        TimelineBgOpacity = BgOpacity;
        AlertsBgOpacity = BgOpacity;
        Version = 2;
    }

    /// <summary>Split shared appearance settings by window and map the retired shadow
    /// effect to outline. Track opacity starts at its new default.</summary>
    public void MigrateFromV2()
    {
        var effect = TextEffect == TextEffectStyle.Off ? TextEffectStyle.Off : TextEffectStyle.Outline;
        TimelineTextEffect = effect;
        AlertsTextEffect = effect;
        DpsTextEffect = effect;

        TimelineEffectThickness = OutlineThickness;
        AlertsEffectThickness = OutlineThickness;
        DpsEffectThickness = OutlineThickness;

        TimelineEffectColor = ColorOutline;
        AlertsEffectColor = ColorOutline;
        DpsEffectColor = ColorOutline;

        TimelineBarColor = ColorBar;
        DpsBarColor = ColorBar;
        TimelineTextColor = ColorBarText;
        DpsTextColor = ColorBarText;
        TimelineBarBorderColor = ColorBarBorder;
        DpsBarBorderColor = ColorBarBorder;

        TimelineBarHeight = BarHeight;
        DpsBarHeight = BarHeight;
        TimelineBarSpacing = BarSpacing;
        DpsBarSpacing = BarSpacing;
        TimelineBarRounding = BarRounding;
        DpsBarRounding = BarRounding;
        TimelineBarBorderThickness = BarBorderThickness;
        DpsBarBorderThickness = BarBorderThickness;
        DpsBarRightToLeft = BarRightToLeft;

        Version = 3;
    }

    /// <summary>Copy shared visibility and alert duration into the settings for each window
    /// and severity.</summary>
    public void MigrateFromV3()
    {
        TimelineOnlyInDuty = OnlyInDuty;
        AlertsOnlyInDuty = OnlyInDuty;
        DpsOnlyInDuty = OnlyInDuty;
        TimelineOnlyInCombat = OnlyInCombat;
        AlertsOnlyInCombat = OnlyInCombat;
        DpsOnlyInCombat = OnlyInCombat;

        AlertSecondsAlert = AlertSeconds;
        AlertSecondsAlarm = AlertSeconds;

        Version = 4;
    }

    /// <summary>Enable the retained meter for existing installations.</summary>
    public void MigrateFromV4()
    {
        DpsHoldLast = true;
        Version = 5;
    }

    /// <summary>Reset appearance while preserving connection settings, placement,
    /// visibility and profiles.</summary>
    public void ResetAppearance() => this.CopyAppearanceFrom(new Configuration());

    /// <summary>Copy appearance while preserving connection settings, placement, visibility
    /// and profiles.</summary>
    public void CopyAppearanceFrom(Configuration fresh)
    {
        fresh.Sanitize();
        foreach (var property in AppearanceProperties)
        {
            property.SetValue(this, property.GetValue(fresh));
        }
    }

    private static readonly PropertyInfo[] AppearanceProperties = new[]
    {
        nameof(TimelineTextScale),
        nameof(TimelineBgOpacity),
        nameof(TimelineFade),
        nameof(TimelineTextEffect),
        nameof(TimelineEffectThickness),
        nameof(TimelineEffectColor),
        nameof(TimelineTextColor),
        nameof(TimelineBarHeight),
        nameof(TimelineBarSpacing),
        nameof(TimelineBarRounding),
        nameof(TimelineBarBorderThickness),
        nameof(TimelineBarTrackOpacity),
        nameof(TimelineBarColor),
        nameof(TimelineBarTrackColor),
        nameof(TimelineBarBorderColor),
        nameof(BarFill),
        nameof(BarRightToLeft),
        nameof(BarTextAlign),
        nameof(ImminentSeconds),
        nameof(ImminentPulse),
        nameof(Countdown),
        nameof(CountdownSplit),
        nameof(TimelineWindow),
        nameof(TimelineRows),
        nameof(TimelineShowClock),
        nameof(TimelineAnchorBottom),
        nameof(TimelineFireFlash),
        nameof(TimelineShowTankbuster),
        nameof(TimelineShowRaidwide),
        nameof(TimelineShowMechanic),
        nameof(ColorImminent),
        nameof(TimelineKindColors),
        nameof(TimelineTankbusterColor),
        nameof(TimelineRaidwideColor),
        nameof(TimelineMechanicColor),
        nameof(AlertsTextScale),
        nameof(AlertsBgOpacity),
        nameof(AlertsFade),
        nameof(AlertsTextEffect),
        nameof(AlertsEffectThickness),
        nameof(AlertsEffectColor),
        nameof(AlertSeconds),
        nameof(AlertSecondsAlert),
        nameof(AlertSecondsAlarm),
        nameof(AlertsMaxVisible),
        nameof(AlertOrder),
        nameof(AlertsAlign),
        nameof(AlertsAnimate),
        nameof(AlertsAlarmScale),
        nameof(AlertsLifeline),
        nameof(AlertsShowInfo),
        nameof(AlertsShowAlert),
        nameof(AlertsShowAlarm),
        nameof(AlertsAnchorBottom),
        nameof(AlertsWrap),
        nameof(AlertsCollapseDupes),
        nameof(AlertsSeverityTint),
        nameof(AlertsSeverityTintOpacity),
        nameof(AlertsAlarmFlash),
        nameof(AlarmScreenFlash),
        nameof(AlarmScreenFlashSize),
        nameof(ColorInfo),
        nameof(ColorAlert),
        nameof(ColorAlarm),
        nameof(DpsTextScale),
        nameof(DpsBgOpacity),
        nameof(DpsFade),
        nameof(DpsTextEffect),
        nameof(DpsEffectThickness),
        nameof(DpsEffectColor),
        nameof(DpsTextColor),
        nameof(DpsSoloOnly),
        nameof(DpsSelfFirst),
        nameof(DpsSortOrder),
        nameof(DpsNamePrivacy),
        nameof(DpsSelfNameYou),
        nameof(DpsRowsShowRank),
        nameof(DpsRowsShowIcons),
        nameof(DpsRowStripes),
        nameof(DpsRowStripeOpacity),
        nameof(DpsMaxRows),
        nameof(DpsBarHeight),
        nameof(DpsBarSpacing),
        nameof(DpsBarRounding),
        nameof(DpsBarBorderThickness),
        nameof(DpsBarTrackOpacity),
        nameof(DpsBarColor),
        nameof(DpsBarTrackColor),
        nameof(DpsBarBorderColor),
        nameof(DpsBarRightToLeft),
        nameof(DpsBarJobColors),
        nameof(DpsBarsShowShare),
        nameof(DpsRowsShowHps),
        nameof(DpsRowsCompact),
        nameof(DpsBarSelfHighlight),
        nameof(DpsBarSelfColor),
        nameof(DpsBarTopHighlight),
        nameof(DpsBarTopColor),
        nameof(DpsShowDeaths),
        nameof(DpsHoldLast),
        nameof(DpsHeaderFormat),
        nameof(DpsStyle),
        nameof(DpsHorizTheme),
        nameof(DpsHorizShowNames),
        nameof(DpsHorizShowRank),
        nameof(DpsHorizShowIcons),
        nameof(DpsHorizShowHps),
        nameof(DpsHorizHighlight),
        nameof(DpsHorizShowPercent),
        nameof(DpsHorizMaxBarWidth),
        nameof(DpsHorizBarHeight),
        nameof(DpsHorizSkew),
        nameof(DpsHorizIconSize),
        nameof(DpsHorizCellPadding),
        nameof(DpsHorizStatScale),
        nameof(DpsHorizPercentScale),
        nameof(DpsHorizDecimals),
        nameof(DpsHorizCompact),
        nameof(DpsHorizBarOpacity),
        nameof(DpsHorizSelfColor),
        nameof(DpsHorizSelfTextColor),
        nameof(DpsHorizDpsColor),
        nameof(DpsHorizTankColor),
        nameof(DpsHorizHealerColor),
        nameof(DpsHorizDimColor),
        nameof(DpsShowHeader),
        nameof(DpsHeaderDuration),
        nameof(DpsHeaderTotalDps),
    }.Select(name => typeof(Configuration).GetProperty(name)!).ToArray();
}
