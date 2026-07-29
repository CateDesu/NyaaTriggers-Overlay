using System;
using System.Numerics;
using Dalamud.Configuration;

namespace NyaaTriggers.Plugin;

[Serializable]
internal sealed class Configuration : IPluginConfiguration
{
    /// <summary>Bumped only when a stored field changes meaning, so old configs
    /// can be migrated rather than silently reinterpreted.</summary>
    public int Version { get; set; } = 1;

    // ── link ──────────────────────────────────────────────────────────────
    /// <summary>Loopback port the desktop app connects to. Not exposed off the
    /// machine: the listener binds 127.0.0.1 and ::1 only.</summary>
    public int Port { get; set; } = 27080;

    /// <summary>Serve the link at all. Off means the plugin is inert.</summary>
    public bool BridgeEnabled { get; set; } = true;

    // ── what to draw ──────────────────────────────────────────────────────
    public bool ShowTimeline { get; set; } = true;
    public bool ShowAlerts { get; set; } = true;

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

    // ── appearance ────────────────────────────────────────────────────────
    public float TextScale { get; set; } = 1.0f;

    /// <summary>Backdrop alpha behind each box's content: 0 = invisible (the
    /// raid-night default; the boxes float bare text/bars over the game), up
    /// to 1 = solid theme background. Applies to the locked boxes via a custom
    /// rect and to unlocked ones via the window bg alpha.</summary>
    public float BgOpacity { get; set; } = 0.0f;

    /// <summary>Timeline bar row height before the text-scale multiplier.</summary>
    public float BarHeight { get; set; } = 22.0f;

    /// <summary>Seconds ahead of the fight clock a timeline entry becomes a bar.</summary>
    public float TimelineWindow { get; set; } = 45.0f;

    /// <summary>How many bars at most, so a dense timeline cannot grow the box
    /// past its configured height.</summary>
    public int TimelineRows { get; set; } = 6;

    /// <summary>Seconds an alert stays up when the app does not specify one.</summary>
    public float AlertSeconds { get; set; } = 4.0f;

    public Vector4 ColorBar { get; set; } = new(0.55f, 0.44f, 0.78f, 0.85f);
    public Vector4 ColorBarText { get; set; } = new(0.95f, 0.95f, 0.98f, 1.00f);
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

    /// <summary>Restore the shipped palette and sizing, leaving the link
    /// settings and window placement alone.</summary>
    public void ResetAppearance()
    {
        var fresh = new Configuration();
        TextScale = fresh.TextScale;
        BgOpacity = fresh.BgOpacity;
        BarHeight = fresh.BarHeight;
        TimelineWindow = fresh.TimelineWindow;
        TimelineRows = fresh.TimelineRows;
        AlertSeconds = fresh.AlertSeconds;
        ColorBar = fresh.ColorBar;
        ColorBarText = fresh.ColorBarText;
        ColorImminent = fresh.ColorImminent;
        ColorInfo = fresh.ColorInfo;
        ColorAlert = fresh.ColorAlert;
        ColorAlarm = fresh.ColorAlarm;
    }
}
