// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using FiveStar.Loader.Shims.D3D12X.Resources;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--trace-exposure &lt;n&gt;</c>, for the white picture after the opening cutscene: every n presents each one-row
/// target the game maps (<see cref="MapReadback"/>, its exposure statistics) logs the floats the game then reads, and
/// the exposure and tonemap pipelines (<see cref="Pipelines"/>) get <see cref="NanScan"/> with values shown: small float
/// textures texel by texel, 8 floats of every buffer and CBV, and their null and released views named. Off by default.
/// </summary>
internal static unsafe class ExposureTrace
{
    /// <summary>Name parts of the exposure, average-luminance and tonemap pipelines (FSSM names of build 1.0.2.1).</summary>
    public const string Pipelines = "Exposure,avgLuminanceCompute,Tonemap";

    /// <summary>The floats shown of a mapped target: five RGBA32F texels.</summary>
    private const int ShownFloats = 20;

    /// <summary>Floats shown of each scanned buffer and CBV when <c>--d3d12-nan-scan-dwords</c> is not given.</summary>
    public static readonly (int Offset, int Count) ScanDwords = (0, 8);

    private static readonly ConcurrentDictionary<ulong, long> Logged = new();

    /// <summary>Presents between two lines of one target; 0 when off.</summary>
    public static int Interval { get; set; }

    public static bool Enabled => Interval > 0;

    /// <summary>The game's Map of <paramref name="texture"/> left <paramref name="game"/> as it will read it: logged once per interval.</summary>
    public static void Mapped(GpuTexture texture, byte* game, string source)
    {
        long presents = ShimContext.Runtime.D3D12X.Presenter.Presents;
        long last = Logged.GetOrAdd(texture.Address, long.MinValue);

        if (last != long.MinValue && presents - last < Interval)
        {
            return;
        }

        Logged[texture.Address] = presents;
        var console = texture.Console;
        int bytes = (int)Math.Min(texture.Size, ShownFloats * sizeof(float));
        string floats = Floats(new ReadOnlySpan<byte>(game, bytes));

        ShimContext.Runtime.Log.Info(string.Create(CultureInfo.InvariantCulture, $"  [exposure] present {presents}: map of 0x{texture.Address:x} ({console.Width}x{console.Height} format {console.Format}, {source}): {floats}"));
    }

    /// <summary>The bytes as float32 values ("nan" and "inf" show as such).</summary>
    public static string Floats(ReadOnlySpan<byte> bytes)
    {
        var shown = new List<string>();

        for (int at = 0; at + sizeof(float) <= bytes.Length; at += sizeof(float))
        {
            shown.Add(BitConverter.ToSingle(bytes.Slice(at, sizeof(float))).ToString("G6", CultureInfo.InvariantCulture));
        }

        return string.Join(", ", shown);
    }
}
