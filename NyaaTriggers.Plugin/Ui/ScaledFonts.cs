using System;
using System.Collections.Generic;
using System.Threading;
using Dalamud.Interface.ManagedFontAtlas;

namespace NyaaTriggers.Plugin.Ui;

/// <summary>Rasterize just above the requested size to limit bitmap scaling. Disable global scaling.</summary>
internal sealed class ScaledFonts : IDisposable
{
    /// <summary>Cover 16 px text at maximum text, alarm and UI scales.</summary>
    private const float MaxRequestPx = 16.0f * 6.0f * 2.0f * 3.0f;

    /// <summary>Preserve existing size choices.</summary>
    private static readonly float[] ListedBuckets =
    {
        8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27,
        28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48,
        50, 52, 54, 56, 58, 60, 62, 64, 66, 68, 70, 72, 74, 76,
        79, 82, 85, 88, 91, 94, 97, 100, 103, 107, 111, 115, 119, 123, 127, 131, 135, 139, 143,
        150, 158, 166, 174, 183, 192, 202, 212, 222, 233,
    };

    private static readonly float[] Buckets = BuildBuckets();

    private int generation;
    internal int Generation => Volatile.Read(ref this.generation);

    private readonly IFontAtlas atlas;
    private readonly Dictionary<float, IFontHandle> handles = new();

    internal ScaledFonts()
    {
        this.atlas = Services.PluginInterface.UiBuilder.CreateFontAtlas(
            FontAtlasAutoRebuildMode.Async, false, "NyaaTriggers");
    }

    /// <summary>Check IFontHandle.Available and use scaled text until ready.</summary>
    internal IFontHandle? Get(float sizePx)
    {
        var bucket = PickBucket(sizePx);
        if (this.handles.TryGetValue(bucket, out var existing))
        {
            return existing;
        }

        try
        {
            var handle = this.atlas.NewDelegateFontHandle(
                e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(bucket)));
            handle.ImFontChanged += (_, _) => Interlocked.Increment(ref this.generation);
            this.handles[bucket] = handle;
            return handle;
        }
        catch (Exception ex)
        {
            Services.Log.Warning($"could not create a {bucket}px font: {ex.Message}");
            return null;
        }
    }

    private static float PickBucket(float sizePx)
    {
        foreach (var bucket in Buckets)
        {
            if (bucket >= sizePx)
            {
                return bucket;
            }
        }

        return Buckets[^1];
    }

    private static float[] BuildBuckets()
    {
        var buckets = new List<float>(ListedBuckets);
        var size = ListedBuckets[^1];
        while (size < MaxRequestPx)
        {
            size = MathF.Round(size * 1.05f);
            buckets.Add(size);
        }

        return buckets.ToArray();
    }

    public void Dispose()
    {
        foreach (var handle in this.handles.Values)
        {
            handle.Dispose();
        }

        this.handles.Clear();
        this.atlas.Dispose();
    }
}
