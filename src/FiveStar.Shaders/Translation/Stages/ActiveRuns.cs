// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.RegularExpressions;

namespace FiveStar.Shaders.Translation.Stages;

/// <summary>
/// A pixel program's runs of vector writes under EXEC (<c>v = select(active, (uint)(x), v);</c>, one select per write)
/// become one branch over the lanes: <c>if (active) { v = (uint)(x); ... }</c>. A lane outside EXEC keeps its value
/// either way, and a write's operands are read in the same order, so the two are the same lane by lane; the branch
/// costs one test where the selects cost one per write. Only writes whose value reads no other lane and needs no
/// derivative go inside (no wave, quad, sample or gather operation), so nothing in the branch depends on lanes it
/// skips. A run shorter than <see cref="MinimumRun"/> stays as it is, and the compilers turn short arms back into
/// selects anyway (the DXIL backend's IfConversion). ps_light_proxy_conservative 8501a4a1 holds 5,924 such selects,
/// 4,898 of them in runs of four or more, after EXEC narrowing the whole-quad entry (ProgramEmitter.WholeQuadEntry)
/// does not reach.
/// </summary>
internal static partial class ActiveRuns
{
    /// <summary>The fewest writes a run must hold to become a branch.</summary>
    public const int MinimumRun = 4;

    private static readonly string[] CrossLane = ["Wave", "Quad", "quad_perm", "vw_", "readfirstlane", "Sample", "Gather", "ddx", "ddy", "IsHelperLane"];

    /// <summary>The program text with each run of at least <see cref="MinimumRun"/> such writes in one branch.</summary>
    public static string Branch(string text)
    {
        var lines = text.Split('\n');
        var result = new StringBuilder(text.Length);
        var run = new List<Match>();

        foreach (var line in lines)
        {
            var write = WritePattern().Match(line);

            bool joins = write.Success && write.Groups["target"].Value != "active" && Local(write.Groups["value"].Value);

            if (joins && run.Count > 0 && run[0].Groups["indent"].Value != write.Groups["indent"].Value)
            {
                Flush(result, run);
            }

            if (joins)
            {
                run.Add(write);

                continue;
            }

            Flush(result, run);
            result.Append(line).Append('\n');
        }

        Flush(result, run);

        // Split leaves one empty piece after a final newline (or none without one): the text keeps its own ending.
        result.Length--;

        return result.ToString();
    }

    /// <summary>Whether a written value reads only this lane (EXEC itself is never a run's target: the branch tests it once).</summary>
    private static bool Local(string value)
    {
        foreach (var name in CrossLane)
        {
            if (value.Contains(name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Appends the pending writes, as one branch when there are enough, and starts a new run.</summary>
    private static void Flush(StringBuilder result, List<Match> run)
    {
        if (run.Count == 0)
        {
            return;
        }

        if (run.Count < MinimumRun)
        {
            foreach (var write in run)
            {
                result.Append(write.Value).Append('\n');
            }

            run.Clear();

            return;
        }

        var indent = run[0].Groups["indent"].Value;
        result.Append(indent).Append("if (active) {\n");

        foreach (var write in run)
        {
            result.Append(indent).Append("  ").Append(write.Groups["target"].Value).Append(" = (").Append(write.Groups["type"].Value).Append(")(")
                .Append(write.Groups["value"].Value).Append(");\n");
        }

        result.Append(indent).Append("}\n");
        run.Clear();
    }

    [GeneratedRegex(@"^(?<indent>[ ]*)(?<target>[A-Za-z_]\w*) = select\(active, \((?<type>u?int|float|bool)\)\((?<value>.*)\), \k<target>\);$")]
    private static partial Regex WritePattern();
}
