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
        internal static TestConfigStore PluginInterface = new();
        internal static readonly TestFramework Framework = new();
        internal static readonly TestCommands Commands = new();
        internal static readonly TestChat Chat = new();
        internal static void Initialize(Dalamud.Plugin.IDalamudPluginInterface pluginInterface)
            => PluginInterface = (TestConfigStore)pluginInterface;
        internal static readonly TestConditions Condition = new();
    }

    internal sealed class TestLog
    {
        internal void Debug(string value) { }
        internal void Information(string value) { }
        internal void Warning(string value) => Console.WriteLine("WARNING " + value);
        internal void Error(string value) => Console.WriteLine("ERROR " + value);
    }

    internal sealed class TestConfigStore : Dalamud.Plugin.IDalamudPluginInterface
    {
        internal Configuration Config = new();
        public FileInfo ConfigFile => new(Path.Combine(Path.GetTempPath(), "overlay-test-config.json"));
        public TestUiBuilder UiBuilder { get; } = new();
        public object GetPluginConfig() => this.Config;
        internal void SavePluginConfig(Configuration value) { }
    }

    public sealed class TestUiBuilder
    {
        public event Action? Draw;
        public event Action? OpenConfigUi;
        public event Action? OpenMainUi;
        internal void Render() => this.Draw?.Invoke();
        internal void Open() { this.OpenConfigUi?.Invoke(); this.OpenMainUi?.Invoke(); }
        internal int DrawSubscribers => this.Draw?.GetInvocationList().Length ?? 0;
    }

    internal sealed class TestFramework : Dalamud.Plugin.Services.IFramework
    {
        public event Action<Dalamud.Plugin.Services.IFramework>? Update;
        internal void Tick() => this.Update?.Invoke(this);
        internal int Subscribers => this.Update?.GetInvocationList().Length ?? 0;
        internal Action? Capture() => this.Update is { } callback ? () => callback(this) : null;
    }

    internal sealed class TestCommands
    {
        internal bool AddHandler(string command, Dalamud.Game.Command.CommandInfo info) => true;
        internal void RemoveHandler(string command) { }
    }

    internal sealed class TestChat
    {
        internal void Print(string value) { }
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
        public static float GlobalScale { get; set; } = 1;
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
    public enum ImGuiStyleVar { ItemSpacing }
    public enum ImDrawFlags { None }
    public enum ImGuiTreeNodeFlags { DefaultOpen }
    [Flags] public enum ImGuiColorEditFlags { NoInputs = 1, AlphaBar = 2 }
    public readonly record struct ImGuiViewportPtr(Vector2 Pos, Vector2 Size);
    public readonly record struct ImFontPtr(float FontSize, float Scale = 1);
    public readonly record struct ImGuiStylePtr(Vector2 WindowPadding, Vector2 ItemSpacing);
    public readonly record struct DrawCommand(string Kind, string? Text, Vector2 A, Vector2 B);

    public static class ImGui
    {
        public static readonly List<DrawCommand> Commands = new();
        public static readonly List<(Vector2 Min, Vector2 Max)> ClipRects = new();
        public static Vector2 ItemSpacing = new(8, 4);
        private static readonly Stack<Vector2> SpacingStack = new();
        public static Vector2 Cursor;
        public static float FontScale = 1;
        public static float FontBaseSize = 16;
        public static float FontMultiplier = 1;
        public static Vector2 WindowPosition;
        public static Vector2 WindowSize = new(1600, 900);
        public static string? ThrowForText;
        public static Vector2 Available = new(1600, 900);
        public static int Disabled;
        public static readonly List<(string Label, bool Disabled)> Buttons = new();
        public static void Reset() { Commands.Clear(); ClipRects.Clear(); SpacingStack.Clear(); ItemSpacing = new(8, 4); Cursor = Vector2.Zero; FontScale = 1; FontBaseSize = 16; FontMultiplier = 1; }
        public static Vector2 CalcTextSize(string text) => new(text.Split('\n').Max(line => line.Length) * 7 * GetFontSize() / 16, text.Split('\n').Length * GetFontSize());
        public static float GetTextLineHeight() => GetFontSize();
        public static ImFontPtr GetFont() => new(FontBaseSize, FontMultiplier);
        public static ImGuiStylePtr GetStyle() => new(new Vector2(8), ItemSpacing);
        public static ImDrawListPtr GetWindowDrawList() => new();
        public static Vector2 GetContentRegionAvail() => Available;
        public static Vector2 GetCursorScreenPos() => Cursor;
        public static Vector2 GetWindowPos() => WindowPosition;
        public static Vector2 GetWindowSize() => WindowSize;
        public static void Dummy(Vector2 size) => Cursor = new(Cursor.X, MathF.Truncate(Cursor.Y + size.Y + ItemSpacing.Y));
        public static void PushStyleVar(ImGuiStyleVar style, Vector2 value) { SpacingStack.Push(ItemSpacing); ItemSpacing = value; }
        public static void PopStyleVar() => ItemSpacing = SpacingStack.Pop();
        public static void SetWindowFontScale(float scale) => FontScale = scale;
        public static void SetNextWindowBgAlpha(float alpha) { }
        public static uint ColorConvertFloat4ToU32(Vector4 color) => 0xffffffff;
        public static uint GetColorU32(Vector4 color) => ColorConvertFloat4ToU32(color);
        public static uint GetColorU32(ImGuiCol color, float alpha) => 0xffffffff;
        public static float GetFontSize() => Math.Max(1, FontBaseSize * FontMultiplier * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale) * FontScale;
        public static float GetFrameHeightWithSpacing() => 22;
        public static ImGuiViewportPtr GetMainViewport() => new(Vector2.Zero, Available);
        public static bool IsRectVisible(Vector2 a, Vector2 b) => true;
        public static bool BeginChild(string label, Vector2 size) => true;
        public static void EndChild() { }
        public static bool CollapsingHeader(string label, ImGuiTreeNodeFlags flags = default) => true;
        public static void Spacing() { }
        public static void SameLine(float offset = 0) { }
        public static void SetNextItemWidth(float width) { }
        public static void PushID(string id) { }
        public static void PushID(int id) { }
        public static void PopID() { }
        public static void BeginDisabled() => Disabled++;
        public static void EndDisabled() => Disabled--;
        public static string? ClickLabel;
        public static bool Button(string label)
        {
            Buttons.Add((label, Disabled > 0));
            if (Disabled == 0 && label == ClickLabel) { ClickLabel = null; return true; }
            return false;
        }
        public static bool Checkbox(string label, ref bool value) => false;
        public static bool InputInt(string label, ref int value) => false;
        public static bool InputText(string label, ref string value, uint size) => false;
        public static bool InputTextWithHint(string label, string hint, ref string value, uint size) => false;
        public static float? FloatInput;
        public static int? IntInput;
        public static bool SliderFloat(string label, ref float value, float min, float max, string format)
        {
            if (FloatInput is not float input) return false;
            value = input;
            FloatInput = null;
            return true;
        }
        public static bool SliderInt(string label, ref int value, int min, int max)
        {
            if (IntInput is not int input) return false;
            value = input;
            IntInput = null;
            return true;
        }
        public static bool Combo(string label, ref int value, string[] names, int count) => false;
        public static bool ColorEdit4(string label, ref Vector4 value, ImGuiColorEditFlags flags) => false;
        public static bool IsItemDeactivatedAfterEdit() => false;
        public static string Clipboard = string.Empty;
        public static string GetClipboardText() => Clipboard;
        public static void SetClipboardText(string value) => Clipboard = value;
        public static void TextDisabled(string text) { }
        public static void TextColored(Vector4 color, string text) { }
        public static void TextWrapped(string text) { }
        public static void TextUnformatted(string text) { }
    }

    public readonly struct ImDrawListPtr
    {
        public void AddText(Vector2 position, uint color, string text)
        {
            if (text == ImGui.ThrowForText) throw new ArithmeticException("Forced drawing failure");
            ImGui.Commands.Add(new("text", text, position, position + ImGui.CalcTextSize(text)));
        }
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
        {
            ImGui.Commands.Add(new("quad", null, a, c));
            ImGui.Commands.Add(new("quad", null, b, d));
        }
        public void AddImage(nint texture, Vector2 a, Vector2 b, Vector2 uv1, Vector2 uv2, uint color)
            => ImGui.Commands.Add(new("image", null, a, b));
        public void PushClipRect(Vector2 a, Vector2 b, bool intersect) => ImGui.ClipRects.Add((a, b));
        public void PopClipRect() { }
    }
}

