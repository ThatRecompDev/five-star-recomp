// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.Input;

/// <summary>
/// What an input script block waits for before it begins (<see cref="InputScript"/>). A <c>screen</c> block waits
/// for the learned screen <see cref="Name"/>: at most <see cref="Limit"/> changed cells (0: the learned screen's
/// limit). A <c>change</c> block waits for the frame to differ from the first frame seen after the block before it
/// began: at least <see cref="Limit"/> changed cells (at least 1), so a press repeated in the block before stops once it
/// took. Both compare <see cref="Region"/> only; with a <see cref="Timeout"/> (seconds after the block before began, 0
/// for none) the block begins anyway, with a warning.
/// </summary>
internal sealed record ScriptBlock(string Name, int Limit, bool Change, CellRegion Region, double Timeout)
{
    /// <summary>Block 0, which begins at loader start.</summary>
    public static ScriptBlock First => new(string.Empty, 0, false, CellRegion.Whole, 0);
}
