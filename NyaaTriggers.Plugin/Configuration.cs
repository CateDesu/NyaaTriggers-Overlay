using System;
using System.Numerics;
using Dalamud.Configuration;

namespace NyaaTriggers.Plugin;

/// <summary>How overlay text is kept readable over the game.</summary>
internal enum TextEffectStyle
{
    Off,
    Shadow,
    Outline,
}

/// <summary>How the dps meter draws its rows: timeline-style share bars,
/// horizoverlay's solid job-coloured bars, or kagerou's underlined text.</summary>
internal enum DpsMeterStyle
{
    Bars,
    Horizoverlay,
    Kagerou,
}

/// <summary>Whether a bar shrinks toward empty as the cue arrives or grows
/// toward full as time elapses.</summary>
internal enum BarFillMode
{
    Deplete,
    Fill,
}

/// <summary>How the countdown on a bar is shown.</summary>
internal enum CountdownStyle
{
    Hidden,
    Seconds,
    Tenths,
}

/// <summary>Which end of the stack the newest callout sits at.</summary>
internal enum AlertOrder
{
    NewestFirst,
    OldestFirst,
}

/// <summary>Horizontal text placement inside a box.</summary>
internal enum TextAlign
{
    Left,
    Center,
    Right,
}

[Serializable]
internal sealed class Configuration : IPluginConfiguration
{
    /// <summary>Bumped only when a stored field changes meaning, so old configs
    /// can be migrated rather than silently reinterpreted.</summary>
    public int Version { get; set; } = 2;

    // ── link ──────────────────────────────────────────────────────────────
    /// <summary>Loopback port the desktop app connects to. Not exposed off the
    /// machine: the listener binds 127.0.0.1 and ::1 only. As a companion
    /// plugin it always listens; there is no off switch.</summary>
    public int Port { get; set; } = 27080;

    // ── what to draw ──────────────────────────────────────────────────────
    public bool ShowTimeline { get; set; } = true;
    public bool ShowAlerts { get; set; } = true;
    public bool ShowDps { get; set; } = true;

    /// <summary>How the dps meter draws its rows.</summary>
    public DpsMeterStyle DpsStyle { get; set; } = DpsMeterStyle.Bars;

    /// <summary>Locked: chromeless and click-through, i.e. the raid-night state.
    /// Unlocked shows a frame and sample content so the boxes can be placed.</summary>
    public bool Locked { get; set; }

    /// <summary>Hide both boxes outside of combat and duties, so they are not
    /// sitting over the overworld doing nothing.</summary>
    public bool OnlyInDuty { get; set; }

    // ── geometry (screen pixels, persisted ourselves: the overlay windows use
    //     NoSavedSettings so imgui.ini never fights us) ────────────────────
    public Vector2 TimelinePos { get; set; } = new(80, 200);
    public Vector2 TimelineSize { get; set; } = new(320, 220);
    public Vector2 AlertsPos { get; set; } = new(80, 440);
    public Vector2 AlertsSize { get; set; } = new(420, 160);
    public Vector2 DpsPos { get; set; } = new(80, 620);
    public Vector2 DpsSize { get; set; } = new(320, 240);

    // ── text ──────────────────────────────────────────────────────────────
    /// <summary>Text scale inside the timeline box: the bar labels and their
    /// countdowns. Bar row heights scale with it so the text stays inside.</summary>
    public float TimelineTextScale { get; set; } = 1.0f;

    /// <summary>Text scale inside the alerts box.</summary>
    public float AlertsTextScale { get; set; } = 1.0f;

    /// <summary>Text scale inside the dps meter box.</summary>
    public float DpsTextScale { get; set; } = 1.0f;

    /// <summary>Rasterize overlay text at its real pixel size in a private
    /// font atlas instead of stretching the default font's bitmap. This is
    /// what keeps large text sharp; the fallback exists in case a Dalamud
    /// build misbehaves with plugin-owned atlases.</summary>
    public bool HighQualityText { get; set; } = true;

