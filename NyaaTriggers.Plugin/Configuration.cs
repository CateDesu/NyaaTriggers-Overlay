using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using Dalamud.Configuration;

namespace NyaaTriggers.Plugin;

/// <summary>How overlay text is kept readable over the game: nothing, a hard
/// outline, or a soft glow. The outline stamps the text around concentric
/// rings; the glow stacks wider, fainter rings for a halo.</summary>
internal enum TextEffectStyle
{
    Off,
    Outline,
    Glow,
}

/// <summary>How the dps meter draws its rows: timeline-style share bars, the
/// Horizon Overlay's side-by-side job-coloured segments, or kagerou's
/// underlined text. The members serialize as their integer values, so the
/// rename from Horizoverlay did not disturb stored configs.</summary>
internal enum DpsMeterStyle
{
    Bars,
    HorizonOverlay,
    Kagerou,
}

/// <summary>The Horizon Overlay bar palette: the ACT original's red/blue/green
/// by role, or its black &amp; white theme where only the local player's bar is
/// white and everyone else is dark.</summary>
internal enum HorizonColorTheme
{
    ByRole,
    BlackWhite,
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

/// <summary>How other players' names show in the dps meter.</summary>
internal enum NamePrivacyStyle
{
    Shown,
    Initials,
    Hidden,
}

/// <summary>How the dps meter orders its rows. ByDps is the feed's own rank
/// order; the others re-sort but keep each row's real rank number.</summary>
internal enum DpsSortOrder
{
    ByDps,
    Alphabetical,
    ByRole,
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
    /// can be migrated rather than silently reinterpreted. Note the enums in
    /// this file serialize as their integer values: never reorder or insert
    /// members without bumping this and writing the migration.</summary>
    public int Version { get; set; } = 4;

    // ── link ──────────────────────────────────────────────────────────────
    /// <summary>Loopback port the desktop app connects to. Not exposed off the
    /// machine: the listener binds 127.0.0.1 and ::1 only. As a companion
    /// plugin it always listens; there is no off switch.</summary>
    public int Port { get; set; } = 27080;

    /// <summary>Run the dps meter off IINACT directly while the program is not
    /// connected, so the meter works for someone who never runs it. The
    /// program's feed always wins while it is connected.</summary>
    public bool StandaloneMeter { get; set; }

    /// <summary>Where IINACT serves the ACT combat feed. Loopback by default,
    /// same stance as the link port.</summary>
    public string IinactEndpoint { get; set; } = "ws://127.0.0.1:10501/ws";

    // ── what to draw ──────────────────────────────────────────────────────
    public bool ShowTimeline { get; set; } = true;
    public bool ShowAlerts { get; set; } = true;
    public bool ShowDps { get; set; } = true;

    /// <summary>How the dps meter draws its rows.</summary>
    public DpsMeterStyle DpsStyle { get; set; } = DpsMeterStyle.Bars;

    /// <summary>The Horizon Overlay palette: by role (red/blue/green) or the
    /// black &amp; white theme. Only read by the Horizon Overlay style.</summary>
    public HorizonColorTheme DpsHorizTheme { get; set; } = HorizonColorTheme.ByRole;

    /// <summary>Locked: chromeless and click-through, i.e. the raid-night state.
    /// Unlocked shows a frame and sample content so the boxes can be placed.</summary>
    public bool Locked { get; set; }

    // ── visibility, per box ───────────────────────────────────────────────
    // The duty and combat filters were shared by every box before version 4.
    // Per box so the meter can ride every pull while the timeline only shows
    // inside duties, or any other mix. Each pair stacks on its own box: both
    // ticked means that box shows only for a fight inside a duty.
    public bool TimelineOnlyInDuty { get; set; }
    public bool TimelineOnlyInCombat { get; set; }
    public bool AlertsOnlyInDuty { get; set; }
    public bool AlertsOnlyInCombat { get; set; }
    public bool DpsOnlyInDuty { get; set; }
    public bool DpsOnlyInCombat { get; set; }

    // ── geometry (screen pixels, persisted ourselves: the overlay windows use
    //     NoSavedSettings so imgui.ini never fights us) ────────────────────
    public Vector2 TimelinePos { get; set; } = new(80, 200);
    public Vector2 TimelineSize { get; set; } = new(320, 220);
    public Vector2 AlertsPos { get; set; } = new(80, 440);
    public Vector2 AlertsSize { get; set; } = new(420, 160);
    public Vector2 DpsPos { get; set; } = new(80, 620);
    public Vector2 DpsSize { get; set; } = new(320, 240);

