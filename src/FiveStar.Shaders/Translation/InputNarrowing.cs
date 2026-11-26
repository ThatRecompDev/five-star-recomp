// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.RegularExpressions;

namespace FiveStar.Shaders.Translation;

/// <summary>
/// Narrows a pixel shader's <c>PSIn</c> vectors to the components it reads (<see cref="StageLinker.TrimInputs"/>): a
/// member read only in .x and .y becomes a float2, so dxc packs it beside another narrow input in one register and a
/// mesh program linked to these inputs has fewer rows to output (its 32 KB). Components are cut from the end only, so
/// every kept component stays where the producer writes it; modes and semantics are untouched.
/// </summary>
internal static partial class InputNarrowing
{
    private const string Components = "xyzw";

    /// <summary>The pixel HLSL with each input vector declared as wide as its highest read component.</summary>
    public static string Apply(string pixelHlsl)
    {
        var input = PixelInputPattern().Match(pixelHlsl);

        if (!input.Success)
        {
            return pixelHlsl;
        }

        var lines = pixelHlsl.Split('\n');
        var reads = ReadLines(lines);

        foreach (Match member in MemberPattern().Matches(input.Groups["members"].Value))
        {
            Narrow(lines, reads, member);
        }

        return string.Join("\n", lines);
    }

    /// <summary>The lines that read locals, not the ones that fill them from <c>pin</c>.</summary>
    private static string ReadLines(string[] lines)
    {
        return string.Join("\n", lines.Where(line => !line.Contains("pin.", StringComparison.Ordinal)));
    }

    private static void Narrow(string[] lines, string reads, Match member)
    {
        string name = member.Groups["name"].Value;
        int width = Width(member.Groups["width"].Value);
        var fills = Enumerable.Range(0, lines.Length).Where(index => Regex.IsMatch(lines[index], $@"\bpin\.{name}\b")).ToList();

        if (width <= 1 || fills.Count == 0)
        {
            return;
        }

        int kept = 0;

        foreach (int index in fills)
        {
            var fill = Fill(lines[index], name, width);

            if (fill is null)
            {
                return;
            }

            kept = Math.Max(kept, Used(reads, fill.Value.Local, fill.Value.Mask));
        }

        kept = Math.Max(kept, 1);

        if (kept >= width)
        {
            return;
        }

        Declare(lines, member, kept);

        foreach (int index in fills)
        {
            lines[index] = Refill(lines[index], name, width, kept);
        }
    }

    /// <summary>The local a fill line writes and the components it writes, in the member's order; null for another form.</summary>
    private static (string Local, string Mask)? Fill(string line, string name, int width)
    {
        var whole = Regex.Match(line, $@"^\s*float4 (?<local>\w+) = pin\.{name};\s*$");

        if (whole.Success)
        {
            return (whole.Groups["local"].Value, Components[..width]);
        }

        var part = Regex.Match(line, $@"^\s*(?<local>\w+)\.(?<mask>[xyzw]+) = .*\bpin\.{name}\b.*;\s*$");

        if (part.Success && part.Groups["mask"].Length == width)
        {
            return (part.Groups["local"].Value, part.Groups["mask"].Value);
        }

        return null;
    }

    /// <summary>One past the highest position of <paramref name="mask"/> whose component of the local is read; all for a whole read.</summary>
    private static int Used(string reads, string local, string mask)
    {
        int kept = 0;

        foreach (Match use in Regex.Matches(reads, $@"\b{local}\b(?<swizzle>\.[xyzw]+)?(?<assign>\s*=(?!=))?"))
        {
            if (use.Groups["assign"].Success)
            {
                continue;
            }

            if (!use.Groups["swizzle"].Success)
            {
                return mask.Length;
            }

            for (int position = 0; position < mask.Length; position++)
            {
                if (use.Groups["swizzle"].Value.Contains(mask[position], StringComparison.Ordinal))
                {
                    kept = Math.Max(kept, position + 1);
                }
            }
        }

        return kept;
    }

    private static void Declare(string[] lines, Match member, int kept)
    {
        string old = member.Value.TrimEnd('\r');
        int index = Array.FindIndex(lines, line => line.TrimEnd('\r') == old);
        string type = member.Groups["base"].Value + Suffix(kept);

        lines[index] = lines[index].Replace(member.Groups["type"].Value + " " + member.Groups["name"].Value, type + " " + member.Groups["name"].Value, StringComparison.Ordinal);
    }

    private static string Refill(string line, string name, int width, int kept)
    {
        var whole = Regex.Match(line, $@"^(?<indent>\s*)float4 (?<local>\w+) = pin\.{name};");

        if (whole.Success)
        {
            string local = whole.Groups["local"].Value;

            return $"{whole.Groups["indent"].Value}float4 {local} = 0; {local}.{Components[..kept]} = pin.{name};";
        }

        return Regex.Replace(line, @"^(?<indent>\s*)(?<local>\w+)\.(?<mask>[xyzw]+) =", match =>
            $"{match.Groups["indent"].Value}{match.Groups["local"].Value}.{match.Groups["mask"].Value[..kept]} =");
    }

    private static int Width(string digits)
    {
        if (digits.Length == 0)
        {
            return 1;
        }

        return int.Parse(digits, CultureInfo.InvariantCulture);
    }

    private static string Suffix(int width)
    {
        if (width == 1)
        {
            return string.Empty;
        }

        return width.ToString(CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^struct PSIn \{\n(?<members>.*?)\n\};", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex PixelInputPattern();

    [GeneratedRegex(@"^[ \t]*(?:(?:nointerpolation|linear|noperspective|centroid|sample)[ \t]+)*(?<type>(?<base>float|uint|int)(?<width>[234]?))[ \t]+(?<name>\w+)[ \t]*:[ \t]*(?!SV_)\w+[ \t]*;[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex MemberPattern();
}