    /// <summary>What is drawn behind text to keep it readable over bright
    /// arenas: nothing, a drop shadow, or a full outline.</summary>
    public TextEffectStyle TextEffect { get; set; } = TextEffectStyle.Outline;

    /// <summary>Effect reach in pixels: outline radius or shadow offset.</summary>
    public int OutlineThickness { get; set; } = 1;

    /// <summary>Outline/shadow colour. The alpha is the effect's opacity and
    /// is scaled by the text's own fade.</summary>
    public Vector4 ColorOutline { get; set; } = new(0.0f, 0.0f, 0.0f, 0.9f);

    // ── boxes ─────────────────────────────────────────────────────────────
    /// <summary>Backdrop alpha behind the timeline box's content: 0 =
    /// invisible (the raid-night default; the box floats bare text/bars over
    /// the game), up to 1 = solid theme background.</summary>
    public float TimelineBgOpacity { get; set; }

    /// <summary>Backdrop alpha behind the alerts box's content.</summary>
    public float AlertsBgOpacity { get; set; }

    /// <summary>Backdrop alpha behind the dps meter box's content.</summary>
    public float DpsBgOpacity { get; set; }

    /// <summary>Version 1's single backdrop opacity, split per box in version
    /// 2. Kept only so the migration can read the old value.</summary>
    public float BgOpacity { get; set; }

    // ── timeline bars ─────────────────────────────────────────────────────
    /// <summary>Timeline bar row height before the text-scale multiplier.</summary>
    public float BarHeight { get; set; } = 22.0f;

    /// <summary>Gap between bar rows.</summary>
    public float BarSpacing { get; set; } = 4.0f;

    /// <summary>Corner rounding on the bar and its track.</summary>
    public float BarRounding { get; set; } = 3.0f;

    /// <summary>Border drawn around each bar, 0 = no border.</summary>
    public float BarBorderThickness { get; set; }

    /// <summary>Alpha multiplier for the full-length track under a bar's
    /// fill, so the depleting part can sit on a faint ghost of the whole.</summary>
    public float BarTrackOpacity { get; set; } = 0.5f;

    /// <summary>Deplete: the bar empties as the cue arrives. Fill: it grows
    /// toward full instead.</summary>
    public BarFillMode BarFill { get; set; } = BarFillMode.Deplete;

    /// <summary>Anchor the fill to the right edge instead of the left.</summary>
    public bool BarRightToLeft { get; set; }

    /// <summary>Where the label and countdown sit across the bar.</summary>
    public TextAlign BarTextAlign { get; set; } = TextAlign.Left;

    /// <summary>Seconds out at which a bar turns to the imminent colour.</summary>
    public float ImminentSeconds { get; set; } = 5.0f;

    /// <summary>Pulse imminent bars so they catch the eye.</summary>
    public bool ImminentPulse { get; set; } = true;

    /// <summary>Whether bars carry a countdown and with what precision.</summary>
    public CountdownStyle Countdown { get; set; } = CountdownStyle.Tenths;

    /// <summary>Pin the countdown to the bar's right edge instead of
    /// appending it to the label.</summary>
    public bool CountdownSplit { get; set; }

    /// <summary>Seconds ahead of the fight clock a timeline entry becomes a bar.</summary>
    public float TimelineWindow { get; set; } = 45.0f;

    /// <summary>How many bars at most, so a dense timeline cannot grow the box
    /// past its configured height.</summary>
    public int TimelineRows { get; set; } = 6;

    // ── alerts ────────────────────────────────────────────────────────────
    /// <summary>Seconds an alert stays up when the app does not specify one.</summary>
    public float AlertSeconds { get; set; } = 4.0f;

    /// <summary>Most callouts shown at once. The bridge keeps a few more than
    /// this; the cap only limits what is drawn.</summary>
    public int AlertsMaxVisible { get; set; } = 8;

    /// <summary>Whether the newest callout lands on top of the stack or
    /// grows it from the bottom.</summary>
    public AlertOrder AlertOrder { get; set; } = AlertOrder.NewestFirst;