    // ── timeline box ──────────────────────────────────────────────────────
    /// <summary>Text scale inside the timeline box: the bar labels and their
    /// countdowns. Bar row heights scale with it so the text stays inside.</summary>
    public float TimelineTextScale { get; set; } = 1.0f;

    /// <summary>Backdrop alpha behind the timeline box's content: 0 =
    /// invisible (the raid-night default; the box floats bare text/bars over
    /// the game), up to 1 = solid theme background.</summary>
    public float TimelineBgOpacity { get; set; }

    /// <summary>Whole-box opacity multiplier: every colour the timeline box
    /// draws, backdrop included, is scaled by it.</summary>
    public float TimelineFade { get; set; } = 1.0f;

    /// <summary>What is drawn behind the timeline's text to keep it readable
    /// over bright arenas.</summary>
    public TextEffectStyle TimelineTextEffect { get; set; } = TextEffectStyle.Outline;

    /// <summary>Effect reach in pixels: outline radius or glow spread.</summary>
    public int TimelineEffectThickness { get; set; } = 1;

    /// <summary>Effect colour. The alpha is the effect's opacity and is scaled
    /// by the text's own fade.</summary>
    public Vector4 TimelineEffectColor { get; set; } = new(0.0f, 0.0f, 0.0f, 0.9f);

    /// <summary>Bar label and countdown colour.</summary>
    public Vector4 TimelineTextColor { get; set; } = new(0.95f, 0.95f, 0.98f, 1.00f);

    /// <summary>Bar row height before the text-scale multiplier.</summary>
    public float TimelineBarHeight { get; set; } = 22.0f;

    /// <summary>Gap between bar rows.</summary>
    public float TimelineBarSpacing { get; set; } = 4.0f;

    /// <summary>Corner rounding on the bar and its track.</summary>
    public float TimelineBarRounding { get; set; } = 3.0f;

    /// <summary>Border drawn around each bar, 0 = no border.</summary>
    public float TimelineBarBorderThickness { get; set; }

    /// <summary>Alpha multiplier for the full-length slot under a bar's fill.</summary>
    public float TimelineBarTrackOpacity { get; set; } = 1.0f;

    public Vector4 TimelineBarColor { get; set; } = new(0.55f, 0.44f, 0.78f, 0.85f);

    /// <summary>The full-length slot under the fill. Dark by default so the
    /// fill still reads against it at full track opacity.</summary>
    public Vector4 TimelineBarTrackColor { get; set; } = new(0.0f, 0.0f, 0.0f, 0.60f);

    public Vector4 TimelineBarBorderColor { get; set; } = new(0.00f, 0.00f, 0.00f, 0.80f);

    // ── timeline behaviour ────────────────────────────────────────────────
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

    /// <summary>The fight clock as a line above the bars, mm:ss. Off by
    /// default: the meter's encounter line already carries a duration, and
    /// this is for layouts that hide the meter.</summary>
    public bool TimelineShowClock { get; set; }

    /// <summary>Bars hug the box's bottom edge and the stack grows upward,
    /// for boxes parked just above the hotbars.</summary>
    public bool TimelineAnchorBottom { get; set; }

    /// <summary>A cue that reaches zero flashes as a full bar for a beat
    /// instead of vanishing, so the moment it fires reads on screen.</summary>
    public bool TimelineFireFlash { get; set; } = true;

    /// <summary>Fill colour for bars about to fire.</summary>
    public Vector4 ColorImminent { get; set; } = new(0.90f, 0.28f, 0.28f, 0.95f);

    /// <summary>Colour each bar by the kind the app tagged its label with:
    /// tankbuster, raidwide and mechanic get their own fill, anything untagged
    /// keeps the shared bar colour. The imminent colour still wins near zero.</summary>
    public bool TimelineKindColors { get; set; }

    /// <summary>Bar fill for cues the app tagged tankbuster.</summary>
    public Vector4 TimelineTankbusterColor { get; set; } = new(0.92f, 0.48f, 0.20f, 0.85f);

    /// <summary>Bar fill for cues the app tagged raidwide.</summary>
    public Vector4 TimelineRaidwideColor { get; set; } = new(0.35f, 0.62f, 0.92f, 0.85f);

    /// <summary>Bar fill for cues the app tagged mechanic. Ships matching the
    /// shared bar colour, so turning kind colours on only moves the tankbuster
    /// and raidwide bars until this one is recoloured. Untagged and unknown
    /// kinds keep the shared bar colour.</summary>
    public Vector4 TimelineMechanicColor { get; set; } = new(0.55f, 0.44f, 0.78f, 0.85f);

