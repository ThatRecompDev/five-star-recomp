// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>The DXGI cast families (<see cref="FiveStar.Graphics.FormatFamilies"/>).</summary>
internal static class FormatFamilies
{
    /// <summary>The formats sharing <paramref name="format"/>'s family; the format alone when it has none.</summary>
    public static IReadOnlyList<int> Members(int format)
    {
        return FiveStar.Graphics.FormatFamilies.Members(format);
    }
}
