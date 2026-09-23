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
    LMeter,
    HorizonOverlay,
    Kagerou,
}

internal enum KagerouTab
{
    Dps,
    Tank,
    Heal,
    Alliance,
}

internal enum KagerouDeathHeading
{
    Dead,
    Deaths,
    D,
    Hidden,
}

internal enum HorizonColorTheme
{
    ByRole,
    BlackWhite,
}

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

// Flat meter fields keep older settings readable.
[Serializable]
internal sealed class Configuration : MeterSettings, IPluginConfiguration
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
        TimelineSize = BoundSize(TimelineSize);
        AlertsSize = BoundSize(AlertsSize);
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
        BarHeight = Math.Clamp(BarHeight, 12.0f, 48.0f);
        BarSpacing = Math.Clamp(BarSpacing, 0.0f, 16.0f);
        BarRounding = Math.Clamp(BarRounding, 0.0f, 12.0f);
        BarBorderThickness = Math.Clamp(BarBorderThickness, 0.0f, 4.0f);
        BgOpacity = Math.Clamp(BgOpacity, 0.0f, 1.0f);
        TimelineEffectThickness = Math.Clamp(TimelineEffectThickness, 0, 4);
        AlertsEffectThickness = Math.Clamp(AlertsEffectThickness, 0, 4);
        OutlineThickness = Math.Clamp(OutlineThickness, 0, 4);
        TimelineRows = Math.Clamp(TimelineRows, 1, 12);
        AlertsMaxVisible = Math.Clamp(AlertsMaxVisible, 1, 8);
        this.SanitizeMeter();
        this.BarsMeter?.SanitizeMeter();
        this.HorizonMeter?.SanitizeMeter();
        this.KagerouMeter?.SanitizeMeter();
    }

    private static Vector2 BoundPosition(Vector2 point)
        => Vector2.Clamp(point, new Vector2(-32768), new Vector2(32768));

    private static Vector2 BoundSize(Vector2 size)
        => Vector2.Clamp(size, new Vector2(1), new Vector2(32768));

    private static float ColorPart(float value, float fallback)
        => float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : fallback;

    /// <summary>Migrate when stored meanings change. Enums serialize as integers, so preserve their order.</summary>
    public int Version { get; set; } = 7;

    // Keep the saved Bars key for existing settings and profiles.
    public MeterSettings? BarsMeter { get; set; }
    public MeterSettings? HorizonMeter { get; set; }
    public MeterSettings? KagerouMeter { get; set; }

    internal MeterSettings GetMeter(DpsMeterStyle style)
    {
        var meter = style switch
        {
            DpsMeterStyle.HorizonOverlay => this.HorizonMeter ??= this.CreateMeter(style),
            DpsMeterStyle.Kagerou => this.KagerouMeter ??= this.CreateMeter(style),
            _ => this.BarsMeter ??= this.CreateMeter(DpsMeterStyle.LMeter),
        };
        meter.DpsStyle = style;
        return meter;
    }

    internal bool HoldFinalMeter => Holds(this.BarsMeter, DpsMeterStyle.LMeter)
        || Holds(this.HorizonMeter, DpsMeterStyle.HorizonOverlay)
        || Holds(this.KagerouMeter, DpsMeterStyle.Kagerou);

    private bool Holds(MeterSettings? meter, DpsMeterStyle style)
        => meter != null ? meter.ShowDps && meter.DpsHoldLast
            : this.DpsStyle == style && this.ShowDps && this.DpsHoldLast;

    private MeterSettings CreateMeter(DpsMeterStyle style)
    {
        var meter = this.CopyMeter();
        meter.DpsStyle = style;
        meter.ShowDps = this.ShowDps && this.DpsStyle == style;
        if (this.DpsStyle != style)
        {
            var placement = MeterSettings.DefaultsFor(style);
            meter.DpsPos = placement.DpsPos;
            meter.DpsSize = placement.DpsSize;
        }
        if (style == DpsMeterStyle.LMeter) meter.UpgradeBarsAppearance();
        return meter;
    }

    public void MigrateFromV5()
    {
        foreach (var style in Enum.GetValues<DpsMeterStyle>()) this.GetMeter(style);
        this.Version = 6;
    }

    public void MigrateFromV6()
    {
        if (this.Version >= 7) return;
        this.GetMeter(DpsMeterStyle.LMeter).UpgradeBarsAppearance();
        this.Version = 7;
    }

    // Connection
    public int Port { get; set; } = 27080;

    /// <summary>Read IINACT while the program is disconnected.</summary>
    public bool StandaloneMeter { get; set; }

    public string IinactEndpoint { get; set; } = "ws://127.0.0.1:10501/ws";

    // Displayed windows
    public bool ShowTimeline { get; set; } = true;
    public bool ShowAlerts { get; set; } = true;
    public bool Locked { get; set; }

    public bool TimelineOnlyInDuty { get; set; }
    public bool TimelineOnlyInCombat { get; set; }
    public bool AlertsOnlyInDuty { get; set; }
    public bool AlertsOnlyInCombat { get; set; }
    // Store geometry in screen pixels. ImGui persistence is disabled.
    public Vector2 TimelinePos { get; set; } = new(80, 200);
    public Vector2 TimelineSize { get; set; } = new(320, 220);
    public Vector2 AlertsPos { get; set; } = new(80, 440);
    public Vector2 AlertsSize { get; set; } = new(420, 160);
    // Timeline appearance
    // Text scale also scales row heights.
    public float TimelineTextScale { get; set; } = 1.0f;

    public float TimelineBgOpacity { get; set; }

    /// <summary>Includes the backdrop, as do AlertsFade and DpsFade.</summary>
    public float TimelineFade { get; set; } = 1.0f;

    public TextEffectStyle TimelineTextEffect { get; set; } = TextEffectStyle.Outline;

    /// <summary>Outline radius or glow spread in pixels, as for alerts and DPS.</summary>
    public int TimelineEffectThickness { get; set; } = 1;

    public Vector4 TimelineEffectColor { get; set; } = new(0.0f, 0.0f, 0.0f, 0.9f);

    public Vector4 TimelineTextColor { get; set; } = new(0.95f, 0.95f, 0.98f, 1.00f);

    /// <summary>Minimum row height before text scaling.</summary>
    public float TimelineBarHeight { get; set; } = 22.0f;

    public float TimelineBarSpacing { get; set; } = 4.0f;

    public float TimelineBarRounding { get; set; } = 3.0f;

    public float TimelineBarBorderThickness { get; set; }

    public float TimelineBarTrackOpacity { get; set; } = 1.0f;

    public Vector4 TimelineBarColor { get; set; } = new(0.55f, 0.44f, 0.78f, 0.85f);

    public Vector4 TimelineBarTrackColor { get; set; } = new(0.0f, 0.0f, 0.0f, 0.60f);

    public Vector4 TimelineBarBorderColor { get; set; } = new(0.00f, 0.00f, 0.00f, 0.80f);

    // Timeline behavior
    public BarFillMode BarFill { get; set; } = BarFillMode.Deplete;

    public bool BarRightToLeft { get; set; }

    public TextAlign BarTextAlign { get; set; } = TextAlign.Left;

    public float ImminentSeconds { get; set; } = 5.0f;

    public bool ImminentPulse { get; set; } = true;

    public CountdownStyle Countdown { get; set; } = CountdownStyle.Tenths;

    public bool CountdownSplit { get; set; }

    /// <summary>Seconds ahead of the fight clock to show timeline entries.</summary>
    public float TimelineWindow { get; set; } = 45.0f;

    public int TimelineRows { get; set; } = 6;

    public bool TimelineShowClock { get; set; }

    public bool TimelineAnchorBottom { get; set; }

    public bool TimelineFireFlash { get; set; } = true;

    public bool TimelineShowTankbuster { get; set; } = true;
    public bool TimelineShowRaidwide { get; set; } = true;
    public bool TimelineShowMechanic { get; set; } = true;

    public Vector4 ColorImminent { get; set; } = new(0.90f, 0.28f, 0.28f, 0.95f);

    public bool TimelineKindColors { get; set; }

    public Vector4 TimelineTankbusterColor { get; set; } = new(0.92f, 0.48f, 0.20f, 0.85f);

    public Vector4 TimelineRaidwideColor { get; set; } = new(0.35f, 0.62f, 0.92f, 0.85f);

    public Vector4 TimelineMechanicColor { get; set; } = new(0.55f, 0.44f, 0.78f, 0.85f);

    // Alert appearance
    public float AlertsTextScale { get; set; } = 1.0f;

    public float AlertsBgOpacity { get; set; }

    public float AlertsFade { get; set; } = 1.0f;

    public TextEffectStyle AlertsTextEffect { get; set; } = TextEffectStyle.Outline;

    public int AlertsEffectThickness { get; set; } = 1;

    public Vector4 AlertsEffectColor { get; set; } = new(0.0f, 0.0f, 0.0f, 0.9f);

    /// <summary>Default info duration without a ttl. Keep this serialized name for older configs.</summary>
    public float AlertSeconds { get; set; } = 4.0f;

    public float AlertSecondsAlert { get; set; } = 4.0f;

    public float AlertSecondsAlarm { get; set; } = 6.0f;

    public int AlertsMaxVisible { get; set; } = 8;

    public AlertOrder AlertOrder { get; set; } = AlertOrder.NewestFirst;

    public TextAlign AlertsAlign { get; set; } = TextAlign.Left;

    public bool AlertsAnimate { get; set; } = true;

    public float AlertsAlarmScale { get; set; } = 1.0f;

    /// <summary>Show remaining callout time as a strip below the text.</summary>
    public bool AlertsLifeline { get; set; }

    public bool AlertsShowInfo { get; set; } = true;
    public bool AlertsShowAlert { get; set; } = true;
    public bool AlertsShowAlarm { get; set; } = true;

    public bool AlertsAnchorBottom { get; set; }

    public bool AlertsWrap { get; set; } = true;

    public bool AlertsCollapseDupes { get; set; } = true;

    public bool AlertsSeverityTint { get; set; }

    public float AlertsSeverityTintOpacity { get; set; } = 0.30f;

    public bool AlertsAlarmFlash { get; set; } = true;

    public bool AlarmScreenFlash { get; set; }

    /// <summary>Flash depth as a fraction of the shorter screen dimension.</summary>
    public float AlarmScreenFlashSize { get; set; } = 0.15f;

    public Vector4 ColorInfo { get; set; } = new(0.89f, 0.74f, 0.42f, 1.00f);
    public Vector4 ColorAlert { get; set; } = new(0.98f, 0.62f, 0.35f, 1.00f);
    public Vector4 ColorAlarm { get; set; } = new(0.95f, 0.30f, 0.30f, 1.00f);

    // Appearance profiles store JSON snapshots.
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

    public string SnapshotAppearance()
    {
        this.Sanitize();
        var node = new JsonObject();
        foreach (var property in AppearanceProperties)
        {
            node[property.Name] = JsonSerializer.SerializeToNode(property.GetValue(this), property.PropertyType, ProfileOptions);
        }
        foreach (var (name, style) in MeterProfiles)
        {
            var meter = this.GetMeter(style);
            var appearance = new JsonObject();
            foreach (var property in MeterAppearanceProperties)
                appearance[property.Name] = JsonSerializer.SerializeToNode(property.GetValue(meter), property.PropertyType, ProfileOptions);
            node[name] = appearance;
        }
        return node.ToJsonString();
    }

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

    /// <summary>Map the retired shadow effect to outline. Leave track opacity at its new default.</summary>
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

    public void MigrateFromV4()
    {
        DpsHoldLast = true;
        Version = 5;
    }

    public void ResetAppearance() => this.CopyAppearanceFrom(new Configuration());

    /// <summary>Preserve connection settings, placement, visibility and profiles.</summary>
    public void CopyAppearanceFrom(Configuration fresh)
    {
        fresh.Sanitize();
        var targets = MeterProfiles.Select(entry => this.GetMeter(entry.Style)).ToArray();
        var sources = MeterProfiles.Select(entry => fresh.GetMeter(entry.Style)).ToArray();
        foreach (var property in AppearanceProperties)
        {
            property.SetValue(this, property.GetValue(fresh));
        }
        for (var i = 0; i < targets.Length; i++)
            foreach (var property in MeterAppearanceProperties)
                property.SetValue(targets[i], property.GetValue(sources[i]));
    }

    private static readonly (string Name, DpsMeterStyle Style)[] MeterProfiles =
    {
        (nameof(BarsMeter), DpsMeterStyle.LMeter),
        (nameof(HorizonMeter), DpsMeterStyle.HorizonOverlay),
        (nameof(KagerouMeter), DpsMeterStyle.Kagerou),
    };

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
        nameof(DpsKagerouTab),
        nameof(DpsKagerouInteractive),
        nameof(DpsKagerouCompact),
        nameof(DpsKagerouIcons),
        nameof(DpsKagerouRank),
        nameof(DpsKagerouDeaths),
        nameof(DpsKagerouHealingShare),
        nameof(DpsKagerouCrit),
        nameof(DpsKagerouDirect),
        nameof(DpsKagerouCritDirect),
        nameof(DpsKagerouDeathHeading),
        nameof(DpsKagerouDeathsAlign),
        nameof(DpsKagerouShowHeadings),
        nameof(DpsKagerouNameHeading),
        nameof(DpsKagerouDpsHeading),
        nameof(DpsKagerouDamageShareHeading),
        nameof(DpsKagerouHealShareHeading),
        nameof(DpsKagerouCritHeading),
        nameof(DpsRowStripes),
        nameof(DpsRowStripeOpacity),
        nameof(DpsMaxRows),
        nameof(DpsLMeterShowDps),
        nameof(DpsLMeterHeaderTitle),
        nameof(DpsLMeterHeaderHps),
        nameof(DpsLMeterHeaderDeaths),
        nameof(DpsLMeterHeaderColor),
        nameof(DpsLMeterDurationColor),
        nameof(DpsLMeterTotalsColor),
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

    private static readonly PropertyInfo[] MeterAppearanceProperties = AppearanceProperties
        .Where(property => property.DeclaringType == typeof(MeterSettings) && property.Name != nameof(DpsStyle)).ToArray();
}