    // ── alerts box ────────────────────────────────────────────────────────
    /// <summary>Text scale inside the alerts box.</summary>
    public float AlertsTextScale { get; set; } = 1.0f;

    /// <summary>Backdrop alpha behind the alerts box's content.</summary>
    public float AlertsBgOpacity { get; set; }

    /// <summary>Whole-box opacity multiplier for the alerts box.</summary>
    public float AlertsFade { get; set; } = 1.0f;

    /// <summary>What is drawn behind callout text to keep it readable.</summary>
    public TextEffectStyle AlertsTextEffect { get; set; } = TextEffectStyle.Outline;

    /// <summary>Effect reach in pixels: outline radius or glow spread.</summary>
    public int AlertsEffectThickness { get; set; } = 1;

    /// <summary>Effect colour. The alpha is the effect's opacity and is scaled
    /// by the callout's own fade.</summary>
    public Vector4 AlertsEffectColor { get; set; } = new(0.0f, 0.0f, 0.0f, 0.9f);

    /// <summary>Seconds an info callout stays up when the app does not specify
    /// one. The field keeps the pre-v4 name so stored configs still load.</summary>
    public float AlertSeconds { get; set; } = 4.0f;

    /// <summary>Seconds an alert callout stays up, same fallback rule.</summary>
    public float AlertSecondsAlert { get; set; } = 4.0f;

    /// <summary>Seconds an alarm callout stays up. Longer than the rest by
    /// default: the loudest callout should linger. Same fallback rule.</summary>
    public float AlertSecondsAlarm { get; set; } = 6.0f;

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

    /// <summary>How much bigger an alarm callout draws than the box's body
    /// text, 1.0 keeping it level. The loudest line earning the most pixels is
    /// the point of an alarm.</summary>
    public float AlertsAlarmScale { get; set; } = 1.0f;

    /// <summary>A thin strip under each callout that empties as its time runs
    /// out, so the eye can tell a stale callout from a fresh one.</summary>
    public bool AlertsLifeline { get; set; }

    /// <summary>Per-severity filters. Hiding info callouts is the common one:
    /// they are spoken anyway, and the box stays quiet for everything but
    /// what must be seen.</summary>
    public bool AlertsShowInfo { get; set; } = true;
    public bool AlertsShowAlert { get; set; } = true;
    public bool AlertsShowAlarm { get; set; } = true;

    /// <summary>Callouts hug the box's bottom edge and the stack grows
    /// upward, for boxes parked low on the screen.</summary>
    public bool AlertsAnchorBottom { get; set; }

    /// <summary>Wrap long callouts inside the box. Off draws each callout as
    /// one line ending in an ellipsis, so the box stays one row per callout.</summary>
    public bool AlertsWrap { get; set; } = true;

    /// <summary>A repeat of the callout already on top folds into it as a
    /// times counter instead of stacking another row. Chatty triggers that
    /// refire the same text stay one line.</summary>
    public bool AlertsCollapseDupes { get; set; } = true;

    /// <summary>A faint plate in the callout's severity colour behind each
    /// callout, so the colour reads in peripheral vision.</summary>
    public bool AlertsSeverityTint { get; set; }

    /// <summary>Opacity of the severity plate, scaled by the callout's own
    /// fade.</summary>
    public float AlertsSeverityTintOpacity { get; set; } = 0.30f;

    /// <summary>Pulse a border around the alerts box while an alarm callout
    /// is up.</summary>
    public bool AlertsAlarmFlash { get; set; } = true;

    /// <summary>Glow the screen edges in the alarm colour while an alarm
    /// callout is up. Loud and locked to the raid-night state, so it never
    /// fires over the settings boxes.</summary>
    public bool AlarmScreenFlash { get; set; }

    /// <summary>How far the screen flash reaches in from each edge, as a share
    /// of the screen's shorter side.</summary>
    public float AlarmScreenFlashSize { get; set; } = 0.15f;

    public Vector4 ColorInfo { get; set; } = new(0.89f, 0.74f, 0.42f, 1.00f);
    public Vector4 ColorAlert { get; set; } = new(0.98f, 0.62f, 0.35f, 1.00f);
    public Vector4 ColorAlarm { get; set; } = new(0.95f, 0.30f, 0.30f, 1.00f);

    // ── dps box ───────────────────────────────────────────────────────────
    /// <summary>Text scale inside the dps meter box.</summary>
    public float DpsTextScale { get; set; } = 1.0f;

