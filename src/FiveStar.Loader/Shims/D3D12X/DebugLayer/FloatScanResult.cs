// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>How many values of a read-back input are NaN or Inf, and the first: texel (or byte offset), channel, value.</summary>
internal readonly record struct FloatScanResult(int Count, int X, int Y, int Channel, float Value)
{
    /// <summary>One more non-finite value; the first one found is kept.</summary>
    public FloatScanResult With(int x, int y, int channel, float value)
    {
        if (Count == 0)
        {
            return new FloatScanResult(1, x, y, channel, value);
        }

        return this with { Count = Count + 1 };
    }

    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"{Count} non-finite, first {Value} at ({X}, {Y}) channel {Channel}");
    }
}
