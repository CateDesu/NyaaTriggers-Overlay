using System;
using System.Collections.Generic;
using Dalamud.Interface.ManagedFontAtlas;

namespace NyaaTriggers.Plugin.Ui;

/// <summary>
/// Crisp text at any size.
///
/// Scaling the default font with SetWindowFontScale stretches a bitmap and
/// blurs fast as the scale grows. Instead the requested size is snapped up to
/// the next bucket and a font rasterized at exactly that size is built in a
/// private atlas; the caller scales the few-percent remainder the old way,
/// which is invisible. Buckets are built lazily and kept, so each size costs
/// one atlas rebuild the first time it is asked for and is free after that.
///
/// The atlas is private and not global-scaled: a built font's pixel size is
/// exactly the requested one, no matter what Dalamud's UI scale is doing.
/// </summary>
internal sealed class ScaledFonts : IDisposable
{
    /// <summary>Loudest request the ladder must cover: 16 px body text at
    /// the 6x text scale with the 2x alarm scale and a 3x UI scale.</summary>
    private const float MaxRequestPx = 16.0f * 6.0f * 2.0f * 3.0f;

    /// <summary>The written-out start of the ladder, kept as the seed the
    /// generated steps grow from so existing sizes snap exactly as before.</summary>
    private static readonly float[] ListedBuckets =
    {
        8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27,
        28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48,
        50, 52, 54, 56, 58, 60, 62, 64, 66, 68, 70, 72, 74, 76,
        79, 82, 85, 88, 91, 94, 97, 100, 103, 107, 111, 115, 119, 123, 127, 131, 135, 139, 143,
        150, 158, 166, 174, 183, 192, 202, 212, 222, 233,
    };

    /// <summary>1 px apart where scaled-up text actually lands, widening to
    /// about 4-5% a step at the top, so the residual bitmap scaling between a
    /// bucket and the requested size stays under about 5%. (At the smallest
    /// sizes a 1 px step is proportionally larger, but a 1 px miss on an 8 px
    /// font is invisible.) The written-out list stops at 233; from there the
    /// same step is generated up to <see cref="MaxRequestPx"/>, so the loudest
    /// alarm text gets a real bucket instead of stretching the top one.
    /// Shared by every overlay window, so the dps meter,
    /// the timeline and the alerts all sharpen from the same list.</summary>
    private static readonly float[] Buckets = BuildBuckets();

    private readonly IFontAtlas atlas;
    private readonly Dictionary<float, IFontHandle> handles = new();

    internal ScaledFonts()
    {
        this.atlas = Services.PluginInterface.UiBuilder.CreateFontAtlas(
            FontAtlasAutoRebuildMode.Async, false, "NyaaTriggers");
    }

    /// <summary>The font rasterized nearest above the requested size. It may
    /// still be building; check <see cref="IFontHandle.Available"/> and fall
    /// back to plain scaling until it is ready.</summary>
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

    /// <summary>The listed sizes, then the same roughly 5% step generated
    /// onward until the loudest reachable request fits under the top rung.</summary>
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
