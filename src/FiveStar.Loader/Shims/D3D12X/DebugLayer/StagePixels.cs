// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Globalization;
using FiveStar.Loader.Shims.D3D12X.Resources;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// The PNG of one <c>--dump-frame</c> readback, with the per-channel minimum, maximum and mean as stored: floats
/// (R11G11B10 too) through a fixed exposure (<c>x / (1 + x)</c>, gamma 2.2) so a dark lit HDR still shows, signed data
/// (SNORM, or a float target whose values lie in -1..1, such as normals) mapped to 0..1, NaN texels magenta, depth
/// log-scaled between its nearest and farthest texel (near bright, the reverse-Z sky at 0.0 black), and integer
/// formats as their raw bytes. DXGI_FORMAT values from dxgiformat.h.
/// </summary>
internal static class StagePixels
{
    private const int Float = 0;
    private const int Snorm = 1;
    private const int Unorm = 2;
    private const int Packed = 3;

    private const int FloatRgba32 = 2;
    private const int FloatRgb32 = 6;
    private const int Rgba16Typeless = 9;
    private const int FloatRgba16 = 10;
    private const int UnormRgba16 = 11;
    private const int SnormRgba16 = 13;
    private const int FloatRg32 = 16;
    private const int FloatR11G11B10 = 26;
    private const int SnormRgba8 = 31;
    private const int FloatRg16 = 34;
    private const int UnormRg16 = 35;
    private const int SnormRg16 = 37;
    private const int FloatR32 = 41;
    private const int UnormRg8 = 49;
    private const int SnormRg8 = 51;
    private const int FloatR16 = 54;
    private const int UnormR16 = 56;
    private const int SnormR16 = 58;
    private const int SnormR8 = 63;

    /// <summary>R24G8_TYPELESS to X24_TYPELESS_G8_UINT and R16_TYPELESS, D16_UNORM: depth not stored as a float.</summary>
    private const int Depth24First = 44;
    private const int Depth24Last = 47;
    private const int Depth16Typeless = 53;
    private const int Depth16 = 55;
    private const uint Depth24Mask = 0xFFFFFF;
    private const float Depth24Max = 16777215f;

    /// <summary>The last DXGI format of each texel size: 128, 96, 64, 32, 16 and 8 bits (the BC formats follow).</summary>
    private const int Last128 = 4;
    private const int Last96 = 8;
    private const int Last64 = 22;
    private const int Last32 = 47;
    private const int Last16 = 59;
    private const int Last8 = 65;
    private const int Bytes128 = 16;
    private const int Bytes96 = 12;
    private const int Bytes64 = 8;

    private const int Rg = 2;
    private const int Rgb = 3;
    private const int Rgba = 4;
    private const int Blue = 2;
    private const int Alpha = 3;
    private const byte Opaque = 255;
    private const float Levels = 255f;
    private const float SnormMax8 = 127f;
    private const float SnormMax16 = 32767f;
    private const float UnormMax16 = 65535f;
    private const float Half = 0.5f;
    private const float Gamma = 1f / 2.2f;

    /// <summary>A float target is read as signed when no value lies outside -1..1 by more than this and one is below -this.</summary>
    private const float SignedSlack = 0.01f;
    private const float GreyMiddle = 0.5f;

    private const uint ElevenBits = 0x7FF;
    private const int GreenShift = 11;
    private const int BlueShift = 22;
    private const int ElevenMantissa = 6;
    private const int TenMantissa = 5;

    /// <summary>The exposure HDR values are multiplied by before the curve.</summary>
    public const float Exposure = 1f;

    /// <summary>The level an auto-exposed image puts the mean at, before the curve (mid grey).</summary>
    public const float AutoKey = 0.18f;

    /// <summary>
    /// Writes the RGBA8 image of a float, SNORM or 16-bit UNORM copy (or of depth, with <paramref name="depth"/>) into
    /// <paramref name="image"/> and returns its channel statistics; null for any other format (the caller converts it).
    /// <paramref name="autoExposure"/> is, for an unsigned float target, the exposure that puts its mean colour at
    /// <see cref="AutoKey"/> (<see cref="Expose"/> writes that image); 0 otherwise.
    /// </summary>
    public static string? Convert(ReadOnlySpan<byte> source, int rowPitch, int width, int rows, int format, bool depth, Span<byte> image, out float autoExposure)
    {
        autoExposure = 0;

        if (depth)
        {
            return Depth(source, rowPitch, width, rows, format, image);
        }

        var layout = LayoutOf(format);

        if (layout is null)
        {
            return null;
        }

        var stats = new ChannelStats(layout.Value.Channels);
        Measure(source, rowPitch, width, rows, layout.Value, stats);
        bool signed = layout.Value.Kind == Snorm || (layout.Value.Kind == Float && stats.Signed(SignedSlack));
        Map(source, rowPitch, width, rows, layout.Value, signed, Exposure, image);
        string mapping = $"exposure {Exposure.ToString(CultureInfo.InvariantCulture)}";

        if (signed)
        {
            mapping = "signed, mapped -1..1 to 0..1";
        }
        else if (layout.Value.Kind is Float or Packed)
        {
            autoExposure = AutoExposure(stats, layout.Value.Channels);
        }

        return $"; stage {mapping}; {stats.Describe()}";
    }

