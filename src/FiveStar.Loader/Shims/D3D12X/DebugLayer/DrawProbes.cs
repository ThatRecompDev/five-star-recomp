// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Formats.Images;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Shims.D3D12X.Resources;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// Debug readback of a render target right after a draw, inside the game's own command list: the target goes from the
/// state it is in on that list to COPY_SOURCE, is copied into a readback buffer and goes back, so the copy sees exactly
/// what the draw left. <see cref="Report"/> maps the buffers once the GPU finished (the caller waited) and logs what
/// each holds. The game executes some closed lists more than once, so a buffer is released only at a later report
/// after the list it was copied on has been reset (the GPU is idle then and no execution can name it any more): released
/// when its own copy completed, a buffer that a second execution of the list still writes would be freed memory on the
/// GPU. Buffers alive, reported or not, stay under <see cref="LiveBytesCap"/>: a probe past it is skipped (in boot w20
/// the probes of --d3d12-dump-targets read back about 2.3 GB of targets every 30 s). The report reads each mapped
/// buffer row by row (<see cref="ReadbackPixels.Summarize(ReadOnlySpan{byte}, int, int, int, int, Span{byte})"/>):
/// no copy of the target on the managed heap, and a pooled image only for a PNG.
/// </summary>
internal sealed unsafe class DrawProbes
{
    /// <summary>The most bytes of readback buffers alive at once.</summary>
    public const long LiveBytesCap = 512L << 20;

    private const int Channels = 4;

    /// <summary>DXGI_FORMAT_R32G32B32A32_FLOAT, R16G16B16A16_FLOAT and R32_FLOAT.</summary>
    private const int FloatRgba32 = 2;
    private const int FloatRgba16 = 10;
    private const int Float32 = 41;
    private const int Float16 = 54;
    private const long SkipsLogged = 100;

    /// <summary>Reports a probe waits for its list's submission before it is logged as never run.</summary>
    private const int ReportsBeforeGivingUp = 3;

    /// <summary>DXGI_FORMAT_R32G8X24_TYPELESS, D32_FLOAT_S8X24_UINT, R32_FLOAT_X8X24_TYPELESS, R32_TYPELESS and D32_FLOAT.</summary>
    private const int DepthStencil32Typeless = 19;
    private const int DepthStencil32 = 20;
    private const int DepthStencil32Float = 21;
    private const int Depth32Typeless = 39;
    private const int Depth32 = 40;

    /// <summary>DXGI_FORMAT_R8_TYPELESS and R8_UINT: a stencil plane's copy.</summary>
    private const int R8Typeless = 60;
    private const int R8Uint = 62;
    private const int StencilValuesShown = 8;
    private const double Percent = 100;

    /// <summary>DXGI_FORMAT_R32_UINT, and the IEEE float fields a uint word is tested against.</summary>
    private const int R32Uint = 42;
    private const int FloatMantissaBits = 23;
    private const uint FloatExponentMask = 0xFF;
    private const uint FloatSign = 0x80000000;
    private const uint SmallestFloatExponent = 127 - 16;
    private const uint LargestFloatExponent = 127 + 16;
    private const long BytesPerMegabyte = 1L << 20;

    // Block-compressed DXGI formats (TYPELESS, UNORM, UNORM_SRGB or SNORM, UF16/SF16 of each): BC1 70-72, BC2 73-75,
    // BC3 76-78, BC4 79-81, BC5 82-84, BC6H 94-96, BC7 97-99; 8-byte blocks for BC1 and BC4, 16 for the others.
    private const int Bc1First = 70;
    private const int Bc1Last = 72;
    private const int Bc2First = 73;
    private const int Bc3Last = 78;
    private const int Bc4First = 79;
    private const int Bc4Last = 81;
    private const int Bc5Last = 84;
    private const int Bc6First = 94;
    private const int Bc7Last = 99;
    private const int HalfBlock = 8;
    private const int FullBlock = 16;
    private const int BlockSide = 4;