    /// <summary>Backdrop alpha behind the dps meter box's content.</summary>
    public float DpsBgOpacity { get; set; }

    /// <summary>Whole-box opacity multiplier for the dps meter box.</summary>
    public float DpsFade { get; set; } = 1.0f;

    /// <summary>What is drawn behind the meter's text to keep it readable.</summary>
    public TextEffectStyle DpsTextEffect { get; set; } = TextEffectStyle.Outline;

    /// <summary>Effect reach in pixels: outline radius or glow spread.</summary>
    public int DpsEffectThickness { get; set; } = 1;

    /// <summary>Effect colour. The alpha is the effect's opacity.</summary>
    public Vector4 DpsEffectColor { get; set; } = new(0.0f, 0.0f, 0.0f, 0.9f);

    /// <summary>Name and number colour.</summary>
    public Vector4 DpsTextColor { get; set; } = new(0.95f, 0.95f, 0.98f, 1.00f);

    /// <summary>Show only the local player's row, the original's solo mode.
    /// Applies to every dps style.</summary>
    public bool DpsSoloOnly { get; set; }

    /// <summary>Pin the local player's row ahead of everyone else's whatever
    /// their rank, keeping its real rank number. Top of the Bars and Kagerou
    /// lists, left end of the Horizon Overlay strip. Applies to every dps
    /// style.</summary>
    public bool DpsSelfFirst { get; set; }

    /// <summary>Row order in the meter. Alphabetical and by role keep each
    /// row's real rank number, so the list can read by name or role without
    /// lying about who parsed where. Applies to every dps style.</summary>
    public DpsSortOrder DpsSortOrder { get; set; } = DpsSortOrder.ByDps;

    /// <summary>Other players' names: shown whole, reduced to initials, or
    /// hidden outright. The streamer knobs. Applies to every dps style.</summary>
    public NamePrivacyStyle DpsNamePrivacy { get; set; } = NamePrivacyStyle.Shown;

    /// <summary>Show the local player as YOU instead of their name, the way
    /// ACT's own overlays do. Applies to every dps style.</summary>
    public bool DpsSelfNameYou { get; set; }

    /// <summary>Bars and Kagerou styles: rank numbers on the rows. The Horizon
    /// Overlay has its own rank knob.</summary>
    public bool DpsRowsShowRank { get; set; } = true;

    /// <summary>Bars and Kagerou styles: the job icon before the name.</summary>
    public bool DpsRowsShowIcons { get; set; }

    /// <summary>Bars and Kagerou styles: lighten every other row so lines are
    /// easier to track across a wide box.</summary>
    public bool DpsRowStripes { get; set; }

    /// <summary>How strongly the striped rows lighten, 0 to 0.5.</summary>
    public float DpsRowStripeOpacity { get; set; } = 0.08f;

    /// <summary>Bars style only: fill the rank 1 bar with its own colour so
    /// the top of the parse reads at a glance. Self highlight still wins.</summary>
    public bool DpsBarTopHighlight { get; set; }

    /// <summary>The rank 1 bar fill when DpsBarTopHighlight is on.</summary>
    public Vector4 DpsBarTopColor { get; set; } = new(0.98f, 0.80f, 0.25f, 0.85f);

    /// <summary>Show each member's death count beside the name, red, when the
    /// feed carries it. Needs names shown in the Horizon Overlay strip.</summary>
    public bool DpsShowDeaths { get; set; }

    /// <summary>Keep the final meter on screen after the encounter ends,
    /// until the next pull starts or the zone changes. Off hides the box the
    /// moment the fight does.</summary>
    public bool DpsHoldLast { get; set; }

    /// <summary>Encounter line layout. Empty joins title, duration and party
    /// dps with a dot, the long standing look. The tokens {title} {duration}
    /// {dps} place those parts freely, so the line can be reordered or
    /// reworded.</summary>
    public string DpsHeaderFormat { get; set; } = string.Empty;

    /// <summary>How many members at most the meter shows, the original's
    /// # combatants. The feed carries up to a 24-man alliance; eight covers
    /// a full party. Applies to every dps style.</summary>
    public int DpsMaxRows { get; set; } = 8;

    /// <summary>Bar row height before the text-scale multiplier.</summary>
    public float DpsBarHeight { get; set; } = 22.0f;

    /// <summary>Gap between bar rows.</summary>
    public float DpsBarSpacing { get; set; } = 4.0f;

    /// <summary>Corner rounding on the bar and its track.</summary>
    public float DpsBarRounding { get; set; } = 3.0f;

    /// <summary>Border drawn around each bar, 0 = no border.</summary>
    public float DpsBarBorderThickness { get; set; }

