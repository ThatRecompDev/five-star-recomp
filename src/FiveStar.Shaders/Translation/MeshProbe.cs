// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FiveStar.Shaders.Translation;

/// <summary>
/// A translated mesh program with probe code (rdna_prelude.hlsli, FIVESTAR_MESH_PROBE): every load site (a
/// <c>buf_fits</c>, <c>BufLoad</c>, <c>BufLoadRW</c> or <c>PtrLoad</c> call; a buffer load's range check and load share
/// one) calls the probe form with its site number, which marks the site in a per-thread mask when its range check fails,
/// it reads through an unmapped page of the address map, or through a record naming no descriptor. Vertex 0's position
/// and the culled primitives are noted as they are written, and at the end of the group thread 0 appends a
/// <see cref="MeshProbeRecord"/> to a ring in the GPU-memory window. The same line in both phases of a two-phase
/// program gets the same site numbers. Site 31 also stands for every site past it.
/// </summary>
public static partial class MeshProbe
{
    /// <summary>The define of probed programs; its value is <see cref="Id"/> of the program.</summary>
    public const string Define = "FIVESTAR_MESH_PROBE";

    /// <summary>The ring: a count, then records from <see cref="RecordsOffset"/> (rdna_prelude PROBE_RECORDS).</summary>
    public const uint Records = 4096;
    public const uint RecordsOffset = 16;
    public const uint RecordBytes = MeshProbeRecord.Dwords * sizeof(uint);

    /// <summary>Window bytes the ring takes.</summary>
    public const uint RingBytes = RecordsOffset + (Records * RecordBytes);

    private const string MeshEntry = "out vertices Out verts[";
    private const string GroupStart = "g_ms_nv = 0; g_ms_np = 0; g_ms_alloc = 0; }";
    private const string BufferCheck = "buf_fits";
    private const string ProbePrefix = "probe_";
    private const int MaxSiteText = 160;
    private const uint FnvOffset = 2166136261;
    private const uint FnvPrime = 16777619;

    /// <summary>A stable id of a program name (32-bit FNV-1a), the same in every boot.</summary>
    public static uint Id(string name)
    {
        uint hash = FnvOffset;

        foreach (char c in name)
        {
            hash = (hash ^ c) * FnvPrime;
        }

        return hash;
    }

    /// <summary>
    /// The program with probe code writing to the ring at byte <paramref name="ringOffset"/> of the GPU-memory window,
    /// and the text of each load site by number. Throws <see cref="UnsupportedProgramException"/> for a program that is
    /// not a mesh program.
    /// </summary>
    public static (string Hlsl, IReadOnlyList<string> Sites) Instrument(string hlsl, uint id, uint ringOffset)
    {
        if (!hlsl.Contains(MeshEntry, StringComparison.Ordinal))
        {
            throw new UnsupportedProgramException("the probe is for mesh programs");
        }

        int body = BodyStart(hlsl);
        var sites = new List<string>();
        var text = hlsl[..body] + NumberSites(hlsl[body..], sites);
        int start = text.IndexOf(GroupStart, StringComparison.Ordinal);
        int end = text.TrimEnd().LastIndexOf('}');

        if (start < 0 || end < 0)
        {
            throw new UnsupportedProgramException("no mesh group start or end to probe");
        }

        text = text[..end] + "  probe_end(in_gid, in_gi, g_ms_nv, g_ms_np);\n" + text[end..];
        text = text.Insert(start + GroupStart.Length, " probe_begin(in_gi);");
        text = NotePosition(text);
        text = CullPattern().Replace(text, "$0 probe_cull(in_gi, ($1 >> 31) != 0);");
        var defines = string.Create(CultureInfo.InvariantCulture, $"#define {Define} {id}u\n#define {Define}_BASE {ringOffset}u\n");

        return (defines + text, sites);
    }

    /// <summary>Where the program's own code starts: past its last #include line.</summary>
    private static int BodyStart(string hlsl)
    {
        var include = IncludePattern().Matches(hlsl);

        if (include.Count == 0)
        {
            return 0;
        }

        var last = include[^1];

        return last.Index + last.Length;
    }

    /// <summary>Vertex 0's position where it is written: whole (staged) or as its member (direct export).</summary>
    private static string NotePosition(string text)
    {
        var position = PositionPattern().Match(text);

        if (!position.Success)
        {
            return text;
        }

        var member = position.Groups["name"].Value;
        text = text.Replace($"verts[in_gi].{member} = _e;", $"verts[in_gi].{member} = _e; if (in_gi == 0u) probe_position(_e);", StringComparison.Ordinal);

        return text.Replace("verts[in_gi] = o;", $"verts[in_gi] = o; if (in_gi == 0u) probe_position(o.{member});", StringComparison.Ordinal);
    }

    /// <summary>Each load call as its probe form with a site number; equal lines get equal numbers.</summary>
    private static string NumberSites(string body, List<string> sites)
    {
        var known = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var lines = body.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var key = lines[i].Trim();

            if (!LoadPattern().IsMatch(key))
            {
                continue;
            }

            if (!known.TryGetValue(key, out var numbers))
            {
                numbers = LineSites(lines[i], sites);
                known[key] = numbers;
            }

            int next = 0;
            string? previous = null;
            lines[i] = LoadPattern().Replace(lines[i], call =>
            {
                var name = call.Groups["name"].Value;

                // A buffer load's check and load are one site: the load takes the number of the check before it.
                int site = previous == BufferCheck && name != BufferCheck ? numbers[next - 1] : numbers[next++];
                previous = name;

                return string.Create(CultureInfo.InvariantCulture, $"{ProbePrefix}{name}({site}u, ");
            });
        }

        return string.Join('\n', lines);
    }

    /// <summary>New site numbers for the sites of one line, with their text.</summary>
    private static List<int> LineSites(string line, List<string> sites)
    {
        var numbers = new List<int>();
        string? previous = null;

        foreach (Match call in LoadPattern().Matches(line))
        {
            var name = call.Groups["name"].Value;

            if (previous == BufferCheck && name != BufferCheck)
            {
                previous = name;

                continue;
            }

            numbers.Add(sites.Count);
            sites.Add(CallText(line, call.Index));
            previous = name;
        }

        return numbers;
    }

    /// <summary>A call's text up to its closing parenthesis (shortened).</summary>
    private static string CallText(string line, int start)
    {
        int depth = 0;
        var text = new StringBuilder();

        for (int i = start; i < line.Length && text.Length < MaxSiteText; i++)
        {
            text.Append(line[i]);
            depth += line[i] switch
            {
                '(' => 1,
                ')' => -1,
                _ => 0,
            };

            if (depth == 0 && line[i] == ')')
            {
                break;
            }
        }

        return text.ToString();
    }

    [GeneratedRegex(@"\b(?<name>buf_fits|BufLoadRW|BufLoad|PtrLoad)\(")]
    private static partial Regex LoadPattern();

    [GeneratedRegex(@"^#include ""[^""]+""[^\n]*\n", RegexOptions.Multiline)]
    private static partial Regex IncludePattern();

    [GeneratedRegex(@"float4 (?<name>\w+) : SV_Position\b", RegexOptions.IgnoreCase)]
    private static partial Regex PositionPattern();

    [GeneratedRegex(@"prims\[in_gi\]\.cull = \((_?p) >> 31\) != 0;")]
    private static partial Regex CullPattern();
}