    // A BC1-BC3 colour block starts with two RGB565 endpoints (little-endian): red bits 11-15, green 5-10, blue 0-4.
    private const int EndpointBytes = 4;
    private const int RedShift = 11;
    private const int GreenShift = 5;
    private const int GreenMask = 0x3F;
    private const int BlueMask = 0x1F;
    private const int Red = 0;
    private const int Green = 1;
    private const int Blue = 2;

    private readonly object _gate = new();
    private readonly List<Probe> _pending = [];
    private readonly List<Probe> _reported = [];
    private readonly Dictionary<nint, long> _submissions = [];
    private readonly LoaderLog _log;
    private readonly StageReadback _stage = new();
    private long _liveBytes;
    private long _skipped;

    public DrawProbes(LoaderLog log)
    {
        _log = log;
    }

    /// <summary>Records the copy of <paramref name="target"/> (in <paramref name="state"/> on the list) into the desktop list <paramref name="list"/>.</summary>
    /// <paramref name="key"/> and <paramref name="generation"/> name the game's list and how often it was reset; with
    /// <paramref name="png"/> the report also writes the image there.
    /// <paramref name="plane"/> 1 copies a depth-stencil's stencil plane (its R8 footprint, summarized as a histogram).
    /// With <paramref name="stage"/> the report goes through <see cref="StageReadback"/> (<c>--dump-frame</c>). False when
    /// the readback buffers' cap refused the copy.
    public bool Record(nint device, nint list, nint target, int state, string label, nint key, long generation, string? png = null, int plane = 0, bool stage = false)
    {
        Marshal.AddRef(device);
        using var desktop = new ID3D12Device(device);
        Marshal.AddRef(target);
        var texture = new ID3D12Resource(target);
        var description = texture.Description;

        // A multisampled, 1D or buffer target cannot be copied to a buffer this way. A 3D one (the scattering pass's
        // distance volume) is copied whole and read as its slices one below the other.
        if ((description.Dimension != ResourceDimension.Texture2D && description.Dimension != ResourceDimension.Texture3D) || description.SampleDescription.Count > 1)
        {
            texture.Dispose();

            return true;
        }

        var footprints = new PlacedSubresourceFootPrint[1];
        var rows = new uint[1];
        var rowBytes = new ulong[1];
        int slices = description.Dimension == ResourceDimension.Texture3D ? 1 : description.DepthOrArraySize;
        uint subresource = PlaneSubresource(plane, description.MipLevels, slices);
        desktop.GetCopyableFootprints(description, subresource, 1, 0, footprints, rows, rowBytes, out ulong total);

        if (!Admit((long)total, label))
        {
            texture.Dispose();

            return false;
        }

        var buffer = desktop.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None, Vortice.Direct3D12.ResourceDescription.Buffer(total), ResourceStates.CopyDest);

        Marshal.AddRef(list);
        using var commands = new ID3D12GraphicsCommandList(list);
        var resting = (ResourceStates)state;

        commands.ResourceBarrierTransition(texture, resting, ResourceStates.CopySource);
        commands.CopyTextureRegion(new TextureCopyLocation(buffer, footprints[0]), 0, 0, 0, new TextureCopyLocation(texture, subresource), null);
        commands.ResourceBarrierTransition(texture, ResourceStates.CopySource, resting);
        texture.Dispose();

        // A plane's copy has the plane's format (a stencil plane R8), not the resource's typeless one; a 32-bit depth
        // plane is read as R32_FLOAT.
        int format = plane == 0 ? CopyFormat((int)description.Format) : (int)footprints[0].Footprint.Format;

        lock (_gate)
        {
            uint depth = description.Dimension == ResourceDimension.Texture3D ? footprints[0].Footprint.Depth : 1;
            bool depthTarget = (description.Flags & ResourceFlags.AllowDepthStencil) != 0 && plane == 0;
            // A stencil plane's copy is keyed apart from its depth plane's (resources are aligned; plane 1 is not one).
            var conversion = new Conversion(stage, depthTarget, target + plane);
            _pending.Add(new Probe(label, buffer, (long)total, footprints[0], rows[0] * depth, (int)footprints[0].Footprint.Width, format, key, generation, png, _submissions.GetValueOrDefault(key), 0, conversion));
        }

