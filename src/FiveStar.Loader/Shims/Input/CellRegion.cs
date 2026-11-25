// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.Input;

/// <summary>
/// A rectangle of <see cref="ScreenSignature"/> cells, first and last column and row inclusive (columns 0 to 47 left
/// to right, rows 0 to 26 top to bottom): the part of a frame a script block compares.
/// </summary>
internal readonly record struct CellRegion(int FirstColumn, int FirstRow, int LastColumn, int LastRow)
{
    /// <summary>Every cell.</summary>
    public static CellRegion Whole => new(0, 0, ScreenSignature.Columns - 1, ScreenSignature.Rows - 1);

    /// <summary>Whether the rectangle lies in the grid, first before last.</summary>
    public bool IsValid()
    {
        return FirstColumn >= 0 && FirstRow >= 0 && LastColumn < ScreenSignature.Columns && LastRow < ScreenSignature.Rows
            && FirstColumn <= LastColumn && FirstRow <= LastRow;
    }
}