    /// <summary>Horizontal placement of callout lines inside the box.</summary>
    public TextAlign AlertsAlign { get; set; } = TextAlign.Left;

    /// <summary>Rise-in and fade-out animation. Off pins callouts at full
    /// opacity for their whole life.</summary>
    public bool AlertsAnimate { get; set; } = true;

    // ── colours ───────────────────────────────────────────────────────────
    public Vector4 ColorBar { get; set; } = new(0.55f, 0.44f, 0.78f, 0.85f);
    public Vector4 ColorBarText { get; set; } = new(0.95f, 0.95f, 0.98f, 1.00f);
    public Vector4 ColorBarBorder { get; set; } = new(0.00f, 0.00f, 0.00f, 0.80f);
    public Vector4 ColorImminent { get; set; } = new(0.90f, 0.28f, 0.28f, 0.95f);
    public Vector4 ColorInfo { get; set; } = new(0.89f, 0.74f, 0.42f, 1.00f);
    public Vector4 ColorAlert { get; set; } = new(0.98f, 0.62f, 0.35f, 1.00f);
    public Vector4 ColorAlarm { get; set; } = new(0.95f, 0.30f, 0.30f, 1.00f);

    /// <summary>Saves reach here from the render thread (settings widgets), the
    /// framework thread (/nyaa lock) and the plugin-manager thread (unload).
    /// Concurrent writes to one file throw and lose the write, so they queue.</summary>
    public void Save()
    {
        lock (SaveLock)
        {
            Services.PluginInterface.SavePluginConfig(this);
        }
    }

    private static readonly object SaveLock = new();

    /// <summary>Carry a version 1 config forward: the single backdrop opacity
    /// becomes both per-box opacities.</summary>
    public void MigrateFromV1()
    {
        TimelineBgOpacity = BgOpacity;
        AlertsBgOpacity = BgOpacity;
        Version = 2;
    }

    /// <summary>Restore the shipped palette and sizing, leaving the link
    /// settings and window placement alone.</summary>
    public void ResetAppearance()
    {
        var fresh = new Configuration();
        TimelineTextScale = fresh.TimelineTextScale;
        AlertsTextScale = fresh.AlertsTextScale;
        DpsTextScale = fresh.DpsTextScale;
        HighQualityText = fresh.HighQualityText;
        TextEffect = fresh.TextEffect;
        OutlineThickness = fresh.OutlineThickness;
        ColorOutline = fresh.ColorOutline;
        TimelineBgOpacity = fresh.TimelineBgOpacity;
        AlertsBgOpacity = fresh.AlertsBgOpacity;
        DpsBgOpacity = fresh.DpsBgOpacity;
        DpsStyle = fresh.DpsStyle;
        BarHeight = fresh.BarHeight;
        BarSpacing = fresh.BarSpacing;
        BarRounding = fresh.BarRounding;
        BarBorderThickness = fresh.BarBorderThickness;
        BarTrackOpacity = fresh.BarTrackOpacity;
        BarFill = fresh.BarFill;
        BarRightToLeft = fresh.BarRightToLeft;
        BarTextAlign = fresh.BarTextAlign;
        ImminentSeconds = fresh.ImminentSeconds;
        ImminentPulse = fresh.ImminentPulse;
        Countdown = fresh.Countdown;
        CountdownSplit = fresh.CountdownSplit;
        TimelineWindow = fresh.TimelineWindow;
        TimelineRows = fresh.TimelineRows;
        AlertSeconds = fresh.AlertSeconds;
        AlertsMaxVisible = fresh.AlertsMaxVisible;
        AlertOrder = fresh.AlertOrder;
        AlertsAlign = fresh.AlertsAlign;
        AlertsAnimate = fresh.AlertsAnimate;
        ColorBar = fresh.ColorBar;
        ColorBarText = fresh.ColorBarText;
        ColorBarBorder = fresh.ColorBarBorder;
        ColorImminent = fresh.ColorImminent;
        ColorInfo = fresh.ColorInfo;
        ColorAlert = fresh.ColorAlert;
        ColorAlarm = fresh.ColorAlarm;
    }
}