    /// <summary>Alpha multiplier for the full-length slot under a bar's fill.</summary>
    public float DpsBarTrackOpacity { get; set; } = 1.0f;

    public Vector4 DpsBarColor { get; set; } = new(0.55f, 0.44f, 0.78f, 0.85f);

    /// <summary>The full-length slot under the fill. Dark by default so the
    /// fill still reads against it at full track opacity.</summary>
    public Vector4 DpsBarTrackColor { get; set; } = new(0.0f, 0.0f, 0.0f, 0.60f);

    public Vector4 DpsBarBorderColor { get; set; } = new(0.00f, 0.00f, 0.00f, 0.80f);

    /// <summary>Anchor the share bars' fill to the right edge instead of the
    /// left.</summary>
    public bool DpsBarRightToLeft { get; set; }

    /// <summary>Bars style only: fill each bar in the member's job colour at
    /// the configured bar colour's alpha, like kagerou's underlines, instead
    /// of the single shared tint.</summary>
    public bool DpsBarJobColors { get; set; }

    /// <summary>Bars style only: put the damage share beside the dps figure
    /// pinned to the right edge.</summary>
    public bool DpsBarsShowShare { get; set; }

    /// <summary>Bars and Kagerou styles: append the member's hps to the row
    /// numbers. Rows with no healing recorded skip it.</summary>
    public bool DpsRowsShowHps { get; set; }

    /// <summary>Bars style only: fill the local player's bar with its own
    /// colour so it reads at a glance. Wins over job coloured bars.</summary>
    public bool DpsBarSelfHighlight { get; set; }

    /// <summary>The local player's bar fill when DpsBarSelfHighlight is on.</summary>
    public Vector4 DpsBarSelfColor { get; set; } = new(1.00f, 1.00f, 1.00f, 0.85f);

    // ── dps box: the Horizon Overlay style's own knobs ────────────────────
    // The serialized names keep the Horiz stem from before the rename so
    // configs written by older builds still load.

    /// <summary>The member name centred above each bar. Off reclaims the line
    /// and lifts the bars to the top of the box.</summary>
    public bool DpsHorizShowNames { get; set; } = true;

    /// <summary>Rank numbers before the names.</summary>
    public bool DpsHorizShowRank { get; set; } = true;

    /// <summary>The job icon straddling each bar's top edge.</summary>
    public bool DpsHorizShowIcons { get; set; } = true;

    /// <summary>HPS inside the bar. Off puts the job acronym in its slot,
    /// which is what the ACT original does.</summary>
    public bool DpsHorizShowHps { get; set; } = true;

    /// <summary>The two-tone bar: faint overall, solid on the side of the
    /// member's relevant stat. Off is one flat tint.</summary>
    public bool DpsHorizHighlight { get; set; } = true;

    /// <summary>The thin damage-share strip under each bar, plus its percent
    /// figure.</summary>
    public bool DpsHorizShowPercent { get; set; } = true;

    /// <summary>Widest a single member's bar may grow, before the text-scale
    /// multiplier. A narrower window still shrinks every cell equally. The ACT
    /// original caps at 140px.</summary>
    public float DpsHorizMaxBarWidth { get; set; } = 140.0f;

    /// <summary>Bar thickness before the text-scale multiplier. Tall enough by
    /// default that the bottom-anchored stats sit clear of the job icon
    /// straddling the top edge.</summary>
    public float DpsHorizBarHeight { get; set; } = 32.0f;

    /// <summary>The parallelogram lean in degrees, 0 being a plain rectangle.
    /// The ACT original leans 30.</summary>
    public float DpsHorizSkew { get; set; } = 30.0f;

    /// <summary>Edge length of the job icon before the text-scale multiplier.</summary>
    public float DpsHorizIconSize { get; set; } = 20.0f;

    /// <summary>Empty space on either side of a cell. The ACT original's
    /// margin is 6px around a 140px bar.</summary>
    public float DpsHorizCellPadding { get; set; } = 6.0f;

    /// <summary>Size of the in-bar hps and dps figures relative to the box's
    /// body text, 0.4 to 1.5. The names share it.</summary>
    public float DpsHorizStatScale { get; set; } = 0.80f;

    /// <summary>Decimal places on the in-bar dps figure, 0 to 2.</summary>
    public int DpsHorizDecimals { get; set; } = 2;

    /// <summary>Shorten the in-bar dps figure to the header's compact shape,
    /// 10234.50 becoming 10.2k.</summary>
    public bool DpsHorizCompact { get; set; }

