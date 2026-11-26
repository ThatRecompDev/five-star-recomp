// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// NaN and Inf in read-back data (<see cref="NanScan"/>): the float texture formats texel by texel, a buffer dword by
/// dword as float32. Block-compressed formats are not decoded (BC6H can hold Inf and NaN, but decoding is heavy).
/// </summary>
internal static class FloatScan
{
    // DXGI_FORMAT values (dxgiformat.h).
    private const int Rgba32Float = 2;
    private const int Rgb32Float = 6;
    private const int Rgba16Float = 10;
    private const int Rg32Float = 16;
    private const int R11G11B10Float = 26;
    private const int Rg16Float = 34;
    private const int R32Float = 41;
    private const int R16Float = 54;
    private const int FirstBlockCompressed = 70;
    private const int LastBlockCompressed = 84;
    private const int FirstNewBlockCompressed = 94;
    private const int LastNewBlockCompressed = 99;

    /// <summary>R11G11B10: 6-bit mantissas for red and green, 5 for blue, each over a 5-bit exponent.</summary>
    private const int RedBits = 11;
    private const int BlueShift = 22;
    private const int SmallMantissa = 5;
    private const int LargeMantissa = 6;
    private const uint ExponentMask = 0x1F;

    private const int Channels = 4;
    private const int ThreeChannels = 3;
    private const int TwoChannels = 2;

    /// <summary>Whether <paramref name="format"/> is block-compressed (skipped).</summary>
    public static bool BlockCompressed(int format)
    {
        return format is >= FirstBlockCompressed and <= LastBlockCompressed or >= FirstNewBlockCompressed and <= LastNewBlockCompressed;
    }

    /// <summary>The channels and bytes per channel of a float format; (0, 0) for any other.</summary>
    public static (int Channels, int Bytes) Layout(int format)
    {
        return format switch
        {
            Rgba32Float => (Channels, sizeof(float)),
            Rgb32Float => (ThreeChannels, sizeof(float)),
            Rgba16Float => (Channels, sizeof(ushort)),
            Rg32Float => (TwoChannels, sizeof(float)),
            R11G11B10Float => (ThreeChannels, 0),
            Rg16Float => (TwoChannels, sizeof(ushort)),
            R32Float => (1, sizeof(float)),
            R16Float => (1, sizeof(ushort)),
            _ => (0, 0),
        };
    }

    /// <summary>The non-finite values of a read-back texture (rows of <paramref name="rowPitch"/> bytes); null for a format that is not float.</summary>
    public static FloatScanResult? Texture(ReadOnlySpan<byte> data, int rowPitch, int width, int rows, int format)
    {
        var (channels, bytes) = Layout(format);

        if (channels == 0)
        {
            return null;
        }

        var result = default(FloatScanResult);

        for (int y = 0; y < rows; y++)
        {
            var row = data.Slice(y * rowPitch);

            for (int x = 0; x < width; x++)
            {
                for (int channel = 0; channel < channels; channel++)
                {
                    float value = Channel(row, x, channel, channels, bytes);

                    if (!float.IsFinite(value))
                    {
                        result = result.With(x, y, channel, value);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>The non-finite float32 values of a buffer's bytes (the "texel" is the dword offset).</summary>
    public static FloatScanResult Dwords(ReadOnlySpan<byte> data)
    {
        var result = default(FloatScanResult);

        for (int offset = 0; offset + sizeof(float) <= data.Length; offset += sizeof(float))
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);

            if (!float.IsFinite(value))
            {
                result = result.With(offset, 0, 0, value);
            }
        }

        return result;
    }

    /// <summary>
    /// The first <paramref name="limit"/> channel values of a read-back float texture, texel by texel (R11G11B10 only as
    /// finite 0 or not); empty for a format that is not float.
    /// </summary>
    public static List<float> Values(ReadOnlySpan<byte> data, int rowPitch, int width, int rows, int format, int limit)
    {
        var (channels, bytes) = Layout(format);
        var values = new List<float>();

        for (int y = 0; y < rows && channels != 0; y++)
        {
            var row = data.Slice(y * rowPitch);

            for (int x = 0; x < width; x++)
            {
                for (int channel = 0; channel < channels && values.Count < limit; channel++)
                {
                    values.Add(Channel(row, x, channel, channels, bytes));
                }
            }
        }

        return values;
    }

    private static float Channel(ReadOnlySpan<byte> row, int x, int channel, int channels, int bytes)
    {
        if (bytes == 0)
        {
            return Packed(BinaryPrimitives.ReadUInt32LittleEndian(row[(x * sizeof(uint))..]), channel);
        }

        int at = ((x * channels) + channel) * bytes;

        if (bytes == sizeof(ushort))
        {
            return (float)BinaryPrimitives.ReadHalfLittleEndian(row[at..]);
        }

        return BinaryPrimitives.ReadSingleLittleEndian(row[at..]);
    }

    /// <summary>An R11G11B10 channel as finite (0) or not (NaN or Inf: an all-ones exponent).</summary>
    private static float Packed(uint texel, int channel)
    {
        int shift = channel * RedBits;
        int mantissa = LargeMantissa;

        if (channel == TwoChannels)
        {
            shift = BlueShift;
            mantissa = SmallMantissa;
        }

        uint field = texel >> shift;

        if (((field >> mantissa) & ExponentMask) != ExponentMask)
        {
            return 0;
        }

        if ((field & ((1u << mantissa) - 1)) != 0)
        {
            return float.NaN;
        }

        return float.PositiveInfinity;
    }
}
