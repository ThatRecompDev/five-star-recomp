// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Shaders.Translation.Waves;

/// <summary>
/// A branch on "any lane of <paramref name="Mask"/>" at the end of block <paramref name="Address"/> that each lane may
/// decide by its own bit: a lane in the mask takes <paramref name="Some"/>, one outside it <paramref name="None"/>, and
/// the paths meet at <paramref name="Join"/> (null when the mask is the same on every lane, so no lane parts from the others).
/// </summary>
internal sealed record LaneBranch(int Address, string Mask, int Some, int None, int? Join);
