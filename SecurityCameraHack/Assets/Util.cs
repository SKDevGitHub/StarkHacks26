using System;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

static class TextureReadback
{
    // Texture readback as Span with AsyncGPUReadback in a synced fashion
    public unsafe static ReadOnlySpan<Color32> AsSpan(this Texture source)
    {
        var req = AsyncGPUReadback.Request(source);

        req.WaitForCompletion();
        if (req.hasError) return ReadOnlySpan<Color32>.Empty;

        var data = req.GetData<Color32>(0);

        var ptr = NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(data);
        return new Span<Color32>(ptr, data.Length);
    }
}

static class NativeArrayReadOnlySpan
{
    public unsafe static ReadOnlySpan<Color32> AsReadOnlySpan(this NativeArray<Color32> source, int length)
    {
        if (!source.IsCreated || length <= 0)
            return ReadOnlySpan<Color32>.Empty;

        length = Math.Min(length, source.Length);
        var ptr = NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(source);
        return new ReadOnlySpan<Color32>(ptr, length);
    }
}
