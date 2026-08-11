using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace NyaaTriggers.Plugin;

/// <summary>
/// Dalamud services, injected once at load. Deliberately a short list: this
/// plugin draws what the desktop app tells it to draw and reads nothing about
/// the game beyond whether the UI should be visible at all.
/// </summary>
internal sealed class Services
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static ITextureProvider Textures { get; private set; } = null!;

    internal static void Initialize(IDalamudPluginInterface pluginInterface)
        => pluginInterface.Create<Services>();
}
