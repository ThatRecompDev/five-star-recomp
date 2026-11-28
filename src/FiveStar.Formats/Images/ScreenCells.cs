// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Formats.Images;

/// <summary>
/// A frame shrunk to <see cref="Columns"/> x <see cref="Rows"/> cells of mean Rec. 709 luminance, one byte each (every
/// 4th pixel of every 4th row read): the signature the loader's input-script screen blocks recognise a screen by, and
/// that <c>fivestar screen learn</c> computes from a captured PNG. Two signatures differ by the number of cells whose
/// luminance differs by more than <see cref="CellTolerance"/>.
/// </summary>
public static class ScreenCells
{
    public const int Columns = 48;
    public const int Rows = 27;
    public const int Cells = Columns * Rows;

    /// <summary>Luminance levels (of 255) two cells may differ by and still count as the same.</summary>
    public const int CellTolerance = 16;

    /// <summary>Changed cells a frame may have and still show a learned screen, unless its line says otherwise.</summary>
    public const int DefaultMaxChanged = 40;

    /// <summary>Pixels between two read in a cell, along each axis: a cell of a 2560x1440 frame is about 53 pixels wide.</summary>
    private const int Stride = 4;

    private const int Channels = 4;
    private const int Green = 1;
    private const int Blue = 2;

    /// <summary>Rec. 709 luma weights in 1/256ths: 54 R + 183 G + 19 B.</summary>
    private const int RedWeight = 54;
    private const int GreenWeight = 183;
    private const int BlueWeight = 19;
    private const int WeightShift = 8;

    /// <summary>The cells of an RGBA8 image, row by row.</summary>
    public static byte[] Of(ReadOnlySpan<byte> rgba, int width, int height)
    {
        var sums = new long[Cells];
        var counts = new int[Cells];

        for (int y = 0; y < height; y += Stride)
        {
            int row = y * Rows / height * Columns;

            for (int x = 0; x < width; x += Stride)
            {
                int at = ((y * width) + x) * Channels;
                int luma = ((rgba[at] * RedWeight) + (rgba[at + Green] * GreenWeight) + (rgba[at + Blue] * BlueWeight)) >> WeightShift;
                int cell = row + (x * Columns / width);
                sums[cell] += luma;
                counts[cell]++;
            }
        }

        var cells = new byte[Cells];

        for (int i = 0; i < Cells; i++)
        {
            cells[i] = (byte)(sums[i] / Math.Max(1, counts[i]));
        }

        return cells;
    }

    /// <summary>How many cells from (<paramref name="firstColumn"/>, <paramref name="firstRow"/>) to (<paramref name="lastColumn"/>, <paramref name="lastRow"/>) differ by more than <see cref="CellTolerance"/>.</summary>
    public static int Distance(ReadOnlySpan<byte> one, ReadOnlySpan<byte> other, int firstColumn, int firstRow, int lastColumn, int lastRow)
    {
        int changed = 0;

        for (int row = firstRow; row <= lastRow; row++)
        {
            for (int column = firstColumn; column <= lastColumn; column++)
            {
                int i = (row * Columns) + column;

                if (Math.Abs(one[i] - other[i]) > CellTolerance)
                {
                    changed++;
                }
            }
        }

        return changed;
    }
}
