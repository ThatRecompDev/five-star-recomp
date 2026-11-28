// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using FiveStar.Formats.Images;
using Xunit;

namespace FiveStar.Formats.Tests;

/// <summary>PNG files read back as RGBA, and the screen signature computed from them.</summary>
public sealed class PngReaderTests
{
    private const int Width = 3;
    private const int Height = 5;

    [Fact]
    public void What_the_writer_wrote_reads_back()
    {
        var rgba = Pixels(Width, Height, 4);
        using var file = new MemoryStream();
        PngWriter.Write(file, Width, Height, rgba);

        var image = PngReader.Read(file.ToArray());

        Assert.Equal((Width, Height), (image.Width, image.Height));
        Assert.Equal(rgba, image.Pixels);
    }

    [Fact]
    public void Every_row_filter_of_an_rgb_file_is_undone()
    {
        var rgb = Pixels(Width, Height, 3);

        // Rows 0-4 use filters none, sub, up, average and Paeth.
        var image = PngReader.Read(Encode(rgb, Width, Height));

        for (int i = 0; i < Width * Height; i++)
        {
            Assert.Equal(rgb.AsSpan(i * 3, 3).ToArray(), image.Pixels.AsSpan(i * 4, 3).ToArray());
            Assert.Equal(255, image.Pixels[(i * 4) + 3]);
        }
    }

    [Fact]
    public void A_uniform_frame_has_uniform_cells_and_a_changed_corner_changes_only_its_cells()
    {
        const int FrameWidth = 192;
        const int FrameHeight = 108;
        var grey = new byte[FrameWidth * FrameHeight * 4];
        Array.Fill(grey, (byte)100);

        var one = ScreenCells.Of(grey, FrameWidth, FrameHeight);
        var lit = (byte[])grey.Clone();

        // A cell is 4x4 pixels here: lighting the top-left 8x8 pixels changes 2x2 cells.
        for (int y = 0; y < 8; y++)
        {
            Array.Fill(lit, (byte)250, y * FrameWidth * 4, 8 * 4);
        }

        var other = ScreenCells.Of(lit, FrameWidth, FrameHeight);

        Assert.All(one, cell => Assert.InRange(cell, 99, 100));
        Assert.Equal(4, ScreenCells.Distance(one, other, 0, 0, ScreenCells.Columns - 1, ScreenCells.Rows - 1));
        Assert.Equal(0, ScreenCells.Distance(one, other, 2, 2, ScreenCells.Columns - 1, ScreenCells.Rows - 1));
    }

    private static byte[] Pixels(int width, int height, int channels)
    {
        var pixels = new byte[width * height * channels];

        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i * 37) + (i / 5 * 11));
        }

        return pixels;
    }

    /// <summary>An 8-bit RGB PNG whose row y uses filter y % 5.</summary>
    private static byte[] Encode(byte[] rgb, int width, int height)
    {
        int rowBytes = width * 3;
        var raw = new byte[(rowBytes + 1) * height];

        for (int y = 0; y < height; y++)
        {
            byte filter = (byte)(y % 5);
            raw[y * (rowBytes + 1)] = filter;

            for (int i = 0; i < rowBytes; i++)
            {
                int left = i >= 3 ? rgb[(y * rowBytes) + i - 3] : 0;
                int up = y > 0 ? rgb[((y - 1) * rowBytes) + i] : 0;
                int upLeft = y > 0 && i >= 3 ? rgb[((y - 1) * rowBytes) + i - 3] : 0;
                int predicted = Predict(filter, left, up, upLeft);
                raw[(y * (rowBytes + 1)) + 1 + i] = (byte)(rgb[(y * rowBytes) + i] - predicted);
            }
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;

        using var file = new MemoryStream();
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(file, "IHDR"u8, header);
        Chunk(file, "IDAT"u8, Compress(raw));
        Chunk(file, "IEND"u8, []);

        return file.ToArray();
    }

    private static int Predict(byte filter, int left, int up, int upLeft)
    {
        int estimate = left + up - upLeft;
        int toLeft = Math.Abs(estimate - left);
        int toUp = Math.Abs(estimate - up);
        int toUpLeft = Math.Abs(estimate - upLeft);
        int paeth = upLeft;

        if (toLeft <= toUp && toLeft <= toUpLeft)
        {
            paeth = left;
        }
        else if (toUp <= toUpLeft)
        {
            paeth = up;
        }

        return filter switch
        {
            1 => left,
            2 => up,
            3 => (left + up) / 2,
            4 => paeth,
            _ => 0,
        };
    }

    private static byte[] Compress(byte[] raw)
    {
        using var buffer = new MemoryStream();

        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return buffer.ToArray();
    }

    private static void Chunk(Stream output, ReadOnlySpan<byte> type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);
        output.Write(new byte[4]);
    }
}
