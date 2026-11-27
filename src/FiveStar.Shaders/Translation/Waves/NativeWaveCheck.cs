// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using FiveStar.Shaders.Translation.Core;

namespace FiveStar.Shaders.Translation.Waves;

/// <summary>
/// Whether a pixel, vertex or mesh program needs wave emulation at all (docs/shader-translation.md, Wave size). The
/// emulated operations differ from the native ones only in reads of lanes by number, in ballots, counts and lane numbers,
/// and on hardware waves wider than the program wave. A program passes when its only wave operations are quad
/// operations, "any lane" as a branch (a 64-lane program only: a 32-lane one meets 64-lane AMD waves) and lane bits of
/// constant masks, and a mesh program also passes the window analysis. Anything else, or any shape this does not know,
/// keeps the emulation.
/// </summary>
internal static partial class NativeWaveCheck
{
    private const int Wave64 = 64;

    /// <summary>Null when the program runs natively with the results of its emulated form, else the first reason.</summary>
    public static string? Reason(TranslationState state)
    {
        var lines = state.Prologue.Concat(state.Blocks.SelectMany(block => block.Lines)).ToList();
        var conditions = state.Blocks.Select(block => block.Exit.Condition).OfType<string>().ToList();
        var wide = state.WaveSize == Wave64;
        var reason = Calls(lines, wide) ?? Conditions(conditions, wide) ?? ConstantMasks(lines) ?? SccAsValue(lines);

        if (reason is null && state.Stage == ShaderStage.Mesh)
        {
            reason = new WindowAnalysis(state.Blocks, state.Prologue, state.MeshThreads).Check();
        }

        return reason;
    }

    private static string? Calls(List<string> lines, bool wide)
    {
        foreach (var line in lines)
        {
            foreach (Match call in WaveCallPattern().Matches(line))
            {
                var name = call.Groups["name"].Value;

                if (name.StartsWith("Quad", StringComparison.Ordinal) || name == "quad_perm")
                {
                    continue;
                }

                bool allowed = name switch
                {
                    "vw_any" => wide && AnyBranchPattern().IsMatch(line),
                    "lane_bit" => wide,
                    "WaveIsFirstLane" => line.TrimStart().StartsWith(AllocationLine, StringComparison.Ordinal),
                    _ => false,
                };

                if (!allowed)
                {
                    return $"{name}: {line.Trim()}";
                }
            }
        }

        return null;
    }

    /// <summary>The structured form's branch conditions: "any lane" of a mask, scc, or values without wave operations.</summary>
    private static string? Conditions(List<string> conditions, bool wide)
    {
        foreach (var condition in conditions)
        {
            if (WaveCallPattern().IsMatch(condition) && !(wide && AnyConditionPattern().IsMatch(condition)))
            {
                return $"branch on {condition}";
            }
        }

        return null;
    }

    /// <summary>
    /// The masks lane bits read: each a literal or a variable that only ever holds literals, so no lane's bit depends on
    /// a ballot or loaded data.
    /// </summary>
    private static string? ConstantMasks(List<string> lines)
    {
        var text = string.Join("\n", lines);
        var names = LaneBitPattern().Matches(text).SelectMany(match => new[] { match.Groups["lo"].Value, match.Groups["hi"].Value })
            .Where(argument => !LiteralPattern().IsMatch(argument)).ToHashSet(StringComparer.Ordinal);

        foreach (var name in names)
        {
            var escaped = Regex.Escape(name);
            int uses = Regex.Count(text, $@"\b{escaped}\b");
            int literal = Regex.Count(text, $@"\b{escaped} = (?:0x[0-9a-f]{{1,8}}u?|\d{{1,10}}u?)(?=[;,])");
            int read = Regex.Count(text, $@"\blane_bit\({escaped}, |\blane_bit\([A-Za-z_]\w*, {escaped}\)|\blane_bit\((?:0x[0-9a-f]{{1,8}}u|\d{{1,10}}u), {escaped}\)");

            if (uses != literal + read)
            {
                return $"lane bits of mask {name}";
            }
        }

        return null;
    }

    /// <summary>
    /// SCC set from "any lane" may only be branched on or pick a mask: a value it selects would see the hardware wave.
    /// </summary>
    private static string? SccAsValue(List<string> lines)
    {
        if (!lines.Any(line => SccAnyPattern().IsMatch(line)))
        {
            return null;
        }

        foreach (var line in lines)
        {
            if (!SccPattern().IsMatch(line) || SccUsePattern().IsMatch(line))
            {
                continue;
            }

            return $"scc as a value: {line.Trim()}";
        }

        return null;
    }

    private const string AllocationLine = "if (WaveIsFirstLane()) { g_ms_nv = ";

    [GeneratedRegex(@"\b(?<name>vw_\w+|g_vw_\w+|readfirstlane|lane_bit|quad_perm|Wave[A-Z]\w*|Quad[A-Z]\w*)\b")]
    private static partial Regex WaveCallPattern();

    // scc = vw_any(m); if ([!]vw_any(m)) {; the dispatch form's pc = ([!]vw_any(m)) ? a : b;
    [GeneratedRegex(@"^\s*(?:scc = vw_any\([A-Za-z_]\w*\);|if \(!?vw_any\([A-Za-z_]\w*\)\) \{|pc = \(?!?vw_any\([A-Za-z_]\w*\)\)? \? 0x[0-9a-f]+ : 0x[0-9a-f]+;)\s*$")]
    private static partial Regex AnyBranchPattern();

    [GeneratedRegex(@"^\(?!?vw_any\(\(?[A-Za-z_]\w*\)?\)\)?$")]
    private static partial Regex AnyConditionPattern();

    [GeneratedRegex(@"\blane_bit\((?<lo>[A-Za-z_]\w*|0x[0-9a-f]{1,8}u|\d{1,10}u), (?<hi>[A-Za-z_]\w*|0x[0-9a-f]{1,8}u|\d{1,10}u)\)")]
    private static partial Regex LaneBitPattern();

    [GeneratedRegex(@"^(?:0x[0-9a-f]{1,8}u|\d{1,10}u)$")]
    private static partial Regex LiteralPattern();

    [GeneratedRegex(@"^\s*scc = vw_any\(")]
    private static partial Regex SccAnyPattern();

    [GeneratedRegex(@"\bscc\b")]
    private static partial Regex SccPattern();

    // Writes of scc (not reading it), branches on it, a mask picked by it, and the entry's declaration.
    [GeneratedRegex(@"^\s*(?:scc = (?:(?!\bscc\b)[^;])*;|if \(!?scc\) \{|pc = \(?!?scc\)? \? 0x[0-9a-f]+ : 0x[0-9a-f]+;|m_\w+ = \(scc \? (?:active|m_\w+) : false\);|bool active = .*)\s*$")]
    private static partial Regex SccUsePattern();
}