[Serializable]
internal class MeterSettings
{
    public bool ShowDps { get; set; } = true;

    public DpsMeterStyle DpsStyle { get; set; } = DpsMeterStyle.LMeter;

    public HorizonColorTheme DpsHorizTheme { get; set; } = HorizonColorTheme.ByRole;

    public bool DpsOnlyInDuty { get; set; }
    public bool DpsOnlyInCombat { get; set; }

    public Vector2 DpsPos { get; set; } = new(80, 620);
    public Vector2 DpsSize { get; set; } = new(320, 240);

    // DPS appearance
    public float DpsTextScale { get; set; } = 1.0f;

    public float DpsBgOpacity { get; set; }

    public float DpsFade { get; set; } = 1.0f;

    public TextEffectStyle DpsTextEffect { get; set; } = TextEffectStyle.Outline;

    public int DpsEffectThickness { get; set; } = 1;

    public Vector4 DpsEffectColor { get; set; } = new(0.0f, 0.0f, 0.0f, 0.9f);

    public Vector4 DpsTextColor { get; set; } = new(0.95f, 0.95f, 0.98f, 1.00f);

    public bool DpsSoloOnly { get; set; }

    public bool DpsSelfFirst { get; set; }

    public DpsSortOrder DpsSortOrder { get; set; } = DpsSortOrder.ByDps;

