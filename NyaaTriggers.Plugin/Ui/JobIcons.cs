using System.Collections.Generic;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace NyaaTriggers.Plugin.Ui;

/// <summary>Gold class icons start at 062301. Jobs use 062400 + ClassJob.JobIndex.
/// Dalamud owns the cached textures.</summary>
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

    private static readonly Dictionary<string, uint> ClassJobs = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["GLA"] = 1, ["PGL"] = 2, ["MRD"] = 3, ["LNC"] = 4, ["ARC"] = 5, ["CNJ"] = 6, ["THM"] = 7,
        ["PLD"] = 19, ["MNK"] = 20, ["WAR"] = 21, ["DRG"] = 22, ["BRD"] = 23, ["WHM"] = 24,
        ["BLM"] = 25, ["ACN"] = 26, ["SMN"] = 27, ["SCH"] = 28, ["ROG"] = 29, ["NIN"] = 30,
        ["MCH"] = 31, ["DRK"] = 32, ["AST"] = 33, ["SAM"] = 34, ["RDM"] = 35, ["BLU"] = 36,
        ["GNB"] = 37, ["DNC"] = 38, ["RPR"] = 39, ["SGE"] = 40, ["VPR"] = 41, ["PCT"] = 42,
        ["BST"] = 43,
    };

    /// <summary>Textures may be unavailable while loading.</summary>
    internal static IDalamudTextureWrap? Get(string job, bool lmeter = false)
    {
        if (string.IsNullOrWhiteSpace(job) || !ByJob.TryGetValue(job, out var iconId))
        {
            return null;
        }

        if (lmeter && ClassJobs.TryGetValue(job, out var classJob)) iconId = 62000 + classJob;

        if (!Cache.TryGetValue(iconId, out var texture))
        {
            texture = Services.Textures.GetFromGameIcon(new GameIconLookup { IconId = iconId, HiRes = true });
            Cache[iconId] = texture;
        }

        return texture.GetWrapOrEmpty();
    }
}