        return true;
    }

    /// <summary>
    /// The format a plane-0 copy of a resource in <paramref name="format"/> is summarized as: the 32-bit depth of
    /// R32G8X24_TYPELESS, D32_FLOAT_S8X24_UINT, R32_FLOAT_X8X24_TYPELESS, R32_TYPELESS and D32_FLOAT as R32_FLOAT (its
    /// plane 0 copies four bytes a texel: min, max, the exactly-0.0 count of a reverse-Z sky); any other as itself.
    /// </summary>
    public static int CopyFormat(int format)
    {
        return format switch
        {
            DepthStencil32Typeless or DepthStencil32 or DepthStencil32Float or Depth32Typeless or Depth32 => Float32,
            _ => format,
        };
    }

    /// <summary>The first subresource of <paramref name="plane"/>: planes follow each other after every mip of every slice (D3D12CalcSubresource).</summary>
    public static uint PlaneSubresource(int plane, int mips, int slices)
    {
        return (uint)(plane * mips * slices);
    }

    /// <summary>The game submitted its list <paramref name="key"/>: probes recorded on it before may be read once the GPU finished.</summary>
    public void Submitted(nint key)
    {
        lock (_gate)
        {
            _submissions[key] = _submissions.GetValueOrDefault(key) + 1;
        }
    }

    /// <summary>
    /// Logs what every probe whose list was submitted since it was recorded holds; the GPU has finished those lists.
    /// A probe of a list not yet submitted waits for a later report: read before its list ran, a buffer holds zeros (boot
    /// w116d read the exposure textures as all zero on two frames whose lists the game submitted after the report).
    /// Buffers of lists reset since their probe (<paramref name="generation"/> of a list key) are released. Returns
    /// whether probes still wait.
    /// </summary>
    public bool Report(Func<nint, long> generation)
    {
        lock (_gate)
        {
            ReleaseReset(generation);
            var waiting = new List<Probe>();

            foreach (var probe in _pending)
            {
                bool ran = _submissions.GetValueOrDefault(probe.List) > probe.Submissions;

                if (!ran && probe.Waits < ReportsBeforeGivingUp)
                {
                    waiting.Add(probe with { Waits = probe.Waits + 1 });

                    continue;
                }

                string summary = "not read, its list was not submitted";

                if (ran)
                {
                    summary = probe.Conversion.Stage ? StageSummary(probe) : Summary(probe);
                }

                _log.Info($"  [draws] after {probe.Label}: {summary}");
                _reported.Add(probe);
            }

            _pending.Clear();
            _pending.AddRange(waiting);

            return waiting.Count > 0;
        }
    }

    /// <summary>
    /// Releases the reported buffers whose lists were reset since their probe, before a new captured frame records its
    /// own: their copies completed before they were reported and a reset list cannot run them again, so the GPU no
    /// longer names them. Without this a frame's buffers stayed alive until the next frame's report, and the second and
    /// third world captures met the cap before their later passes (w94b skipped 101).
    /// </summary>
    public void Release(Func<nint, long> generation)
    {
        lock (_gate)
        {
            ReleaseReset(generation);
        }
    }

    private void ReleaseReset(Func<nint, long> generation)
    {
        for (int i = _reported.Count - 1; i >= 0; i--)
        {
            if (generation(_reported[i].List) != _reported[i].Generation)
            {
                _reported[i].Buffer.Dispose();
                _liveBytes -= _reported[i].Bytes;
                _reported.RemoveAt(i);
            }
        }
    }

    /// <summary>Whether a buffer of <paramref name="bytes"/> fits under <see cref="LiveBytesCap"/> beside <paramref name="live"/>.</summary>
    public static bool Fits(long live, long bytes)
    {
        return live + bytes <= LiveBytesCap;
    }

    /// <summary>Counts a new buffer in, or the skip when it does not fit.</summary>
    private bool Admit(long bytes, string label)
    {
        lock (_gate)
        {
            if (Fits(_liveBytes, bytes))
            {
                _liveBytes += bytes;

                return true;
            }

            _skipped++;

            if (_skipped % SkipsLogged == 1)
            {
                _log.Info($"  [draws] {label}: skipped, {_liveBytes / BytesPerMegabyte} MB of readback buffers alive ({_skipped} skipped so far)");
            }

            return false;
        }
    }

    /// <summary>The PNGs <c>--dump-frame</c> has written (<see cref="StageReadback"/>).</summary>
    public int StagePngs => _stage.Written;

    /// <summary>A <c>--dump-frame</c> probe's report (<see cref="StageReadback"/>), from its mapped buffer.</summary>
    private string StageSummary(Probe probe)
    {
        void* data;
        probe.Buffer.Map(0, null, &data).CheckError();

        try
        {
            var source = new ReadOnlySpan<byte>(data, checked((int)probe.Bytes));
            int pitch = (int)probe.Footprint.Footprint.RowPitch;

            return _stage.Report(source, pitch, probe.Width, (int)probe.Rows, probe.Format, probe.Conversion.Depth, probe.Conversion.Resource, probe.Png ?? "stage.png");
        }
        finally
        {
            probe.Buffer.Unmap(0);
        }
    }

    private static string Summary(Probe probe)
    {
        void* data;
        probe.Buffer.Map(0, null, &data).CheckError();
        int imageBytes = probe.Width * (int)probe.Rows * Channels;
        byte[]? image = null;

        try
        {
            if (probe.Png is not null)
            {
                image = System.Buffers.ArrayPool<byte>.Shared.Rent(imageBytes);
            }

            var source = new ReadOnlySpan<byte>(data, checked((int)probe.Bytes));
            var target = image is null ? Span<byte>.Empty : image.AsSpan(0, imageBytes);
            var summary = ReadbackPixels.Summarize(source, (int)probe.Footprint.Footprint.RowPitch, probe.Width, (int)probe.Rows, probe.Format, target);

            string floats = FloatSummary(source, (int)probe.Footprint.Footprint.RowPitch, probe.Width, (int)probe.Rows, probe.Format)
                + StencilSummary(source, (int)probe.Footprint.Footprint.RowPitch, probe.Width, (int)probe.Rows, probe.Format)
                + UintSummary(source, (int)probe.Footprint.Footprint.RowPitch, probe.Width, (int)probe.Rows, probe.Format);

            if (summary is null)
            {
                return $"format {probe.Format} not converted{floats}{BlockSummary(source, (int)probe.Footprint.Footprint.RowPitch, probe.Width, (int)probe.Rows, probe.Format)}";
            }

            summary += floats;

            if (probe.Png is not null)
            {
                PngWriter.Write(probe.Png, probe.Width, (int)probe.Rows, target);
            }

            return summary;
        }
        finally
        {
            probe.Buffer.Unmap(0);

            if (image is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(image);
            }
        }
    }

    /// <summary>
    /// For a block-compressed texture (BC1-BC7, read back in blocks: <paramref name="rows"/> block rows): how many blocks
    /// hold any non-zero byte (a texture never filled is all zero), and for BC1-BC3 the range of each RGB565 endpoint
    /// channel over the colour blocks (0-31, green 0-63), so a channel the texture never has shows as max 0. Empty for
    /// other formats.
    /// </summary>
    public static string BlockSummary(ReadOnlySpan<byte> source, int rowPitch, int width, int rows, int format)
    {
        int blockBytes = format switch
        {
            >= Bc1First and <= Bc1Last => HalfBlock,
            >= Bc4First and <= Bc4Last => HalfBlock,
            >= Bc2First and <= Bc5Last => FullBlock,
            >= Bc6First and <= Bc7Last => FullBlock,
            _ => 0,
        };

        if (blockBytes == 0 || width == 0 || rows == 0)
        {
            return string.Empty;
        }

        // The colour block: the whole BC1 block, the second half of a BC2 or BC3 block (after the alpha).
        int colourAt = -1;

        if (format is >= Bc1First and <= Bc1Last)
        {
            colourAt = 0;
        }
        else if (format is >= Bc2First and <= Bc3Last)
        {
            colourAt = HalfBlock;
        }

        int columns = (width + BlockSide - 1) / BlockSide;
        long filled = 0;
        int[] max = [0, 0, 0];
        int[] min = [int.MaxValue, int.MaxValue, int.MaxValue];

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                var block = source.Slice((y * rowPitch) + (x * blockBytes), blockBytes);

                if (block.IndexOfAnyExcept((byte)0) >= 0)
                {
                    filled++;
                }

                if (colourAt >= 0)
                {
                    Endpoints(block[colourAt..], min, max);
                }
            }
        }

        string text = $"; {filled} of {(long)columns * rows} blocks non-zero";

        if (colourAt >= 0)
        {
            text += $"; endpoint r {min[Red]}-{max[Red]}, g {min[Green]}-{max[Green]}, b {min[Blue]}-{max[Blue]}";
        }

        return text;
    }

    /// <summary>Widens <paramref name="min"/> and <paramref name="max"/> by the RGB565 channels of a colour block's two endpoints.</summary>
    private static void Endpoints(ReadOnlySpan<byte> colourBlock, int[] min, int[] max)
    {
        var endpoints = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(colourBlock[..EndpointBytes]);

        foreach (int colour in endpoints)
        {
            int[] channels = [colour >> RedShift, (colour >> GreenShift) & GreenMask, colour & BlueMask];

            for (int c = 0; c < channels.Length; c++)
            {
                max[c] = Math.Max(max[c], channels[c]);
                min[c] = Math.Min(min[c], channels[c]);
            }
        }
    }

    /// <summary>
    /// For a float target (R32G32B32A32_FLOAT, R16G16B16A16_FLOAT, R32_FLOAT, R16_FLOAT): the mean, largest and smallest
    /// value of each channel as stored, which the RGBA8 summary clamps (an HDR scene, an exposure texel, the scattering
    /// distance volume: all zero, or all at the empty-space distance); empty for other formats.
    /// </summary>
    public static string FloatSummary(ReadOnlySpan<byte> source, int rowPitch, int width, int rows, int format)
    {
        int channels = format switch
        {
            FloatRgba32 => Channels,
            FloatRgba16 => Channels,
            Float32 => 1,
            Float16 => 1,
            _ => 0,
        };

        if (channels == 0 || width == 0 || rows == 0)
        {
            return string.Empty;
        }

        int bytes = format is FloatRgba16 or Float16 ? sizeof(ushort) : sizeof(float);
        var sum = new double[channels];
        var max = new double[channels];
        var min = new double[channels];
        var zeros = new long[channels];
        var nans = new long[channels];

        Array.Fill(max, double.MinValue);
        Array.Fill(min, double.MaxValue);

        for (int y = 0; y < rows; y++)
        {
            var row = source.Slice(y * rowPitch, width * channels * bytes);

            for (int i = 0; i < width * channels; i++)
            {
                double value = bytes == sizeof(float) ? BitConverter.ToSingle(row.Slice(i * bytes, bytes)) : (double)BitConverter.ToHalf(row.Slice(i * bytes, bytes));

                // Exactly 0.0 counts both signs: a reverse-Z depth's sky is +0.0.
                if (value == 0)
                {
                    zeros[i % channels]++;
                }

                if (double.IsNaN(value))
                {
                    nans[i % channels]++;
                }

                sum[i % channels] += value;
                max[i % channels] = Math.Max(max[i % channels], value);
                min[i % channels] = Math.Min(min[i % channels], value);
            }
        }

        double count = (double)width * rows;
        string mean = string.Join(", ", sum.Select(value => (value / count).ToString("G4", System.Globalization.CultureInfo.InvariantCulture)));
        string largest = string.Join(", ", max.Select(value => value.ToString("G4", System.Globalization.CultureInfo.InvariantCulture)));
        string smallest = string.Join(", ", min.Select(value => value.ToString("G4", System.Globalization.CultureInfo.InvariantCulture)));

        return $"; float mean ({mean}), max ({largest}), min ({smallest}); exactly 0.0 ({string.Join(", ", zeros)}) and NaN ({string.Join(", ", nans)}) of {width * (long)rows} pixels";
    }

    /// <summary>
    /// For an R8_UINT or R8_TYPELESS copy (a stencil plane, the material ids TL_PS reads): how many distinct values and
    /// the commonest with their share; empty for other formats.
    /// </summary>
    public static string StencilSummary(ReadOnlySpan<byte> source, int rowPitch, int width, int rows, int format)
    {
        if ((format != R8Typeless && format != R8Uint) || width == 0 || rows == 0)
        {
            return string.Empty;
        }

        var counts = new long[byte.MaxValue + 1];

        for (int y = 0; y < rows; y++)
        {
            foreach (byte value in source.Slice(y * rowPitch, width))
            {
                counts[value]++;
            }
        }

        double total = (double)width * rows;
        var common = Enumerable.Range(0, counts.Length).Where(value => counts[value] > 0).OrderByDescending(value => counts[value]).ToList();
        string top = string.Join(", ", common.Take(StencilValuesShown).Select(value => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"0x{value:x2} {counts[value] * Percent / total:F1}%")));

        return $"; stencil {common.Count} values: {top}";
    }

    /// <summary>
    /// For an R32_UINT copy (TL_PS's packed light, which PS_SSSResolve decodes as three 5e5m fields): the share of zero
    /// words, of words that read as ordinary floats (sign clear or set, exponent 2^-16 to 2^16: a float written into the
    /// uint target, which decodes to a large R with G = B = 0), of those with the sign set, and the commonest words.
    /// </summary>
    public static string UintSummary(ReadOnlySpan<byte> source, int rowPitch, int width, int rows, int format)
    {
        if (format != R32Uint || width == 0 || rows == 0)
        {
            return string.Empty;
        }

        long zero = 0;
        long floatLike = 0;
        long negative = 0;
        var counts = new Dictionary<uint, long>();

        for (int y = 0; y < rows; y++)
        {
            var row = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(source.Slice(y * rowPitch, width * sizeof(uint)));

            foreach (uint word in row)
            {
                uint exponent = (word >> FloatMantissaBits) & FloatExponentMask;
                counts[word] = counts.GetValueOrDefault(word) + 1;

                if (word == 0)
                {
                    zero++;

                    continue;
                }

                if (exponent >= SmallestFloatExponent && exponent <= LargestFloatExponent)
                {
                    floatLike++;
                }

                if ((word & FloatSign) != 0)
                {
                    negative++;
                }
            }
        }

        double total = (double)width * rows;
        string top = string.Join(", ", counts.OrderByDescending(pair => pair.Value).Take(StencilValuesShown).Select(pair => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"0x{pair.Key:x8} {pair.Value * Percent / total:F1}%")));

        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"; uint zero {zero * Percent / total:F1}%, float-like {floatLike * Percent / total:F1}%, sign set {negative * Percent / total:F1}%; commonest {top}");
    }

    /// <summary>One recorded copy: its buffer and size, the copied footprint, the game's list and generation it was copied on, the list's submissions then, and the reports it waited.</summary>
    private sealed record Probe(string Label, ID3D12Resource Buffer, long Bytes, PlacedSubresourceFootPrint Footprint, uint Rows, int Width, int Format, nint List, long Generation, string? Png, long Submissions, int Waits, Conversion Conversion);

    /// <summary>Whether a probe is reported by <see cref="StageReadback"/> (<c>--dump-frame</c>), whether the copy is depth, and its resource.</summary>
    private readonly record struct Conversion(bool Stage, bool Depth, nint Resource);
}
