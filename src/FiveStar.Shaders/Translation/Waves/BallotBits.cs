// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.RegularExpressions;
using FiveStar.Shaders.Translation.Core;

namespace FiveStar.Shaders.Translation.Waves;

/// <summary>
/// Reads a lane's own bit of a ballot from the lane-mask bool the ballot took, before the superstep form cuts the
/// blocks: <c>lane_bit(lo, hi)</c> of two SGPR halves that hold <c>vw_ballot(m).x</c> and <c>.y</c> is <c>m</c> while
/// neither half nor <c>m</c> has been written since, because every thread of the console wave deposits its bit. The
/// ballot then often has no other reader and <see cref="DeadWaveOperations"/> drops it: in
/// MSNC_VS_LinearDepthSkin_f1f2249c most of the ballots left were read only this way. Within one block, in order. A
/// wave32 program's lanes are all below 32, so there <c>lane_bit(lo, x)</c> needs only the low half, whatever x is
/// (<c>0u</c> for a 32-bit mask: <c>s_cselect_b32 s8, exec, 0</c> then <c>v_cndmask</c> on s8, 115 of them in
/// CS_PedStandardSkinning).
/// </summary>
internal static partial class BallotBits
{
    /// <summary>Replaces such reads in <paramref name="blocks"/> of a program of <paramref name="wave32"/> lanes or 64; how many.</summary>
    public static int Substitute(List<CodeBlock> blocks, bool wave32 = false)
    {
        int replaced = 0;

        for (int index = 0; index < blocks.Count; index++)
        {
            // SGPR half -> (mask bool, half) it holds.
            var halves = new Dictionary<string, (string Mask, char Half)>(StringComparer.Ordinal);
            var block = blocks[index];
            replaced += SubstituteBlock(block.Lines, halves, wave32);

            // The structured form branches on the exit's condition, not on the block's pc line: it reads the same bits
            // at the block's end. Left as lane_bit, it read halves whose ballots, now unread in the lines, were dropped
            // (a loop left on VCC then never ended: CS_TransmissionVolumetricsSubmerged_bbfb2953).
            if (block.Exit.Condition is string condition)
            {
                int ignored = 0;
                blocks[index] = block with { Exit = block.Exit with { Condition = Replace(condition, halves, wave32, ref ignored) } };
            }
        }

        return replaced;
    }

    private static int SubstituteBlock(List<string> lines, Dictionary<string, (string Mask, char Half)> halves, bool wave32)
    {
        int replaced = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            string line = Replace(lines[i], halves, wave32, ref replaced);
            lines[i] = line;
            Invalidate(line, halves);

            var ballot = BallotPattern().Match(line);
            var copy = i + 1 < lines.Count ? CopyPattern().Match(lines[i + 1]) : Match.Empty;

            if (ballot.Success && copy.Success && copy.Groups["value"].Value == ballot.Groups["temp"].Value)
            {
                halves.Remove(copy.Groups["name"].Value);
                Invalidate(lines[i + 1], halves);
                halves[copy.Groups["name"].Value] = (ballot.Groups["mask"].Value, ballot.Groups["half"].Value[0]);
                i++;
            }
        }

        return replaced;
    }

    private static string Replace(string line, Dictionary<string, (string Mask, char Half)> halves, bool wave32, ref int replaced)
    {
        int count = 0;
        string result = LaneBitPattern().Replace(line, call =>
        {
            if (!halves.TryGetValue(call.Groups["lo"].Value, out var lo) || lo.Half != 'x')
            {
                return call.Value;
            }

            if (wave32 || (halves.TryGetValue(call.Groups["hi"].Value, out var hi) && lo.Mask == hi.Mask && hi.Half == 'y'))
            {
                count++;

                return $"({lo.Mask})";
            }

            return call.Value;
        });
        replaced += count;

        return result;
    }

    /// <summary>Forgets every half the line writes, and every half whose mask bool it writes.</summary>
    private static void Invalidate(string line, Dictionary<string, (string Mask, char Half)> halves)
    {
        foreach (Match write in AssignmentPattern().Matches(line))
        {
            string name = write.Groups["name"].Value;
            halves.Remove(name);

            foreach (var key in halves.Where(pair => pair.Value.Mask == name).Select(pair => pair.Key).ToList())
            {
                halves.Remove(key);
            }
        }
    }

    [GeneratedRegex(@"^\s*uint (?<temp>_t\d+) = vw_ballot\((?<mask>[A-Za-z_]\w*)\)\.(?<half>[xy]);\s*$")]
    private static partial Regex BallotPattern();

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z_]\w*) = (?<value>_t\d+);\s*$")]
    private static partial Regex CopyPattern();

    [GeneratedRegex(@"\blane_bit\((?<lo>[A-Za-z_]\w*), (?<hi>\w+)\)")]
    private static partial Regex LaneBitPattern();

    [GeneratedRegex(@"(?<![.\w])(?<name>[A-Za-z_]\w*)\s*(?:[-+*/&|^]|<<|>>)?=(?!=)")]
    private static partial Regex AssignmentPattern();
}