    /// <summary>Alpha of a bar's solid side. The faint side of the two-tone
    /// follows at a third of it.</summary>
    public float DpsHorizBarOpacity { get; set; } = 0.30f;

    // The default bar tints are the ACT original's own rgb() values at full
    // alpha; the configured bar opacity scales the role tints at draw time.
    /// <summary>The local player's bar, white in both themes.</summary>
    public Vector4 DpsHorizSelfColor { get; set; } = new(1.000f, 1.000f, 1.000f, 0.80f);

    /// <summary>Stat text on the local player's bar, plain black against the
    /// white bar, drawn without the text effect.</summary>
    public Vector4 DpsHorizSelfTextColor { get; set; } = new(0.000f, 0.000f, 0.000f, 1.00f);

    /// <summary>Bar tint for dps jobs in the by-role theme.</summary>
    public Vector4 DpsHorizDpsColor { get; set; } = new(0.957f, 0.263f, 0.212f, 1.00f);

    /// <summary>Bar tint for tanks in the by-role theme.</summary>
    public Vector4 DpsHorizTankColor { get; set; } = new(0.129f, 0.588f, 0.953f, 1.00f);

    /// <summary>Bar tint for healers in the by-role theme.</summary>
    public Vector4 DpsHorizHealerColor { get; set; } = new(0.545f, 0.765f, 0.290f, 1.00f);

    /// <summary>Bar for jobs we do not know, and for everyone but the local
    /// player in the black &amp; white theme.</summary>
    public Vector4 DpsHorizDimColor { get; set; } = new(0.000f, 0.000f, 0.000f, 0.30f);

    // ── dps box: the encounter line, shared by every style ────────────────
    /// <summary>The encounter line at all: title, duration and party dps.</summary>
    public bool DpsShowHeader { get; set; } = true;

    /// <summary>The fight clock in the encounter line.</summary>
    public bool DpsHeaderDuration { get; set; } = true;

    /// <summary>The party's dps in the encounter line.</summary>
    public bool DpsHeaderTotalDps { get; set; } = true;

    // ── profiles ────────────────────────────────────────────────────────────
    /// <summary>Named appearance snapshots: profile name to a serialized
    /// Configuration blob from SnapshotAppearance. Only the appearance knobs
    /// come back on apply; the link, placement and visibility stay as they
    /// are.</summary>
    public Dictionary<string, string> AppearanceProfiles { get; set; } = new();

    /// <summary>Profile blob options. The colour knobs are Vector4, whose X Y
    /// Z W are public fields, and default options skip fields: a blob would
    /// carry every colour as an empty object and applying it would wipe them
    /// to transparent black. One shared instance, both directions.</summary>
    private static readonly JsonSerializerOptions ProfileOptions = new()
    {
        IncludeFields = true,
    };

    /// <summary>This configuration as a JSON blob for AppearanceProfiles,
    /// minus the profiles themselves so saved blobs do not nest.</summary>
    public string SnapshotAppearance()
    {
        var node = JsonSerializer.SerializeToNode(this, ProfileOptions)!.AsObject();
        node.Remove(nameof(this.AppearanceProfiles));
        return node.ToJsonString();
    }

    /// <summary>Bring a SnapshotAppearance blob's appearance knobs in. False
    /// on a blob that does not parse, so a mangled stored profile leaves the
    /// current look alone. A hand edited config can put a JSON null in the
    /// dictionary, which materializes as a null blob, so that is refused up
    /// front rather than trusted to the parser.</summary>
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

    /// <summary>Clipboard text to a clean profile blob, or null when it is not
    /// one. The parse is validated then re-serialized, so an imported profile
    /// carries only what SnapshotAppearance would have written and never a
    /// hand edited oddity like a nested profiles dictionary. The size cap
    /// keeps a clipboard stuffed with something huge out of the parser.</summary>
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

    // ── legacy: the shared pre-v3 look, kept only so MigrateFromV2 can read
    //     the old values, the same pattern as BgOpacity from v1 ────────────
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

    /// <summary>Version 1's single backdrop opacity, split per box in version
    /// 2. Kept only so the migration can read the old value.</summary>
    public float BgOpacity { get; set; }

    /// <summary>Version 3's shared visibility filters, per box in version 4.
    /// Kept only so MigrateFromV3 can read the old values.</summary>
    public bool OnlyInDuty { get; set; }
    public bool OnlyInCombat { get; set; }

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

