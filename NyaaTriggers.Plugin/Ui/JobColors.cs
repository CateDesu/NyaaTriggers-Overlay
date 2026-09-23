using System;
using System.Collections.Generic;
using System.Numerics;

namespace NyaaTriggers.Plugin.Ui;

internal enum JobRole
{
    Dps,
    Tank,
    Healer,
}

/// <summary>Job accent colours based on cactbot.</summary>
internal static class JobColors
{
    private static readonly Vector4 Unknown = Hex(0x9A9A9A);

    private static readonly HashSet<string> Tanks = new(StringComparer.OrdinalIgnoreCase)
    {
        "GLA", "MRD", "PLD", "WAR", "DRK", "GNB",
    };

    private static readonly HashSet<string> Healers = new(StringComparer.OrdinalIgnoreCase)
    {
        "CNJ", "WHM", "SCH", "AST", "SGE",
    };

    private static readonly HashSet<string> DpsJobs = new(StringComparer.OrdinalIgnoreCase)
    {
        "PGL", "LNC", "ARC", "THM", "MNK", "DRG", "BRD", "BLM", "ACN", "SMN",
        "ROG", "NIN", "MCH", "SAM", "RDM", "BLU", "RPR", "DNC", "VPR", "PCT",
        "BST",
    };

    internal static JobRole? RoleOf(string job)
    {
        if (string.IsNullOrWhiteSpace(job))
        {
            return null;
        }

        if (Tanks.Contains(job))
        {
            return JobRole.Tank;
        }

        if (Healers.Contains(job))
        {
            return JobRole.Healer;
        }

        return DpsJobs.Contains(job) ? JobRole.Dps : null;
    }

    private static readonly Dictionary<string, Vector4> ByJob = new(StringComparer.OrdinalIgnoreCase)
    {
        // Base classes share their associated job colours.
        ["GLA"] = Hex(0xA8D2E6),
        ["PGL"] = Hex(0xD69C00),
        ["MRD"] = Hex(0xCF2621),
        ["LNC"] = Hex(0x4164CD),
        ["ARC"] = Hex(0x91BA5E),
        ["CNJ"] = Hex(0xFFF0DC),
        ["THM"] = Hex(0xA579D6),
        ["ACN"] = Hex(0x2D9B78),
        ["ROG"] = Hex(0xAF1964),
        ["PLD"] = Hex(0xA8D2E6),
        ["WAR"] = Hex(0xCF2621),
        ["DRK"] = Hex(0xD126CC),
        ["GNB"] = Hex(0x796D30),
        ["WHM"] = Hex(0xFFF0DC),
        ["SCH"] = Hex(0x8657FF),
        ["AST"] = Hex(0xFFE74A),
        ["SGE"] = Hex(0x80A0F0),
        ["MNK"] = Hex(0xD69C00),
        ["DRG"] = Hex(0x4164CD),
        ["NIN"] = Hex(0xAF1964),
        ["SAM"] = Hex(0xE46D04),
        ["RPR"] = Hex(0x965A90),
        ["VPR"] = Hex(0x778220),
        ["BRD"] = Hex(0x91BA5E),
        ["MCH"] = Hex(0x6EE1D6),
        ["DNC"] = Hex(0xE2B0AF),
        ["BLM"] = Hex(0xA579D6),
        ["SMN"] = Hex(0x2D9B78),
        ["RDM"] = Hex(0xE87B7B),
        ["BLU"] = Hex(0x2459FF),
        ["PCT"] = Hex(0xFCA8E0),
        // Custom Beastmaster colour.
        ["BST"] = Hex(0xA65E2E),
    };

    internal static Vector4 Get(string job)
        => !string.IsNullOrWhiteSpace(job) && ByJob.TryGetValue(job, out var color)
            ? color
            : Unknown;

    private static Vector4 Hex(int rgb)
        => new(
            ((rgb >> 16) & 0xFF) / 255.0f,
            ((rgb >> 8) & 0xFF) / 255.0f,
            (rgb & 0xFF) / 255.0f,
            1.0f);
}