    public NamePrivacyStyle DpsNamePrivacy { get; set; } = NamePrivacyStyle.Shown;

    public bool DpsSelfNameYou { get; set; }

    public bool DpsRowsShowRank { get; set; } = true;

    public bool DpsRowsShowIcons { get; set; }

    public KagerouTab DpsKagerouTab { get; set; }
    public bool DpsKagerouInteractive { get; set; }
    public bool DpsKagerouCompact { get; set; }
    public bool DpsKagerouIcons { get; set; } = true;
    public bool DpsKagerouRank { get; set; }
    public bool DpsKagerouDeaths { get; set; } = true;
    public bool DpsKagerouHealingShare { get; set; } = true;
    public bool DpsKagerouCrit { get; set; } = true;
    public bool DpsKagerouDirect { get; set; }
    public bool DpsKagerouCritDirect { get; set; }
    public KagerouDeathHeading DpsKagerouDeathHeading { get; set; }
    public TextAlign DpsKagerouDeathsAlign { get; set; } = TextAlign.Center;
    public bool DpsKagerouShowHeadings { get; set; } = true;
    public bool DpsKagerouNameHeading { get; set; } = true;
    public bool DpsKagerouDpsHeading { get; set; } = true;
    public bool DpsKagerouDamageShareHeading { get; set; } = true;
    public bool DpsKagerouHealShareHeading { get; set; } = true;
    public bool DpsKagerouCritHeading { get; set; } = true;

