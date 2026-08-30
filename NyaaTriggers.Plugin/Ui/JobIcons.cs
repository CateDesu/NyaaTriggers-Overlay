using System.Collections.Generic;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace NyaaTriggers.Plugin.Ui;

/// <summary>
/// The metallic gold job icons, loaded from the game's own icon pack
/// (ui/icon/062000). The dps meter's Horizon Overlay style centres one on
/// each bar like the ACT original; nothing else uses them. The wire format carries
/// the acronym, so the map is acronym to icon id: classes are 062301 onward,
/// jobs are 062400 + the ClassJob sheet's JobIndex (062401 PLD through 062423
/// BST, verified against the game files). Hi-res variants exist for the whole
/// set, so the request asks for them; Dalamud falls back to the standard one
/// when an icon lacks it. Unknown or blank jobs get no icon, like the
/// original's empty.png. The textures stay owned by Dalamud's shared cache
/// (ISharedImmediateTexture is not disposable on this API), so the cache
/// below holds managed handles only and there is nothing to release at
/// teardown.
/// </summary>
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

    /// <summary>The job's gold icon texture, or null for an acronym we do not
    /// know. Textures load in the background; the wrap may be the empty one
    /// for a frame or two, which draws as nothing and is fine.</summary>
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