    /// <summary>The image of an unsigned float copy through <paramref name="exposure"/> (the auto-exposed PNG); false for another format.</summary>
    public static bool Expose(ReadOnlySpan<byte> source, int rowPitch, int width, int rows, int format, float exposure, Span<byte> image)
    {
        var layout = LayoutOf(format);

        if (layout is null || layout.Value.Kind is Snorm or Unorm)
        {
            return false;
        }

        Map(source, rowPitch, width, rows, layout.Value, signed: false, exposure, image);

        return true;
    }

    /// <summary><see cref="AutoKey"/> over the mean of the colour channels' means; 0 when that mean is not above 0.</summary>
    internal static float AutoExposure(ChannelStats stats, int channels)
    {
        int colour = Math.Min(channels, Rgb);
        double mean = 0;

        for (int channel = 0; channel < colour; channel++)
        {
            mean += Math.Max(stats.Mean(channel), 0) / colour;
        }

        if (!(mean > 0) || double.IsInfinity(mean))
        {
            return 0;
        }

        return (float)(AutoKey / mean);
    }

    private static void Map(ReadOnlySpan<byte> source, int rowPitch, int width, int rows, (int Channels, int Bytes, int Kind) layout, bool signed, float exposure, Span<byte> image)
    {
        for (int y = 0; y < rows; y++)
        {
            var row = source.Slice(y * rowPitch);

            for (int x = 0; x < width; x++)
            {
                Texel(row, x, layout, signed, exposure, image.Slice(((y * width) + x) * Rgba, Rgba));
            }
        }
    }

