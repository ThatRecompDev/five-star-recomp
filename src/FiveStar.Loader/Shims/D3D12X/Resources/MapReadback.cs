// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

// Facts after Leonida (GPL-3.0; runtime/shims/d3d12_vulkan_device.cpp, read_back_on_map): which targets the title maps and why.

using System.Diagnostics;
using System.Globalization;
using FiveStar.Loader.Shims.D3D12X.Present;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// The game reads its exposure statistics on the CPU: it maps a 1x1 and a 5x1 RGBA32F render target it placed and reads
/// their memory (Leonida's finding). The GPU renders into our desktop texture, never into that memory, so the read saw
/// zeros or a stale value and the exposure went several stops off, differently from run to run. At such a Map the GPU
/// finishes everything submitted, the texels of the texture's one row are read back and written to the game's memory
/// at offset 0 (a single row is linear there). Only a GPU-written 2D texture of one row, one mip and one layer, at most
/// <see cref="MaxRowBytes"/>; on by default, off with <c>--no-map-readback</c>.
/// </summary>
internal static unsafe class MapReadback
{
    /// <summary>The longest row read back: 256 RGBA32F texels.</summary>
    public const int MaxRowBytes = 4096;

    /// <summary>D3D12_RESOURCE_DIMENSION_TEXTURE2D.</summary>
    private const int Texture2D = 3;

    /// <summary>The widest row of 16-byte texels under <see cref="MaxRowBytes"/>.</summary>
    private const ulong MaxWidth = MaxRowBytes / 16;

    private static readonly TextureReadback Readback = new();
    private static readonly AsyncMapReadback Async = new();
    private static long _filled;
    private static long _failed;
    private static long _served;
    private static long _waitTicks;

    /// <summary>Whether maps of small GPU-written targets read the GPU's texels back (off with <c>--no-map-readback</c>).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// <c>--map-readback-wait</c>: every Map waits for the GPU to finish all submitted work and reads the row back then,
    /// as before <see cref="AsyncMapReadback"/>. Off by default.
    /// </summary>
    public static bool Wait { get; set; }

    /// <summary>
    /// Maps that wrote the GPU's texels into the game's memory after a wait, those that could not, those served by a copy
    /// an earlier Map started, and the game's time waiting.
    /// </summary>
    public static string Summary
    {
        get
        {
            var (started, busy) = Async.Counts;
            double waited = Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _waitTicks)).TotalMilliseconds;

            return string.Create(CultureInfo.InvariantCulture, $"{Interlocked.Read(ref _filled)} maps of GPU-written targets read back after a wait for the GPU ({Interlocked.Read(ref _failed)} failed, {waited:F0} ms waited), {Interlocked.Read(ref _served)} from a copy an earlier map started ({started} copies started, {busy} maps started none: all lists in flight)");
        }
    }

    /// <summary>Whether a texture qualifies: GPU-written, 2D, one row, one mip, one layer, a narrow row.</summary>
    public static bool Takes(GpuTexture texture)
    {
        var console = texture.Console;

        return texture.GpuWritten && console.Dimension == Texture2D && console.Height == 1 && console.DepthOrArraySize == 1 && console.MipLevels <= 1 && console.Width <= MaxWidth;
    }

    /// <summary>
    /// A Map of the game's texture at <paramref name="address"/> whose memory is <paramref name="game"/>: when the texture
    /// qualifies, the GPU finishes all submitted work (<paramref name="throttle"/>) and its row lands in the memory.
    /// </summary>
    public static void OnMap(nint device, GpuTextures textures, GpuFrameThrottle throttle, ulong address, byte* game)
    {
        if (!Enabled)
        {
            return;
        }

        var texture = textures.Registry.Find(address);

        if (texture is null || texture.Address != address || !Takes(texture))
        {
            return;
        }

        if (Wait)
        {
            WaitAndFill(device, texture, throttle, game);
            Trace(texture, game, "read after a wait");

            return;
        }

        // A texture's first Map waits as before, so the game never reads a row of nothing; later ones read the copy an
        // earlier Map started (AsyncMapReadback).
        if (Async.TryServe(texture.Desktop, game))
        {
            Interlocked.Increment(ref _served);
            Trace(texture, game, "an earlier map's copy");
        }
        else
        {
            WaitAndFill(device, texture, throttle, game);
            Trace(texture, game, "read after a wait");
        }

        Async.Start(device, texture.Desktop, texture.State, throttle.Submitted());
    }

    /// <summary><c>--trace-exposure</c>: what the game reads from the target (<see cref="DebugLayer.ExposureTrace"/>).</summary>
    private static void Trace(GpuTexture texture, byte* game, string source)
    {
        if (DebugLayer.ExposureTrace.Enabled)
        {
            DebugLayer.ExposureTrace.Mapped(texture, game, source);
        }
    }

    /// <summary>The GPU finishes all submitted work (the game's thread waits), then the row is read back into <paramref name="game"/>.</summary>
    private static void WaitAndFill(nint device, GpuTexture texture, GpuFrameThrottle throttle, byte* game)
    {
        long start = Stopwatch.GetTimestamp();

        foreach (var (fence, value) in throttle.Submitted())
        {
            throttle.WaitFor(fence, value);
        }

        Count(Fill(device, texture.Desktop, texture.State, game));
        Interlocked.Add(ref _waitTicks, Stopwatch.GetTimestamp() - start);
    }

    /// <summary>Reads the row of <paramref name="desktop"/> (resting in <paramref name="state"/>) into <paramref name="destination"/>; whether it did.</summary>
    public static bool Fill(nint device, nint desktop, int state, byte* destination)
    {
        var image = Readback.Read(device, desktop, state);

        if (image is null || image.RowBytes > MaxRowBytes)
        {
            return false;
        }

        image.Pixels.AsSpan(0, image.RowBytes).CopyTo(new Span<byte>(destination, image.RowBytes));

        return true;
    }

    private static void Count(bool filled)
    {
        if (filled)
        {
            Interlocked.Increment(ref _filled);

            return;
        }

        Interlocked.Increment(ref _failed);
    }
}
