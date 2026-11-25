// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;

namespace FiveStar.Shaders.Translation;

/// <summary>
/// The non-finite sites of a probed pixel or compute program (<see cref="MeshProbe"/>, FIVESTAR_STAGE_PROBE): every line
/// that sets a VGPR from a float result (<c>vN = select(active, (uint)(asuint(...)), vN);</c>, <c>if (active) vN =
/// asuint(...);</c>: arithmetic, transcendentals, image and buffer loads) gets a site number after the load sites and
/// notes, per invocation, the first such site whose value comes out NaN or infinite, with that value. At its end the
/// first lane of each wave records the wave's earliest site (+ 1, 0 for none), its value, the pixel (x | y &lt;&lt; 16) or
/// group x of a lane that hit it, and how many lanes hit any (<see cref="MeshProbeRecord.NonFiniteSite"/>), in a stage
/// record's otherwise unused dwords 4-7. The helpers are written into the probed program, so the prelude every program
/// includes stays as it is.
/// </summary>
internal static partial class NanProbe
{
    private const string EndName = "probe_end_stage";
    private const string NanEndName = "probe_end_stage_nan";
    private const string Prelude = "rdna_prelude.hlsli";
    private const string UnusedStore = "m.Store4(at + 16u, uint4(0u, 0u, 0u, 0u));";
    private const string NanStore = "m.Store4(at + 16u, uint4(nan_first + 1u, nan_value, nan_at, nan_lanes));";
    private const string Prefix = "nan ";
    private const int MaxSiteText = 160;

    /// <summary>The lengths of "{\n" and "\n}\n".</summary>
    private const int OpenLength = 2;
    private const int CloseLength = 3;

    private const string Notes = """
        static uint g_probe_nan_site = 0xffffffffu, g_probe_nan_value = 0u;
        void probe_nan(uint site, uint v, bool on) {   // the first site whose value is NaN or infinite
            if (on && g_probe_nan_site == 0xffffffffu && !isfinite(asfloat(v))) { g_probe_nan_site = site; g_probe_nan_value = v; }
        }

        """;

    private const string WaveNotes = """
            uint nan_first = WaveActiveMin(g_probe_nan_site);
            uint nan_lanes = WaveActiveCountBits(g_probe_nan_site != 0xffffffffu);
            uint nan_value = WaveActiveMin(g_probe_nan_site == nan_first ? g_probe_nan_value : 0xffffffffu);
            uint nan_at = WaveActiveMin(g_probe_nan_site == nan_first ? (at_id.x | (at_id.y << 16)) : 0xffffffffu);

        """;

    /// <summary>The body with a <c>probe_nan</c> after each line that sets a VGPR from a float result; their texts join <paramref name="sites"/>.</summary>
    public static string Number(string body, List<string> sites)
    {
        var lines = body.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var set = FloatSetPattern().Match(lines[i]);

            if (!set.Success)
            {
                continue;
            }

            var text = lines[i].Trim();
            sites.Add(Prefix + text[..Math.Min(text.Length, MaxSiteText)]);
            lines[i] += string.Create(CultureInfo.InvariantCulture, $" probe_nan({sites.Count - 1}u, {set.Groups["reg"].Value}, active);");
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// The helpers written ahead of the program's body and the end call: <c>probe_end_stage</c> from the prelude, taken
    /// as it is with the non-finite notes stored in dwords 4-7; the prelude's own end when its form is not the one known.
    /// </summary>
    public static (string Helpers, string Call) End()
    {
        var prelude = RuntimeIncludes.Read(Prelude).Replace("\r\n", "\n", StringComparison.Ordinal);
        int start = prelude.IndexOf($"void {EndName}(", StringComparison.Ordinal);
        int open = start < 0 ? -1 : prelude.IndexOf("{\n", start, StringComparison.Ordinal);
        int close = open < 0 ? -1 : prelude.IndexOf("\n}\n", open, StringComparison.Ordinal);

        if (close < 0 || !prelude[open..close].Contains(UnusedStore, StringComparison.Ordinal))
        {
            return (Notes, EndName);
        }

        var head = prelude[start..(open + OpenLength)].Replace(EndName, NanEndName, StringComparison.Ordinal);
        var rest = prelude[(open + OpenLength)..(close + CloseLength)].Replace(UnusedStore, NanStore, StringComparison.Ordinal);

        return (Notes + head + WaveNotes + rest + "\n", NanEndName);
    }

    /// <summary>A line setting a VGPR from a float result (not an integer one through <c>asint</c>).</summary>
    [GeneratedRegex(@"^\s*(?:if \(active\) )?(?<reg>v\d+) = (?:select\(active, \(uint\)\(asuint\(|asuint\()(?!(?:min|max|med3i|clamp)\(asint|asint\(|\(int\))[^\n]*;\s*$")]
    private static partial Regex FloatSetPattern();
}