    /// <summary>The first three bytes of each texel as RGB (integer formats and any other the readers do not convert); false for a block format.</summary>
    public static bool Raw(ReadOnlySpan<byte> source, int rowPitch, int width, int rows, int format, Span<byte> image)
    {
        int bytes = TexelBytes(format);

        if (bytes == 0)
        {
            return false;
        }

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var texel = source.Slice((y * rowPitch) + (x * bytes), bytes);
                var output = image.Slice(((y * width) + x) * Rgba, Rgba);
                output.Clear();
                texel[..Math.Min(bytes, Rgb)].CopyTo(output);
                output[Alpha] = Opaque;
            }
        }

        return true;
    }

    /// <summary>Bytes a texel of <paramref name="format"/> takes; 0 for a block-compressed, video or unknown format.</summary>
    public static int TexelBytes(int format)
    {
        return format switch
        {
            <= 0 => 0,
            <= Last128 => Bytes128,
            <= Last96 => Bytes96,
            <= Last64 => Bytes64,
            <= Last32 => sizeof(uint),
            <= Last16 => sizeof(ushort),
            <= Last8 => sizeof(byte),
            _ => 0,
        };
    }

    /// <summary>The channels, bytes per channel and kind of the formats <see cref="Convert"/> reads.</summary>
    private static (int Channels, int Bytes, int Kind)? LayoutOf(int format)
    {
        return format switch
        {
            FloatRgba32 => (Rgba, sizeof(float), Float),
            FloatRgb32 => (Rgb, sizeof(float), Float),
            Rgba16Typeless or FloatRgba16 => (Rgba, sizeof(ushort), Float),
            FloatRg32 => (Rg, sizeof(float), Float),
            FloatRg16 => (Rg, sizeof(ushort), Float),
            FloatR32 => (1, sizeof(float), Float),
            FloatR16 => (1, sizeof(ushort), Float),
            FloatR11G11B10 => (Rgb, sizeof(uint), Packed),
            SnormRgba16 => (Rgba, sizeof(ushort), Snorm),
            SnormRgba8 => (Rgba, sizeof(byte), Snorm),
            SnormRg16 => (Rg, sizeof(ushort), Snorm),
            SnormRg8 => (Rg, sizeof(byte), Snorm),
            SnormR16 => (1, sizeof(ushort), Snorm),
            SnormR8 => (1, sizeof(byte), Snorm),
            UnormRgba16 => (Rgba, sizeof(ushort), Unorm),
            UnormRg16 => (Rg, sizeof(ushort), Unorm),
            UnormRg8 => (Rg, sizeof(byte), Unorm),
            UnormR16 => (1, sizeof(ushort), Unorm),
            _ => null,
        };
    }

    /// <summary>Channel <paramref name="channel"/> of texel <paramref name="x"/> of a row.</summary>
    internal static float Read(ReadOnlySpan<byte> row, int x, int channel, (int Channels, int Bytes, int Kind) layout)
    {
        if (layout.Kind == Packed)
        {
            uint texel = BinaryPrimitives.ReadUInt32LittleEndian(row[(x * sizeof(uint))..]);

            return channel switch
            {
                0 => ReadbackPixels.SmallFloat(texel & ElevenBits, ElevenMantissa),
                1 => ReadbackPixels.SmallFloat((texel >> GreenShift) & ElevenBits, ElevenMantissa),
                _ => ReadbackPixels.SmallFloat(texel >> BlueShift, TenMantissa),
            };
        }

        var at = row[(((x * layout.Channels) + channel) * layout.Bytes)..];

        return (layout.Kind, layout.Bytes) switch
        {
            (Float, sizeof(float)) => BinaryPrimitives.ReadSingleLittleEndian(at),
            (Float, _) => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(at)),
            (Snorm, sizeof(byte)) => Math.Max((sbyte)at[0] / SnormMax8, -1f),
            (Snorm, _) => Math.Max(BinaryPrimitives.ReadInt16LittleEndian(at) / SnormMax16, -1f),
            (_, sizeof(byte)) => at[0] / Levels,
            _ => BinaryPrimitives.ReadUInt16LittleEndian(at) / UnormMax16,
        };
    }

    /// <summary>The 8-bit level of one stored value: signed mapped from -1..1, a float through the exposure curve, UNORM as is.</summary>
    internal static byte Level(float value, bool signed, int kind, float exposure = Exposure)
    {
        float level = value;

        if (signed)
        {
            level = (value * Half) + Half;
        }
        else if (kind != Unorm)
        {
            float exposed = Math.Max(value, 0f) * exposure;
            level = float.IsPositiveInfinity(exposed) ? 1f : MathF.Pow(exposed / (1f + exposed), Gamma);
        }

        return (byte)(Math.Clamp(level, 0f, 1f) * Levels);
    }

    private static void Measure(ReadOnlySpan<byte> source, int rowPitch, int width, int rows, (int Channels, int Bytes, int Kind) layout, ChannelStats stats)
    {
        for (int y = 0; y < rows; y++)
        {
            var row = source.Slice(y * rowPitch);

            for (int x = 0; x < width; x++)
            {
                for (int channel = 0; channel < layout.Channels; channel++)
                {
                    stats.Add(channel, Read(row, x, channel, layout));
                }
            }
        }
    }

    /// <summary>One texel as RGBA8: one channel as grey, two as red and green, a NaN in any channel magenta.</summary>
    private static void Texel(ReadOnlySpan<byte> row, int x, (int Channels, int Bytes, int Kind) layout, bool signed, float exposure, Span<byte> output)
    {
        output.Clear();
        output[Alpha] = Opaque;

        for (int channel = 0; channel < Math.Min(layout.Channels, Rgb); channel++)
        {
            float value = Read(row, x, channel, layout);

            if (float.IsNaN(value))
            {
                output[0] = Opaque;
                output[1] = 0;
                output[Blue] = Opaque;

                return;
            }

            output[channel] = Level(value, signed, layout.Kind, exposure);
        }

        if (layout.Channels == 1)
        {
            output[1] = output[0];
            output[Blue] = output[0];
        }
    }

    /// <summary>Depth, log-scaled between the nearest and farthest texel above 0.0.</summary>
    private static string Depth(ReadOnlySpan<byte> source, int rowPitch, int width, int rows, int format, Span<byte> image)
    {
        var stats = new ChannelStats(1);

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < width; x++)
            {
                stats.Add(0, DepthValue(source.Slice(y * rowPitch), x, format));
            }
        }

        float nearest = stats.SmallestPositive;
        float farthest = stats.Largest(0);
        float span = MathF.Log(farthest / nearest);

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float value = DepthValue(source.Slice(y * rowPitch), x, format);
                var output = image.Slice(((y * width) + x) * Rgba, Rgba);
                output.Fill((byte)(Math.Clamp(DepthGrey(value, nearest, span), 0f, 1f) * Levels));
                output[Alpha] = Opaque;
            }
        }

        return $"; stage depth, log-scaled; {stats.Describe()}";
    }

    /// <summary>0..1 of a depth between <paramref name="nearest"/> and e^<paramref name="span"/> times it; 0.0 (the sky) is 0, one depth only mid-grey.</summary>
    internal static float DepthGrey(float value, float nearest, float span)
    {
        if (value <= 0 || float.IsNaN(value))
        {
            return 0f;
        }

        if (span <= 0 || float.IsNaN(span))
        {
            return GreyMiddle;
        }

        return MathF.Log(value / nearest) / span;
    }

    private static float DepthValue(ReadOnlySpan<byte> row, int x, int format)
    {
        if (format is >= Depth24First and <= Depth24Last)
        {
            return (BinaryPrimitives.ReadUInt32LittleEndian(row[(x * sizeof(uint))..]) & Depth24Mask) / Depth24Max;
        }

        if (format is Depth16Typeless or Depth16 or UnormR16)
        {
            return BinaryPrimitives.ReadUInt16LittleEndian(row[(x * sizeof(ushort))..]) / UnormMax16;
        }

        return BinaryPrimitives.ReadSingleLittleEndian(row[(x * sizeof(float))..]);
    }
}
