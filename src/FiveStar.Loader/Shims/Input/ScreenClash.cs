// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.Input;

/// <summary>
/// Two screens of an input script learned so alike that a frame showing one begins the other's block too
/// (<see cref="ScreenStore.Alike"/>): <see cref="Distance"/> cells apart where a frame may differ by <see cref="Limit"/>.
/// </summary>
internal readonly record struct ScreenClash(string First, string Second, int Distance, int Limit)
{
    public bool Names(string screen)
    {
        return string.Equals(First, screen, StringComparison.OrdinalIgnoreCase) || string.Equals(Second, screen, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The warning for the log.</summary>
    public string Describe(string? file)
    {
        return $"[input] screens {First} and {Second} are {Distance} cells apart, within the {Limit} a frame may differ by: one frame begins both blocks, whatever the game shows. Remove their lines from {file ?? "the screens file"} and press F8 on each screen again";
    }
}
