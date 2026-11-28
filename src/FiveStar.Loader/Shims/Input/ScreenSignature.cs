// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Formats.Images;

namespace FiveStar.Loader.Shims.Input;

/// <summary>
/// A presented frame shrunk to <see cref="Columns"/> x <see cref="Rows"/> cells of mean luminance, one byte each: what
/// an input script's <c>screen</c> blocks recognise a screen by (<see cref="ScreenCells"/>, which <c>fivestar screen
/// learn</c> shares). Two signatures differ by the number of cells whose luminance differs by more than
/// <see cref="CellTolerance"/>, so animation in a corner or a moved focus changes a few cells while another screen
/// changes many.
/// </summary>
internal sealed class ScreenSignature
{
    public const int Columns = ScreenCells.Columns;
    public const int Rows = ScreenCells.Rows;
    public const int Cells = ScreenCells.Cells;

    /// <summary>Luminance levels (of 255) two cells may differ by and still count as the same.</summary>
    public const int CellTolerance = ScreenCells.CellTolerance;

    private readonly byte[] _cells;

    private ScreenSignature(byte[] cells)
    {
        _cells = cells;
    }

    /// <summary>The signature of an RGBA8 image.</summary>
    public static ScreenSignature Of(ReadOnlySpan<byte> rgba, int width, int height)
    {
        return new ScreenSignature(ScreenCells.Of(rgba, width, height));
    }

    /// <summary>Reads the text form (<see cref="ToString"/>); null when it is not one.</summary>
    public static ScreenSignature? Parse(string text)
    {
        var cells = new byte[Cells];

        if (!Convert.TryFromBase64String(text, cells, out int written) || written != Cells)
        {
            return null;
        }

        return new ScreenSignature(cells);
    }

    /// <summary>How many cells differ by more than <see cref="CellTolerance"/>.</summary>
    public int Distance(ScreenSignature other)
    {
        return Distance(other, CellRegion.Whole);
    }

    /// <summary>How many cells of <paramref name="region"/> differ by more than <see cref="CellTolerance"/>.</summary>
    public int Distance(ScreenSignature other, CellRegion region)
    {
        return ScreenCells.Distance(_cells, other._cells, region.FirstColumn, region.FirstRow, region.LastColumn, region.LastRow);
    }

    /// <summary>The cells in base64.</summary>
    public override string ToString()
    {
        return Convert.ToBase64String(_cells);
    }
}
