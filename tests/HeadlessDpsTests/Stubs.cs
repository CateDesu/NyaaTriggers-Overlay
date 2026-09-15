using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using NyaaTriggers.Plugin.Bridge;

namespace Dalamud.Configuration
{
    public interface IPluginConfiguration { int Version { get; set; } }
}

namespace Dalamud.Game.ClientState.Conditions
{
    public enum ConditionFlag
    {
        BetweenAreas, BetweenAreas51, OccupiedInCutSceneEvent,
        WatchingCutscene, WatchingCutscene78, BoundByDuty, InCombat
    }
}

namespace NyaaTriggers.Plugin
{
    internal static class Services
    {
        internal static readonly TestLog Log = new();
        internal static readonly TestConfigStore PluginInterface = new();
        internal static readonly TestConditions Condition = new();
    }

    internal sealed class TestLog
    {
        internal void Debug(string value) { }
        internal void Information(string value) { }
        internal void Warning(string value) => Console.WriteLine("WARNING " + value);
        internal void Error(string value) => Console.WriteLine("ERROR " + value);
    }

    internal sealed class TestConfigStore
    {
        internal void SavePluginConfig(Configuration value) { }
    }

    internal sealed class TestConditions
    {
        private readonly Dictionary<ConditionFlag, bool> values = new();
        internal bool this[ConditionFlag key]
        {
            get => values.GetValueOrDefault(key);
            set => values[key] = value;
        }
    }
}

namespace Dalamud.Interface.ManagedFontAtlas
{
    public interface IFontHandle
    {
        bool Available { get; }
        IDisposable Push();
    }
}

namespace Dalamud.Interface.Utility
{
    public static class ImGuiHelpers
    {
        public static float GlobalScale => 1;
    }
}

namespace Dalamud.Bindings.ImGui
{
    [Flags]
    public enum ImGuiWindowFlags
    {
        NoTitleBar = 1, NoResize = 2, NoMove = 4, NoScrollbar = 8,
        NoScrollWithMouse = 16, NoCollapse = 32, NoBackground = 64,
        NoSavedSettings = 128, NoInputs = 256, NoFocusOnAppearing = 512,
        NoNav = 1024, NoDocking = 2048
    }
    public enum ImGuiCond { Always, FirstUseEver }
    public enum ImGuiCol { WindowBg }
    public enum ImDrawFlags { None }
    public readonly record struct ImFontPtr(float FontSize);
    public readonly record struct ImGuiStylePtr(Vector2 WindowPadding);
    public readonly record struct DrawCommand(string Kind, string? Text, Vector2 A, Vector2 B);

    public static class ImGui
    {
        public static readonly List<DrawCommand> Commands = new();
        public static Vector2 Cursor;
        public static float FontScale = 1;
        public static void Reset() { Commands.Clear(); Cursor = Vector2.Zero; FontScale = 1; }
        public static Vector2 CalcTextSize(string text) => new(text.Length * 7 * FontScale, 16 * FontScale);
        public static float GetTextLineHeight() => 16 * FontScale;
        public static ImFontPtr GetFont() => new(16);
        public static ImGuiStylePtr GetStyle() => new(new Vector2(8));
        public static ImDrawListPtr GetWindowDrawList() => new();
        public static Vector2 GetContentRegionAvail() => new(1600, 900);
        public static Vector2 GetCursorScreenPos() => Cursor;
        public static Vector2 GetWindowPos() => Vector2.Zero;
        public static Vector2 GetWindowSize() => new(1600, 900);
        public static void Dummy(Vector2 size) => Cursor += new Vector2(0, size.Y);
        public static void SetWindowFontScale(float scale) => FontScale = scale;
        public static void SetNextWindowBgAlpha(float alpha) { }
        public static uint ColorConvertFloat4ToU32(Vector4 color) => 0xffffffff;
        public static uint GetColorU32(Vector4 color) => ColorConvertFloat4ToU32(color);
        public static uint GetColorU32(ImGuiCol color, float alpha) => 0xffffffff;
    }

    public readonly struct ImDrawListPtr
    {
        public void AddText(Vector2 position, uint color, string text)
            => ImGui.Commands.Add(new("text", text, position, position));
        public void AddRectFilled(Vector2 min, Vector2 max, uint color, float rounding = 0)
            => ImGui.Commands.Add(new("rect", null, min, max));
        public void AddRect(Vector2 min, Vector2 max, uint color, float rounding = 0,
                            ImDrawFlags flags = ImDrawFlags.None, float thickness = 1)
            => ImGui.Commands.Add(new("border", null, min, max));
        public void AddLine(Vector2 a, Vector2 b, uint color)
            => ImGui.Commands.Add(new("line", null, a, b));
        public void AddRectFilledMultiColor(Vector2 a, Vector2 b, uint c1, uint c2, uint c3, uint c4)
            => ImGui.Commands.Add(new("gradient", null, a, b));
        public void AddQuadFilled(Vector2 a, Vector2 b, Vector2 c, Vector2 d, uint color)
            => ImGui.Commands.Add(new("quad", null, a, c));
        public void AddImage(nint texture, Vector2 a, Vector2 b, Vector2 uv1, Vector2 uv2, uint color)
            => ImGui.Commands.Add(new("image", null, a, b));
        public void PushClipRect(Vector2 a, Vector2 b, bool intersect) { }
        public void PopClipRect() { }
    }
}

namespace Dalamud.Interface.Windowing
{
    public class Window(string name)
    {
        public string WindowName { get; } = name;
        public bool IsOpen { get; set; }
        public bool RespectCloseHotkey { get; set; }
        public bool ShowCloseButton { get; set; }
        public bool DisableWindowSounds { get; set; }
        public ImGuiWindowFlags Flags { get; set; }
        public Vector2? Position { get; set; }
        public Vector2? Size { get; set; }
        public ImGuiCond PositionCondition { get; set; }
        public ImGuiCond SizeCondition { get; set; }
        public virtual void PreDraw() { }
        public virtual void Draw() { }
        public void Toggle() => IsOpen = !IsOpen;
    }

    public class WindowSystem(string name)
    {
        private readonly List<Window> windows = new();
        public string Name { get; } = name;
        public void AddWindow(Window window) => windows.Add(window);
        public void RemoveAllWindows() => windows.Clear();
        public void Draw()
        {
            foreach (var window in windows.Where(window => window.IsOpen))
            {
                window.PreDraw();
                window.Draw();
            }
        }
    }
}

namespace NyaaTriggers.Plugin.Ui
{
    internal sealed class ScaledFonts : IDisposable
    {
        internal IFontHandle? Get(float size) => null;
        public void Dispose() { }
    }
    internal sealed class TestIcon { internal nint Handle => 1; }
    internal static class JobIcons
    {
        internal static TestIcon? Get(string job) => null;
    }
    internal sealed class FlashWindow : Window
    {
        internal FlashWindow(Configuration config) : base("flash") { }
        internal float AlarmAlpha { get; set; }
    }
    internal sealed class TimelineWindow : Window
    {
        internal TimelineWindow(Configuration config, BridgeHost bridge, ScaledFonts fonts) : base("timeline") { }
        internal void ResetGeometry() { }
    }
    internal sealed class AlertsWindow : Window
    {
        internal AlertsWindow(Configuration config, BridgeHost bridge, ScaledFonts fonts) : base("alerts") { }
        internal void ResetGeometry() { }
    }
    internal sealed class ConfigWindow : Window
    {
        internal ConfigWindow(Configuration config, BridgeHost bridge, PluginUi ui) : base("config") { }
    }
}