namespace Dalamud.Interface.Windowing
{
    public sealed class WindowSizeConstraints
    {
        public Vector2 MinimumSize { get; set; }
        public Vector2 MaximumSize { get; set; }
    }
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
        public WindowSizeConstraints? SizeConstraints { get; set; }
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
        internal static Action? BeforeDraw;
        internal static Action<Window>? AfterPreDraw;
        public void Draw()
        {
            BeforeDraw?.Invoke();
            foreach (var window in windows.Where(window => window.IsOpen))
            {
                window.PreDraw();
                AfterPreDraw?.Invoke(window);
                window.Draw();
            }
        }
    }
}

namespace NyaaTriggers.Plugin.Ui
{
    internal sealed class ScaledFonts : IDisposable
    {
        internal Func<float, float?>? AvailableSize;
        internal IFontHandle? Get(float size)
            => this.AvailableSize?.Invoke(size) is float pixels ? new TestFont(pixels) : null;
        internal int Generation => 0;
        public void Dispose() { }
    }
    internal sealed class TestFont(float pixels) : IFontHandle
    {
        public bool Available => true;
        public IDisposable Push()
        {
            var restore = new RestoreFont(ImGui.FontBaseSize, ImGui.FontMultiplier);
            ImGui.FontBaseSize = pixels;
            ImGui.FontMultiplier = 1;
            return restore;
        }

        private sealed class RestoreFont(float pixels, float multiplier) : IDisposable
        {
            public void Dispose() { ImGui.FontBaseSize = pixels; ImGui.FontMultiplier = multiplier; }
        }
    }
    internal sealed class TestIcon { internal nint Handle => 1; }
    internal static class JobIcons
    {
        internal static bool Available;
        internal static TestIcon? Get(string job) => Available ? new TestIcon() : null;
    }
}

namespace Dalamud.Plugin.Services
{
    public interface IFramework { }
}

namespace Dalamud.Plugin
{
    public interface IDalamudPlugin : IDisposable { }
    public interface IDalamudPluginInterface
    {
        NyaaTriggers.Plugin.TestUiBuilder UiBuilder { get; }
        FileInfo ConfigFile { get; }
        object GetPluginConfig();
    }
}

namespace Dalamud.Game.Command
{
    public sealed class CommandInfo(Action<string, string> handler)
    {
        public Action<string, string> Handler { get; } = handler;
        public string HelpMessage { get; set; } = string.Empty;
    }
}