    public bool DpsLMeterShowDps { get; set; } = true;
    public bool DpsLMeterHeaderTitle { get; set; } = true;
    public bool DpsLMeterHeaderHps { get; set; } = true;
    public bool DpsLMeterHeaderDeaths { get; set; } = true;
    public Vector4 DpsLMeterHeaderColor { get; set; } = new(30 / 255f, 30 / 255f, 30 / 255f, 230 / 255f);
    public Vector4 DpsLMeterDurationColor { get; set; } = new(0, 190 / 255f, 225 / 255f, 1);
    public Vector4 DpsLMeterTotalsColor { get; set; } = new(.5f, .5f, .5f, 1);

    public bool DpsRowStripes { get; set; }

    public float DpsRowStripeOpacity { get; set; } = 0.08f;

    public bool DpsBarTopHighlight { get; set; }

    public Vector4 DpsBarTopColor { get; set; } = new(0.98f, 0.80f, 0.25f, 0.85f);

    public bool DpsShowDeaths { get; set; }

    /// <summary>Keep final rows until damage starts on the next pull or the zone changes.</summary>
    public bool DpsHoldLast { get; set; } = true;

    /// <summary>Supports {title}, {duration} and {dps}. Empty uses the default layout.</summary>
    public string DpsHeaderFormat
    {
        get;
        set
        {
            // Imported formats use the same length limit as the text editor.
            var text = value ?? string.Empty;
            var length = Math.Min(text.Length, 128);
            if (length < text.Length && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
            {
                length--;
            }

            field = text[..length];
        }
    } = string.Empty;

    public int DpsMaxRows { get; set; } = 8;

    /// <summary>Minimum row height before text scaling.</summary>
    public float DpsBarHeight { get; set; } = 22.0f;

    public float DpsBarSpacing { get; set; } = 4.0f;

    public float DpsBarRounding { get; set; } = 3.0f;

    public float DpsBarBorderThickness { get; set; }

    public float DpsBarTrackOpacity { get; set; } = 1.0f;

    public Vector4 DpsBarColor { get; set; } = new(0.55f, 0.44f, 0.78f, 0.85f);

    public Vector4 DpsBarTrackColor { get; set; } = new(0.0f, 0.0f, 0.0f, 0.60f);

    public Vector4 DpsBarBorderColor { get; set; } = new(0.00f, 0.00f, 0.00f, 0.80f);

    public bool DpsBarRightToLeft { get; set; }

    public bool DpsBarJobColors { get; set; }

    public bool DpsBarsShowShare { get; set; }

    public bool DpsRowsShowHps { get; set; }

    public bool DpsRowsCompact { get; set; } = true;

    public bool DpsBarSelfHighlight { get; set; }

    public Vector4 DpsBarSelfColor { get; set; } = new(1.00f, 1.00f, 1.00f, 0.85f);

    // Horizon Overlay settings retain their serialized Horiz names for compatibility.

    public bool DpsHorizShowNames { get; set; } = true;

    public bool DpsHorizShowRank { get; set; } = true;

    public bool DpsHorizShowIcons { get; set; } = true;

    public bool DpsHorizShowHps { get; set; } = true;

    public bool DpsHorizHighlight { get; set; } = true;

    public bool DpsHorizShowPercent { get; set; } = true;

    /// <summary>Maximum cell width before text scaling.</summary>
    public float DpsHorizMaxBarWidth { get; set; } = 140.0f;

    /// <summary>Minimum bar height before text scaling.</summary>
    public float DpsHorizBarHeight { get; set; } = 32.0f;

    /// <summary>Skew angle in degrees.</summary>
    public float DpsHorizSkew { get; set; } = 30.0f;

    /// <summary>Icon edge length before text scaling.</summary>
    public float DpsHorizIconSize { get; set; } = 20.0f;

    public float DpsHorizCellPadding { get; set; } = 6.0f;

    /// <summary>Name and stat size relative to body text.</summary>
    public float DpsHorizStatScale { get; set; } = 0.80f;

    /// <summary>Percentage size relative to body text.</summary>
    public float DpsHorizPercentScale { get; set; } = 0.45f;

    public int DpsHorizDecimals { get; set; } = 2;

    public bool DpsHorizCompact { get; set; }

    public float DpsHorizBarOpacity { get; set; } = 0.30f;

    public Vector4 DpsHorizSelfColor { get; set; } = new(1.000f, 1.000f, 1.000f, 0.80f);

    public Vector4 DpsHorizSelfTextColor { get; set; } = new(0.000f, 0.000f, 0.000f, 1.00f);

    public Vector4 DpsHorizDpsColor { get; set; } = new(0.957f, 0.263f, 0.212f, 1.00f);

    public Vector4 DpsHorizTankColor { get; set; } = new(0.129f, 0.588f, 0.953f, 1.00f);

    public Vector4 DpsHorizHealerColor { get; set; } = new(0.545f, 0.765f, 0.290f, 1.00f);

    public Vector4 DpsHorizDimColor { get; set; } = new(0.000f, 0.000f, 0.000f, 0.30f);

    // Encounter header
    public bool DpsShowHeader { get; set; } = true;

    public bool DpsHeaderDuration { get; set; } = true;

    public bool DpsHeaderTotalDps { get; set; } = true;

    internal static MeterSettings DefaultsFor(DpsMeterStyle style)
    {
        var meter = new MeterSettings
        {
            DpsStyle = style,
            DpsPos = style == DpsMeterStyle.Kagerou ? new Vector2(440, 620)
                : style == DpsMeterStyle.HorizonOverlay ? new Vector2(80, 880) : new Vector2(80, 620),
            DpsSize = style == DpsMeterStyle.Kagerou ? new Vector2(380, 260)
                : style == DpsMeterStyle.HorizonOverlay ? new Vector2(960, 180) : new Vector2(320, 240),
        };
        if (style == DpsMeterStyle.LMeter) meter.UpgradeBarsAppearance();
        return meter;
    }

    internal void UpgradeBarsAppearance()
    {
        if (DpsBarHeight == 22) DpsBarHeight = 24;
        if (DpsBarSpacing == 4) DpsBarSpacing = 1;
        if (DpsBarRounding == 3) DpsBarRounding = 0;
        if (DpsBarColor == new Vector4(.55f, .44f, .78f, .85f)) DpsBarColor = new Vector4(.3f, .3f, .3f, 1);
        if (DpsBarTrackColor == new Vector4(0, 0, 0, .6f)) DpsBarTrackColor = Vector4.Zero;
        DpsBarJobColors = true;
        DpsRowsShowIcons = true;
        DpsRowsShowRank = false;
        DpsRowsShowHps = true;
        DpsShowDeaths = true;
        DpsRowsCompact = false;
    }

    internal MeterSettings CopyMeter()
    {
        var copy = new MeterSettings();
        foreach (var property in typeof(MeterSettings).GetProperties())
            property.SetValue(copy, property.GetValue(this));
        return copy;
    }

    internal void SanitizeMeter()
    {
        var defaults = DefaultsFor(Enum.IsDefined(this.DpsStyle) ? this.DpsStyle : DpsMeterStyle.LMeter);
        foreach (var property in typeof(MeterSettings).GetProperties())
        {
            var value = property.GetValue(this);
            if (value is float number && !float.IsFinite(number)
                || value is Vector2 point && (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
                || value is Enum choice && !Enum.IsDefined(property.PropertyType, choice))
                property.SetValue(this, property.GetValue(defaults));
            else if (value is Vector4 color)
            {
                var fallback = (Vector4)property.GetValue(defaults)!;
                property.SetValue(this, new Vector4(
                    ColorPart(color.X, fallback.X), ColorPart(color.Y, fallback.Y),
                    ColorPart(color.Z, fallback.Z), ColorPart(color.W, fallback.W)));
            }
        }
        DpsPos = BoundPosition(DpsPos);
        DpsSize = BoundSize(DpsSize);
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
        DpsEffectThickness = Math.Clamp(DpsEffectThickness, 0, 4);
        DpsMaxRows = Math.Clamp(DpsMaxRows, 1, 24);
        DpsHorizDecimals = Math.Clamp(DpsHorizDecimals, 0, 2);
    }

    private static Vector2 BoundPosition(Vector2 point)
        => Vector2.Clamp(point, new Vector2(-32768), new Vector2(32768));

    private static Vector2 BoundSize(Vector2 size)
        => Vector2.Clamp(size, new Vector2(1), new Vector2(32768));

    private static float ColorPart(float value, float fallback)
        => float.IsFinite(value) ? Math.Clamp(value, 0.0f, 1.0f) : fallback;
}