    /// <summary>Carry a version 2 config forward: the shared bar look and text
    /// effect become each box's own, and the retired shadow effect folds into
    /// the outline. The old shared track opacity is deliberately not carried:
    /// the track is a colour of its own now and starts fully visible.</summary>
    public void MigrateFromV2()
    {
        // v2's Off stays Off; its Shadow and Outline both land on the outline.
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

    /// <summary>Carry a version 3 config forward: the shared visibility filters
    /// become each box's own, and the single alert time seeds the per severity
    /// times so the on screen rhythm does not change for an upgrader.</summary>
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

    /// <summary>Restore the shipped palette and sizing, leaving the link
    /// settings and window placement alone.</summary>
    public void ResetAppearance() => this.CopyAppearanceFrom(new Configuration());

    /// <summary>Copy every appearance knob over from another configuration,
    /// leaving the link, placement, visibility and profiles alone. Applying a
    /// saved profile and resetting to defaults differ only in the source.</summary>
    public void CopyAppearanceFrom(Configuration fresh)
    {
        TimelineTextScale = fresh.TimelineTextScale;
        TimelineBgOpacity = fresh.TimelineBgOpacity;
        TimelineFade = fresh.TimelineFade;
        TimelineTextEffect = fresh.TimelineTextEffect;
        TimelineEffectThickness = fresh.TimelineEffectThickness;
        TimelineEffectColor = fresh.TimelineEffectColor;
        TimelineTextColor = fresh.TimelineTextColor;
        TimelineBarHeight = fresh.TimelineBarHeight;
        TimelineBarSpacing = fresh.TimelineBarSpacing;
        TimelineBarRounding = fresh.TimelineBarRounding;
        TimelineBarBorderThickness = fresh.TimelineBarBorderThickness;
        TimelineBarTrackOpacity = fresh.TimelineBarTrackOpacity;
        TimelineBarColor = fresh.TimelineBarColor;
        TimelineBarTrackColor = fresh.TimelineBarTrackColor;
        TimelineBarBorderColor = fresh.TimelineBarBorderColor;
        BarFill = fresh.BarFill;
        BarRightToLeft = fresh.BarRightToLeft;
        BarTextAlign = fresh.BarTextAlign;
        ImminentSeconds = fresh.ImminentSeconds;
        ImminentPulse = fresh.ImminentPulse;
        Countdown = fresh.Countdown;
        CountdownSplit = fresh.CountdownSplit;
        TimelineWindow = fresh.TimelineWindow;
        TimelineRows = fresh.TimelineRows;
        TimelineShowClock = fresh.TimelineShowClock;
        TimelineAnchorBottom = fresh.TimelineAnchorBottom;
        TimelineFireFlash = fresh.TimelineFireFlash;
        ColorImminent = fresh.ColorImminent;
        TimelineKindColors = fresh.TimelineKindColors;
        TimelineTankbusterColor = fresh.TimelineTankbusterColor;
        TimelineRaidwideColor = fresh.TimelineRaidwideColor;
        TimelineMechanicColor = fresh.TimelineMechanicColor;
        AlertsTextScale = fresh.AlertsTextScale;
        AlertsBgOpacity = fresh.AlertsBgOpacity;
        AlertsFade = fresh.AlertsFade;
        AlertsTextEffect = fresh.AlertsTextEffect;
        AlertsEffectThickness = fresh.AlertsEffectThickness;
        AlertsEffectColor = fresh.AlertsEffectColor;
        AlertSeconds = fresh.AlertSeconds;
        AlertSecondsAlert = fresh.AlertSecondsAlert;
        AlertSecondsAlarm = fresh.AlertSecondsAlarm;
        AlertsMaxVisible = fresh.AlertsMaxVisible;
        AlertOrder = fresh.AlertOrder;
        AlertsAlign = fresh.AlertsAlign;
        AlertsAnimate = fresh.AlertsAnimate;
        AlertsAlarmScale = fresh.AlertsAlarmScale;
        AlertsLifeline = fresh.AlertsLifeline;
        AlertsShowInfo = fresh.AlertsShowInfo;
        AlertsShowAlert = fresh.AlertsShowAlert;
        AlertsShowAlarm = fresh.AlertsShowAlarm;
        AlertsAnchorBottom = fresh.AlertsAnchorBottom;
        AlertsWrap = fresh.AlertsWrap;
        AlertsCollapseDupes = fresh.AlertsCollapseDupes;
        AlertsSeverityTint = fresh.AlertsSeverityTint;
        AlertsSeverityTintOpacity = fresh.AlertsSeverityTintOpacity;
        AlertsAlarmFlash = fresh.AlertsAlarmFlash;
        AlarmScreenFlash = fresh.AlarmScreenFlash;
        AlarmScreenFlashSize = fresh.AlarmScreenFlashSize;
        ColorInfo = fresh.ColorInfo;
        ColorAlert = fresh.ColorAlert;
        ColorAlarm = fresh.ColorAlarm;
        DpsTextScale = fresh.DpsTextScale;
        DpsBgOpacity = fresh.DpsBgOpacity;
        DpsFade = fresh.DpsFade;
        DpsTextEffect = fresh.DpsTextEffect;
        DpsEffectThickness = fresh.DpsEffectThickness;
        DpsEffectColor = fresh.DpsEffectColor;
        DpsTextColor = fresh.DpsTextColor;
        DpsSoloOnly = fresh.DpsSoloOnly;
        DpsSelfFirst = fresh.DpsSelfFirst;
        DpsSortOrder = fresh.DpsSortOrder;
        DpsNamePrivacy = fresh.DpsNamePrivacy;
        DpsSelfNameYou = fresh.DpsSelfNameYou;
        DpsRowsShowRank = fresh.DpsRowsShowRank;
        DpsRowsShowIcons = fresh.DpsRowsShowIcons;
        DpsRowStripes = fresh.DpsRowStripes;
        DpsRowStripeOpacity = fresh.DpsRowStripeOpacity;
        DpsMaxRows = fresh.DpsMaxRows;
        DpsBarHeight = fresh.DpsBarHeight;
        DpsBarSpacing = fresh.DpsBarSpacing;
        DpsBarRounding = fresh.DpsBarRounding;
        DpsBarBorderThickness = fresh.DpsBarBorderThickness;
        DpsBarTrackOpacity = fresh.DpsBarTrackOpacity;
        DpsBarColor = fresh.DpsBarColor;
        DpsBarTrackColor = fresh.DpsBarTrackColor;
        DpsBarBorderColor = fresh.DpsBarBorderColor;
        DpsBarRightToLeft = fresh.DpsBarRightToLeft;
        DpsBarJobColors = fresh.DpsBarJobColors;
        DpsBarsShowShare = fresh.DpsBarsShowShare;
        DpsRowsShowHps = fresh.DpsRowsShowHps;
        DpsBarSelfHighlight = fresh.DpsBarSelfHighlight;
        DpsBarSelfColor = fresh.DpsBarSelfColor;
        DpsBarTopHighlight = fresh.DpsBarTopHighlight;
        DpsBarTopColor = fresh.DpsBarTopColor;
        DpsShowDeaths = fresh.DpsShowDeaths;
        DpsHoldLast = fresh.DpsHoldLast;
        DpsHeaderFormat = fresh.DpsHeaderFormat;
        DpsStyle = fresh.DpsStyle;
        DpsHorizTheme = fresh.DpsHorizTheme;
        DpsHorizShowNames = fresh.DpsHorizShowNames;
        DpsHorizShowRank = fresh.DpsHorizShowRank;
        DpsHorizShowIcons = fresh.DpsHorizShowIcons;
        DpsHorizShowHps = fresh.DpsHorizShowHps;
        DpsHorizHighlight = fresh.DpsHorizHighlight;
        DpsHorizShowPercent = fresh.DpsHorizShowPercent;
        DpsHorizMaxBarWidth = fresh.DpsHorizMaxBarWidth;
        DpsHorizBarHeight = fresh.DpsHorizBarHeight;
        DpsHorizSkew = fresh.DpsHorizSkew;
        DpsHorizIconSize = fresh.DpsHorizIconSize;
        DpsHorizCellPadding = fresh.DpsHorizCellPadding;
        DpsHorizStatScale = fresh.DpsHorizStatScale;
        DpsHorizDecimals = fresh.DpsHorizDecimals;
        DpsHorizCompact = fresh.DpsHorizCompact;
        DpsHorizBarOpacity = fresh.DpsHorizBarOpacity;
        DpsHorizSelfColor = fresh.DpsHorizSelfColor;
        DpsHorizSelfTextColor = fresh.DpsHorizSelfTextColor;
        DpsHorizDpsColor = fresh.DpsHorizDpsColor;
        DpsHorizTankColor = fresh.DpsHorizTankColor;
        DpsHorizHealerColor = fresh.DpsHorizHealerColor;
        DpsHorizDimColor = fresh.DpsHorizDimColor;
        DpsShowHeader = fresh.DpsShowHeader;
        DpsHeaderDuration = fresh.DpsHeaderDuration;
        DpsHeaderTotalDps = fresh.DpsHeaderTotalDps;
    }
}
