namespace Dalamud.Configuration
{
    public interface IPluginConfiguration { int Version { get; set; } }
}

namespace NyaaTriggers.Plugin
{
    internal static class Services
    {
        internal static readonly TestLog Log = new();
        internal static readonly TestConfigStore PluginInterface = new();
    }

    internal sealed class TestLog
    {
        internal int Warnings;
        internal void Debug(string value) { }
        internal void Information(string value) { }
        internal void Warning(string value) => Interlocked.Increment(ref this.Warnings);
        internal void Error(string value) { }
    }

    internal sealed class TestConfigStore
    {
        internal void SavePluginConfig(Configuration value) { }
    }
}
