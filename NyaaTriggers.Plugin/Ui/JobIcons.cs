using System.Collections.Generic;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace NyaaTriggers.Plugin.Ui;

/// <summary>Load gold job icons from ui/icon/062000. Class IDs start at 062301 and job IDs
/// are 062400 plus ClassJob.JobIndex. Request high resolution icons with the standard
/// resolution as fallback. Dalamud owns the cached textures, so these handles need no
/// disposal.</summary>
internal static class JobIcons
{
    private static readonly Dictionary<string, uint> ByJob = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["GLA"] = 062301,
        ["PGL"] = 062302,
        ["MRD"] = 062303,
        ["LNC"] = 062304,
        ["ARC"] = 062305,
        ["CNJ"] = 062306,
        ["THM"] = 062307,
        ["ACN"] = 062308,
        ["ROG"] = 062309,
        ["PLD"] = 062401,
        ["MNK"] = 062402,
        ["WAR"] = 062403,
        ["DRG"] = 062404,
        ["BRD"] = 062405,
        ["WHM"] = 062406,
        ["BLM"] = 062407,
        ["SMN"] = 062408,
        ["SCH"] = 062409,
        ["NIN"] = 062410,
        ["MCH"] = 062411,
        ["DRK"] = 062412,
        ["AST"] = 062413,
        ["SAM"] = 062414,
        ["RDM"] = 062415,
        ["BLU"] = 062416,
        ["GNB"] = 062417,
        ["DNC"] = 062418,
        ["RPR"] = 062419,
        ["SGE"] = 062420,
        ["VPR"] = 062421,
        ["PCT"] = 062422,
        ["BST"] = 062423,
    };

    private static readonly Dictionary<uint, ISharedImmediateTexture> Cache = new();

    /// <summary>Return null for unknown jobs. Textures may be unavailable while
    /// loading.</summary>
    internal static IDalamudTextureWrap? Get(string job)
    {
        if (string.IsNullOrWhiteSpace(job) || !ByJob.TryGetValue(job, out var iconId))
        {
            return null;
        }

        if (!Cache.TryGetValue(iconId, out var texture))
        {
            texture = Services.Textures.GetFromGameIcon(new GameIconLookup { IconId = iconId, HiRes = true });
            Cache[iconId] = texture;
        }

        return texture.GetWrapOrEmpty();
    }
}
