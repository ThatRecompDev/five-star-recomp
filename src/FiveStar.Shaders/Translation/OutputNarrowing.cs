// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FiveStar.Shaders.Translation;

/// <summary>
/// A producer's outputs narrowed to the width the linked pixel shader reads them in (<see cref="InputNarrowing"/>):
/// the program keeps writing the full value, now to a static variable, and the narrowed <c>Out</c> member takes its
/// leading components where the producer hands its outputs over (before <c>return o;</c>, before a staging mesh
/// program stores <c>o</c>, or after each write of a mesh program that writes its outputs at export).
/// </summary>
internal static class OutputNarrowing
{
    private const string Components = "xyzw";
    private const string Prefix = "nar_";
    private const string ReturnAnchor = "\n  return o;";
    private const string StagedAnchor = "\n    verts[in_gi] = o;";
    private const string MeshEntry = "out vertices Out verts[";

    /// <summary>The declaration of <paramref name="member"/> narrowed to <paramref name="width"/> components; null when it is not wider.</summary>
    public static string? Narrowed(OutStructMember member, int width)
    {
        var type = Regex.Match(member.Declaration, @"^(?<base>float|uint|int)(?<width>[234])\s");

        if (!type.Success || width < 1 || width >= int.Parse(type.Groups["width"].Value, CultureInfo.InvariantCulture))
        {
            return null;
        }

        string suffix = string.Empty;

        if (width > 1)
        {
            suffix = width.ToString(CultureInfo.InvariantCulture);
        }

        return type.Groups["base"].Value + suffix + member.Declaration[(type.Length - 1)..];
    }

    /// <summary>Redirects the producer's writes of each narrowed member to a full-width static and hands the leading components over.</summary>
    public static string Apply(string hlsl, IReadOnlyList<(OutStructMember Member, int Width)> narrowed)
    {
        if (narrowed.Count == 0)
        {
            return hlsl;
        }

        var statics = new StringBuilder();
        var copies = new StringBuilder();

        foreach (var (member, width) in narrowed)
        {
            string full = member.Declaration[..member.Declaration.IndexOf(' ', StringComparison.Ordinal)];
            string variable = Prefix + member.Name;
            statics.Append(CultureInfo.InvariantCulture, $"static {full} {variable};\n");
            hlsl = Regex.Replace(hlsl, $@"(?:verts\[in_gi\]|\bo)\.{Regex.Escape(member.Name)}\b", variable);
            copies.Append(CultureInfo.InvariantCulture, $" o.{member.Name} = {variable}.{Components[..width]};");
        }

        hlsl = HandOver(hlsl, narrowed, copies.ToString());
        int at = hlsl.IndexOf("struct Out {", StringComparison.Ordinal);

        return hlsl.Insert(at, statics.ToString());
    }

    private static string HandOver(string hlsl, IReadOnlyList<(OutStructMember Member, int Width)> narrowed, string copies)
    {
        if (!hlsl.Contains(MeshEntry, StringComparison.Ordinal))
        {
            int end = hlsl.LastIndexOf(ReturnAnchor, StringComparison.Ordinal);

            if (end < 0)
            {
                throw new UnsupportedProgramException("no return of the Out struct");
            }

            return hlsl.Insert(end, "\n " + copies);
        }

        int staged = hlsl.LastIndexOf(StagedAnchor, StringComparison.Ordinal);

        if (staged >= 0)
        {
            return hlsl.Insert(staged, "\n   " + copies);
        }

        // Mesh programs that write their outputs at each export: copy after each write of the full value.
        foreach (var (member, width) in narrowed)
        {
            string variable = Prefix + member.Name;
            hlsl = Regex.Replace(hlsl, $@"(\b{variable}(?:\.[xyzw]+)?\s*=(?!=)[^;]*;)", $"$1 verts[in_gi].{member.Name} = {variable}.{Components[..width]};");
        }

        return hlsl;
    }
}
