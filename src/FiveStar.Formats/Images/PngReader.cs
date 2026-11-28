// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;

namespace FiveStar.Formats.Images;

/// <summary>
/// Reads 8-bit, non-interlaced PNG files (grey, grey and alpha, RGB, RGBA; what <see cref="PngWriter"/> and most tools
/// write) into RGBA: the IDAT chunks inflated and every row unfiltered (none, sub, up, average, Paeth). CRCs are not checked.
/// </summary>
public static class PngReader
{
    private const int SignatureBytes = 8;
    private const int LengthBytes = 4;
    private const int TypeBytes = 4;
    private const int CrcBytes = 4;
    private const int HeightField = 4;
    private const int DepthField = 8;
    private const int ColourField = 9;
    private const int InterlaceField = 12;
    private const byte BitDepth = 8;
    private const int OutputChannels = 4;
    private const int GreenChannel = 1;
    private const int BlueChannel = 2;
    private const int Alpha = 3;
    private const int Halves = 2;
    private const byte Opaque = 255;

    private const byte Grey = 0;
    private const byte Rgb = 2;
    private const byte GreyAlpha = 4;
    private const byte Rgba = 6;
    private const int RgbChannels = 3;
    private const int GreyAlphaChannels = 2;

    private const byte FilterSub = 1;
    private const byte FilterUp = 2;
    private const byte FilterAverage = 3;
    private const byte FilterPaeth = 4;

    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static RgbaImage Read(string path)
    {
        return Read(File.ReadAllBytes(path));
    }

    public static RgbaImage Read(ReadOnlySpan<byte> png)
    {
        if (png.Length < SignatureBytes || !png[..SignatureBytes].SequenceEqual(Signature))
        {
            throw new InvalidDataException("not a PNG file");
        }

        var (header, compressed) = Chunks(png[SignatureBytes..]);
        int width = BinaryPrimitives.ReadInt32BigEndian(header);
        int height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(HeightField));
        int channels = Channels(header[ColourField]);

        if (header[DepthField] != BitDepth || header[InterlaceField] != 0)
        {
            throw new NotSupportedException($"PNG: bit depth {header[DepthField]}, interlace {header[InterlaceField]}; only 8-bit non-interlaced is read");
        }

        int rowBytes = width * channels;
        var raw = Inflate(compressed, (rowBytes + 1) * height);
        Unfilter(raw, rowBytes, height, channels);

        return new RgbaImage(width, height, ToRgba(raw, width, height, channels));
    }

    /// <summary>The IHDR data and the IDAT data joined, up to IEND.</summary>
    private static (byte[] Header, byte[] Data) Chunks(ReadOnlySpan<byte> chunks)
    {
        byte[]? header = null;
        using var data = new MemoryStream();

        while (chunks.Length >= LengthBytes + TypeBytes + CrcBytes)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(chunks);
            var type = chunks.Slice(LengthBytes, TypeBytes);
            var body = chunks.Slice(LengthBytes + TypeBytes, length);

            if (type.SequenceEqual("IHDR"u8))
            {
                header = body.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                data.Write(body);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            chunks = chunks[(LengthBytes + TypeBytes + length + CrcBytes)..];
        }

        return (header ?? throw new InvalidDataException("PNG: no IHDR chunk"), data.ToArray());
    }

    private static int Channels(byte colour)
    {
        return colour switch
        {
            Grey => 1,
            GreyAlpha => GreyAlphaChannels,
            Rgb => RgbChannels,
            Rgba => OutputChannels,
            _ => throw new NotSupportedException($"PNG: colour type {colour} (palette) is not read"),
        };
    }

    private static byte[] Inflate(byte[] compressed, int length)
    {
        var raw = new byte[length];
        using var zlib = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
        zlib.ReadExactly(raw);

        return raw;
    }

    /// <summary>Undoes each row's filter in place; a row is its filter byte and <paramref name="rowBytes"/> bytes.</summary>
    private static void Unfilter(byte[] raw, int rowBytes, int height, int channels)
    {
        for (int y = 0; y < height; y++)
        {
            int start = (y * (rowBytes + 1)) + 1;
            byte filter = raw[start - 1];

            for (int i = 0; i < rowBytes; i++)
            {
                int left = 0;
                int up = 0;
                int upLeft = 0;

                if (i >= channels)
                {
                    left = raw[start + i - channels];
                }

                if (y > 0)
                {
                    up = raw[start + i - rowBytes - 1];
                    upLeft = PreviousLeft(raw, start + i - rowBytes - 1, i, channels);
                }

                raw[start + i] = (byte)(raw[start + i] + Predict(filter, left, up, upLeft));
            }
        }
    }

    private static int PreviousLeft(byte[] raw, int at, int i, int channels)
    {
        if (i < channels)
        {
            return 0;
        }

        return raw[at - channels];
    }

    private static int Predict(byte filter, int left, int up, int upLeft)
    {
        return filter switch
        {
            0 => 0,
            FilterSub => left,
            FilterUp => up,
            FilterAverage => (left + up) / Halves,
            FilterPaeth => Paeth(left, up, upLeft),
            _ => throw new InvalidDataException($"PNG: filter type {filter}"),
        };
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        int estimate = left + up - upLeft;
        int toLeft = Math.Abs(estimate - left);
        int toUp = Math.Abs(estimate - up);
        int toUpLeft = Math.Abs(estimate - upLeft);

        if (toLeft <= toUp && toLeft <= toUpLeft)
        {
            return left;
        }

        if (toUp <= toUpLeft)
        {
            return up;
        }

        return upLeft;
    }

    private static byte[] ToRgba(byte[] raw, int width, int height, int channels)
    {
        var rgba = new byte[width * height * OutputChannels];

        for (int y = 0; y < height; y++)
        {
            int row = (y * ((width * channels) + 1)) + 1;

            for (int x = 0; x < width; x++)
            {
                Pixel(raw, row + (x * channels), rgba, ((y * width) + x) * OutputChannels, channels);
            }
        }

        return rgba;
    }

    /// <summary>One pixel of <paramref name="channels"/> bytes at <paramref name="from"/> as RGBA at <paramref name="to"/>.</summary>
    private static void Pixel(byte[] raw, int from, byte[] rgba, int to, int channels)
    {
        int green = from;
        int blue = from;
        byte alpha = Opaque;

        if (channels > GreyAlphaChannels)
        {
            green = from + GreenChannel;
            blue = from + BlueChannel;
        }

        if (channels == OutputChannels || channels == GreyAlphaChannels)
        {
            alpha = raw[from + channels - 1];
        }

        rgba[to] = raw[from];
        rgba[to + GreenChannel] = raw[green];
        rgba[to + BlueChannel] = raw[blue];
        rgba[to + Alpha] = alpha;
    }
}
