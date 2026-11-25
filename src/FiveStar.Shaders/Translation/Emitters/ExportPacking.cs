// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Shaders.Translation.Emitters;

/// <summary>
/// The channels of a compressed export (<c>exp ... compr</c>): each register holds two FP16 channels (every compressed
/// export of the game is packed by v_cvt_pkrtz_f16_f32), and the colour buffer converts them to the target's number
/// type (shadPS4's export translation does the same per export format; facts only). The <c>Exp</c> struct holds
/// floats, and an integer target takes its channels as bits (<c>asuint</c>/<c>asint</c> in the entry point), so an
/// FP16 channel bound for one is converted to an integer first: the console's clears write a uint target from a float
/// colour this way (PS_ClearUint32: a clear to 1.0 stores 1, not 0x3f800000).
/// </summary>
internal static class ExportPacking
{
    /// <summary>One channel of the Exp float4 from the FP16 half in the low bits of <paramref name="half"/>.</summary>
    public static string Channel(string half, string targetType)
    {
        var value = $"f16tof32({half})";

        return targetType switch
        {
            "uint" => $"asfloat((uint)round(max({value}, 0.0)))",
            "int" => $"asfloat((int)round({value}))",
            _ => value,
        };
    }
}
