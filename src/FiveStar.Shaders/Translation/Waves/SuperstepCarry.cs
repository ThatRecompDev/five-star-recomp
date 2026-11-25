// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using FiveStar.Shaders.Translation.Core;

namespace FiveStar.Shaders.Translation.Waves;

/// <summary>
/// Liveness-aware carry for the superstep form (<see cref="TranslationOptions.SuperstepCarry"/>). Its dispatch switch has
/// thousands of cases, and every register live into any of them made the compiler join it after the switch and at the
/// loop header: a phi per register with an operand per case. Here registers pass between cases through carry slots
/// instead, one slot shared by registers never live at the same case boundary: a case loads the slots of the registers
/// it reads before writing and stores the registers it touches that a later case reads. Every register is then dead at
/// the loop header, so only the slots are joined there.
/// </summary>
internal sealed partial class SuperstepCarry
{
    private const string HexPrefix = "0x";

    private readonly List<string> _names;
    private readonly Dictionary<string, int> _index;
    private readonly Dictionary<string, string> _types;
    private readonly Dictionary<int, string> _slots = [];
    private readonly List<string> _declarations = [];
    private readonly List<string> _entry = [];

    /// <param name="registers">The function-scope registers and their HLSL types.</param>
    /// <param name="pinned">Registers read after the dispatch loop: they stay as they are.</param>
    private SuperstepCarry(IReadOnlyDictionary<string, string> registers, IReadOnlySet<string> pinned)
    {
        _names = [.. registers.Keys.Where(name => !pinned.Contains(name)).Order(StringComparer.Ordinal)];
        _index = _names.Select((name, index) => (name, index)).ToDictionary(pair => pair.name, pair => pair.index);
        _types = registers.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    /// <summary>Slot declarations, before the loop.</summary>
    public IReadOnlyList<string> Declarations => _declarations;

    /// <summary>Stores of the registers the first case reads, after the prologue.</summary>
    public IReadOnlyList<string> Entry => _entry;

    /// <summary>The blocks with their loads and stores.</summary>
    public List<CodeBlock> Blocks { get; } = [];

    /// <summary>The number of registers that pass through slots and the number of slots.</summary>
    public (int Registers, int Slots) Size { get; private set; }

    /// <summary>The carry of <paramref name="blocks"/>; the first case is the block at address 0.</summary>
    public static SuperstepCarry Plan(IReadOnlyList<CodeBlock> blocks, IReadOnlyDictionary<string, string> registers, IReadOnlySet<string> pinned)
    {
        var carry = new SuperstepCarry(registers, pinned);
        carry.Build(blocks);

        return carry;
    }

    private void Build(IReadOnlyList<CodeBlock> blocks)
    {
        var count = _names.Count;
        var facts = blocks.Select(block => Scan(block.Lines, count)).ToList();
        var successors = Successors(blocks);
        var liveIn = facts.Select(_ => new BitArray(count)).ToList();
        var liveOut = facts.Select(_ => new BitArray(count)).ToList();

        // Backward to a fixed point; the blocks are in address order, so walking them in reverse converges quickly.
        for (var changed = true; changed;)
        {
            changed = false;

            for (var block = blocks.Count - 1; block >= 0; block--)
            {
                var output = new BitArray(count);
                successors[block].ForEach(next => output.Or(liveIn[next]));
                var input = new BitArray(output).And(new BitArray(facts[block].Defined).Not()).Or(facts[block].Used);
                changed |= !Same(input, liveIn[block]);
                liveOut[block] = output;
                liveIn[block] = input;
            }
        }

        var entry = blocks.Select((block, index) => (block, index)).FirstOrDefault(pair => pair.block.Address == 0);
        var entryLive = entry.block is null ? new BitArray(count) : liveIn[entry.index];
        var slots = Color([.. liveOut, entryLive]);
        _entry.AddRange(Members(entryLive).Select(register => $"  {Slot(slots[register])} = {_names[register]};"));

        for (var block = 0; block < blocks.Count; block++)
        {
            var touched = facts[block].Touched;
            var lines = Members(new BitArray(liveIn[block]).And(touched)).Select(register => $"  {_names[register]} = {Slot(slots[register])};").ToList();
            lines.AddRange(blocks[block].Lines);
            lines.AddRange(Members(new BitArray(liveOut[block]).And(touched)).Select(register => $"  {Slot(slots[register])} = {_names[register]};"));
            Blocks.Add(blocks[block] with { Lines = lines });
        }
    }

    /// <summary>Upward-exposed reads, certain writes and every mention of the carried registers in one block.</summary>
    private (BitArray Used, BitArray Defined, BitArray Touched) Scan(IReadOnlyList<string> lines, int count)
    {
        var used = new BitArray(count);
        var defined = new BitArray(count);
        var touched = new BitArray(count);
        var depth = 0;

        foreach (var raw in lines)
        {
            var comment = raw.IndexOf("//", StringComparison.Ordinal);
            var line = comment < 0 ? raw : raw[..comment];
            var write = depth == 0 && !Stages.MeshDeclarations.WritesOutput(line) ? WritePattern().Match(line) : Match.Empty;
            var reads = line;

            if (write.Success)
            {
                reads = write.Groups["value"].Value;
            }

            foreach (var register in Mentions(reads))
            {
                touched[register] = true;
                used[register] = used[register] || !defined[register];
            }

            if (write.Success && _index.TryGetValue(write.Groups["name"].Value, out var target))
            {
                touched[target] = true;
                defined[target] = true;
            }

            depth += line.Count(character => character == '{') - line.Count(character => character == '}');
        }

        return (used, defined, touched);
    }

    private IEnumerable<int> Mentions(string text)
    {
        foreach (Match name in NamePattern().Matches(text))
        {
            if (_index.TryGetValue(name.Value, out var register))
            {
                yield return register;
            }
        }
    }

    /// <summary>The cases each block's <c>pc</c> and <c>pc_resume</c> assignments reach; every case for one it cannot read.</summary>
    private static List<List<int>> Successors(IReadOnlyList<CodeBlock> blocks)
    {
        var nodes = blocks.Select((block, index) => (block.Address, index)).ToDictionary(pair => (long)pair.Address, pair => pair.index);
        var all = Enumerable.Range(0, blocks.Count).ToList();
        var result = new List<List<int>>();

        foreach (var block in blocks)
        {
            var targets = new List<int>();

            foreach (Match assignment in JumpPattern().Matches(string.Join("\n", block.Lines)))
            {
                // The condition of a branch is a value; its targets are not.
                var value = assignment.Groups["value"].Value;
                value = value[(value.LastIndexOf('?') + 1)..];

                if (OpaqueTargetPattern().IsMatch(value))
                {
                    targets = all;

                    break;
                }

                foreach (Match address in AddressPattern().Matches(value))
                {
                    if (nodes.TryGetValue(long.Parse(address.Value[HexPrefix.Length..], NumberStyles.HexNumber, CultureInfo.InvariantCulture), out var node))
                    {
                        targets.Add(node);
                    }
                }
            }

            result.Add(targets);
        }

        return result;
    }

    /// <summary>Greedy slots per type: registers live out of the same case (or into the first) get different ones.</summary>
    private int[] Color(IReadOnlyList<BitArray> boundaries)
    {
        var count = _names.Count;
        var neighbours = Enumerable.Range(0, count).Select(_ => new BitArray(count)).ToArray();
        var carried = new BitArray(count);

        foreach (var boundary in boundaries)
        {
            carried.Or(boundary);
            Members(boundary).ForEach(register => neighbours[register].Or(boundary));
        }

        var slots = Enumerable.Repeat(-1, count).ToArray();
        var perType = new Dictionary<string, List<int>>();

        foreach (var register in Members(carried))
        {
            var type = _types[_names[register]];
            var taken = Members(neighbours[register]).Where(other => slots[other] >= 0).Select(other => slots[other]).ToHashSet();

            if (!perType.TryGetValue(type, out var pool))
            {
                pool = [];
                perType[type] = pool;
            }

            var slot = pool.FirstOrDefault(candidate => !taken.Contains(candidate), -1);

            if (slot < 0)
            {
                slot = _slots.Count;
                _slots[slot] = $"ss_c{slot}";
                pool.Add(slot);
                _declarations.Add($"  {type} {_slots[slot]} = ({type})0;");
            }

            slots[register] = slot;
        }

        Size = (Members(carried).Count, _slots.Count);

        return slots;
    }

    private string Slot(int slot)
    {
        return _slots[slot];
    }

    private static List<int> Members(BitArray bits)
    {
        var members = new List<int>();

        for (var index = 0; index < bits.Length; index++)
        {
            if (bits[index])
            {
                members.Add(index);
            }
        }

        return members;
    }

    private static bool Same(BitArray left, BitArray right)
    {
        return !new BitArray(left).Xor(right).Cast<bool>().Any(bit => bit);
    }

    // A whole-line write of one variable, outside any braces of the block: the variable is certainly written.
    [GeneratedRegex(@"^\s*(?<name>[A-Za-z_]\w*)\s*=(?!=)\s*(?<value>[^;{}]*);\s*$")]
    private static partial Regex WritePattern();

    [GeneratedRegex(@"(?<![.\w])[A-Za-z_]\w*")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\bpc(?:_resume)?\s*=(?!=)\s*(?<value>[^;]*);")]
    private static partial Regex JumpPattern();

    [GeneratedRegex(@"0x[0-9a-fA-F]+")]
    private static partial Regex AddressPattern();

    // A target computed from a value, not a block address or a PC_ constant.
    [GeneratedRegex(@"(?<!\w)(?!PC_)[A-Za-z_]\w*")]
    private static partial Regex OpaqueTargetPattern();
}
