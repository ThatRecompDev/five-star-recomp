// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using FiveStar.Formats.Images;

namespace FiveStar.Cli.Screens;

/// <summary>
/// The loader's <c>--screens</c> file as lines: <c>&lt;name&gt; &lt;signature&gt; [&lt;max changed cells&gt;]</c>, '#'
/// starting a comment, names compared without case. Learning replaces the screen's line (keeping its limit unless a
/// new one is given) or appends one; every other line stays as it was.
/// </summary>
internal static class ScreenFile
{
    private const char CommentMark = '#';
    private const int NameField = 0;
    private const int SignatureField = 1;
    private const int LimitField = 2;
    private const int MinimumFields = 2;

    /// <summary>The lines with <paramref name="name"/> learned as <paramref name="cells"/>.</summary>
    public static IReadOnlyList<string> Learn(IEnumerable<string> lines, string name, byte[] cells, int? limit)
    {
        var result = new List<string>();
        bool replaced = false;

        foreach (string line in lines)
        {
            string[] fields = Fields(line);

            if (replaced || fields.Length < MinimumFields || !Names(fields, name))
            {
                result.Add(line);

                continue;
            }

            result.Add(Line(name, cells, limit ?? Limit(fields)));
            replaced = true;
        }

        if (!replaced)
        {
            result.Add(Line(name, cells, limit ?? ScreenCells.DefaultMaxChanged));
        }

        return result;
    }

    /// <summary>The cells and limit of <paramref name="name"/>; false when the lines do not hold it.</summary>
    public static bool TryFind(IEnumerable<string> lines, string name, out byte[] cells, out int limit)
    {
        foreach (string line in lines)
        {
            string[] fields = Fields(line);

            if (fields.Length >= MinimumFields && Names(fields, name))
            {
                cells = Convert.FromBase64String(fields[SignatureField]);
                limit = Limit(fields);

                return true;
            }
        }

        cells = [];
        limit = 0;

        return false;
    }

    private static string Line(string name, byte[] cells, int limit)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{name} {Convert.ToBase64String(cells)} {limit}");
    }

    private static string[] Fields(string line)
    {
        return line.Split(CommentMark)[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool Names(string[] fields, string name)
    {
        return fields[NameField].Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    private static int Limit(string[] fields)
    {
        if (fields.Length > LimitField)
        {
            return int.Parse(fields[LimitField], CultureInfo.InvariantCulture);
        }

        return ScreenCells.DefaultMaxChanged;
    }
}
