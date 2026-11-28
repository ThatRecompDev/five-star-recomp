// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Disassembly;
using FiveStar.Shaders.Translation.Core;

namespace FiveStar.Shaders.Translation;

/// <summary>
/// How far a program reads past each descriptor-table pointer its user SGPRs hold: the end (in bytes) of every
/// <c>s_load</c> whose base pair is an entry SGPR, copied by <c>s_mov_b32</c> or not, and a high dword set to 0 (a 32-bit
/// pointer into the descriptor heap). A table's root-signature ranges can be shorter than what its programs read:
/// VS_PassthroughComposite loads records 28 and 41 (bytes 0x380 and 0x520) of a table whose ranges span 7. A load at an
/// offset in an SGPR reaches the whole table (<see cref="DynamicReach"/>). The scan is
/// linear, so a pointer formed on one branch counts on every path; a larger reach only mirrors more records.
/// </summary>
public static class TableReach
{
    private const int DestinationOperand = 0;
    private const int BaseOperand = 1;
    private const int OffsetOperand = 2;
    private const int LoadOperands = 3;
    private const uint DwordBytes = 4;

    /// <summary>An image sample's T# operand (after the destination and the address).</summary>
    private const int ResourceOperand = 2;
    private const string SamplePrefix = "image_sample";

    /// <summary>Trap temporaries as llvm-objdump prints them (<c>ttmp5</c>, <c>ttmp[4:5]</c>).</summary>
    private const string TrapTemporaryPrefix = "ttmp";

    /// <summary>The reach of a load at an offset the program computes: the game's tables span 0x8000 bytes (its 0x7fe0 offset mask).</summary>
    public const uint DynamicReach = 0x8000;

    /// <summary>Scalar instructions whose first operand is read, not written.</summary>
    private static readonly string[] Readers = ["s_cmp", "s_bitcmp", "s_cbranch", "s_branch", "s_setpc", "s_store", "s_buffer_store", "s_atomic", "s_sendmsg", "s_waitcnt", "s_setreg"];

    /// <summary>Bytes read past each entry SGPR holding a table pointer, by SGPR; empty when none is read so.</summary>
    public static IReadOnlyDictionary<int, uint> Find(IReadOnlyList<Instruction> instructions)
    {
        var reach = new Dictionary<int, uint>();
        var origin = new Dictionary<int, int>();
        var zero = new HashSet<int>();
        var written = new HashSet<int>();

        foreach (var instruction in instructions)
        {
            var (mnemonic, arguments) = Split(instruction);

            if (arguments.Count == 0 || !Writes(mnemonic))
            {
                continue;
            }

            var destination = Parse(arguments[DestinationOperand]);

            if (destination is null || destination.Kind != OperandKind.Scalar)
            {
                continue;
            }

            if (mnemonic.StartsWith("s_load_", StringComparison.Ordinal) && arguments.Count >= LoadOperands)
            {
                Load(arguments, destination, reach, origin, zero, written);
            }

            Track(mnemonic, arguments, destination, origin, zero, written);
        }

        return reach;
    }

    /// <summary>
    /// The records a program samples through: the entry SGPR and byte offset of each T# an <c>image_sample*</c> takes,
    /// loaded at an immediate offset through a table pointer as <see cref="Find"/> follows them. A T# loaded at an offset
    /// the program computes is not listed.
    /// </summary>
    public static IReadOnlyList<(int Sgpr, uint Offset)> Sampled(IReadOnlyList<Instruction> instructions)
    {
        var origin = new Dictionary<int, int>();
        var zero = new HashSet<int>();
        var written = new HashSet<int>();
        var loaded = new Dictionary<int, (int Sgpr, uint Offset)>();
        var sampled = new HashSet<(int Sgpr, uint Offset)>();

        foreach (var instruction in instructions)
        {
            var (mnemonic, arguments) = Split(instruction);

            if (mnemonic.StartsWith(SamplePrefix, StringComparison.Ordinal) && arguments.Count > ResourceOperand)
            {
                var resource = Parse(arguments[ResourceOperand]);

                if (resource is not null && resource.Kind == OperandKind.Scalar && loaded.TryGetValue(resource.Low, out var record))
                {
                    sampled.Add(record);
                }

                continue;
            }

            var destination = arguments.Count == 0 || !Writes(mnemonic) ? null : Parse(arguments[DestinationOperand]);

            if (destination is null || destination.Kind != OperandKind.Scalar)
            {
                continue;
            }

            for (int sgpr = destination.Low; sgpr <= destination.High; sgpr++)
            {
                loaded.Remove(sgpr);
            }

            var at = mnemonic.StartsWith("s_load_", StringComparison.Ordinal) ? Record(arguments, origin, zero, written) : null;

            if (at is not null)
            {
                loaded[destination.Low] = at.Value;
            }

            Track(mnemonic, arguments, destination, origin, zero, written);
        }

        return [.. sampled];
    }

    /// <summary>The entry SGPR and immediate offset an <c>s_load</c> reads a record at; null for any other load.</summary>
    private static (int Sgpr, uint Offset)? Record(List<string> arguments, Dictionary<int, int> origin, HashSet<int> zero, HashSet<int> written)
    {
        if (arguments.Count < LoadOperands)
        {
            return null;
        }

        var pair = Parse(arguments[BaseOperand]);
        var offset = Parse(arguments[OffsetOperand]);

        if (pair is null || offset is null || pair.Kind != OperandKind.Scalar || !zero.Contains(pair.High) || !offset.IsImmediate)
        {
            return null;
        }

        int? entry = Entry(pair.Low, origin, written);

        if (entry is null)
        {
            return null;
        }

        return (entry.Value, offset.Value);
    }

    /// <summary>Counts a load through a 32-bit pointer whose low dword is an entry SGPR.</summary>
    private static void Load(List<string> arguments, Operand destination, Dictionary<int, uint> reach, Dictionary<int, int> origin, HashSet<int> zero, HashSet<int> written)
    {
        var pair = Parse(arguments[BaseOperand]);
        var offset = Parse(arguments[OffsetOperand]);

        if (pair is null || pair.Kind != OperandKind.Scalar || !zero.Contains(pair.High))
        {
            return;
        }

        int? entry = Entry(pair.Low, origin, written);

        if (entry is null || (offset is not null && !(offset.IsImmediate || offset.Kind == OperandKind.Scalar)))
        {
            return;
        }

        // An offset in an SGPR is an index the program computed (TL_PS_PBR_MATERIAL_STANDARD picks a probe's T# at
        // s14 + 32 * index): it may reach anywhere in the table. So may an offset this scan cannot read.
        uint end = DynamicReach;

        if (offset is not null && offset.IsImmediate)
        {
            end = offset.Value + ((uint)(destination.High - destination.Low + 1) * DwordBytes);
        }

        reach[entry.Value] = Math.Max(reach.GetValueOrDefault(entry.Value), end);
    }

    /// <summary>What the destination SGPRs hold after the instruction: a copied entry SGPR, 0, or nothing known.</summary>
    private static void Track(string mnemonic, List<string> arguments, Operand destination, Dictionary<int, int> origin, HashSet<int> zero, HashSet<int> written)
    {
        Operand? source = null;

        if (mnemonic == "s_mov_b32" && arguments.Count > 1)
        {
            source = Parse(arguments[1]);
        }

        int? copied = null;
        bool isZero = false;

        if (source is not null && source.Kind == OperandKind.Scalar && source.Low == source.High)
        {
            copied = Entry(source.Low, origin, written);
        }
        else if (source is not null && source.IsImmediate && source.Value == 0)
        {
            isZero = true;
        }

        for (int sgpr = destination.Low; sgpr <= destination.High; sgpr++)
        {
            written.Add(sgpr);
            origin.Remove(sgpr);
            zero.Remove(sgpr);
        }

        if (copied is not null)
        {
            origin[destination.Low] = copied.Value;
        }

        if (isZero)
        {
            zero.Add(destination.Low);
        }
    }

    /// <summary>The entry SGPR whose value <paramref name="sgpr"/> holds now: itself until written, a copied one, or none.</summary>
    private static int? Entry(int sgpr, Dictionary<int, int> origin, HashSet<int> written)
    {
        if (!written.Contains(sgpr))
        {
            return sgpr;
        }

        if (origin.TryGetValue(sgpr, out int copied))
        {
            return copied;
        }

        return null;
    }

    /// <summary>
    /// The operand, or null for one this scan does not follow: a trap temporary (ttmp0 to ttmp15, the trap handler's
    /// registers, not SGPRs; issue #64 logged the first-chance exception of ttmp5) or anything the parser refuses.
    /// </summary>
    private static Operand? Parse(string text)
    {
        if (text.StartsWith(TrapTemporaryPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return OperandParser.Parse(text);
        }
        catch (Exception exception) when (exception is UnsupportedProgramException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static bool Writes(string mnemonic)
    {
        if (Readers.Any(prefix => mnemonic.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return false;
        }

        return mnemonic.StartsWith("s_", StringComparison.Ordinal) || mnemonic.StartsWith("v_read", StringComparison.Ordinal)
            || mnemonic.StartsWith("v_cmp", StringComparison.Ordinal);
    }

    private static (string Mnemonic, List<string> Arguments) Split(Instruction instruction)
    {
        var line = instruction.Text.Split(';')[0].Split("//")[0].Trim();
        var space = line.IndexOfAny([' ', '\t']);

        if (space < 0)
        {
            return (line, []);
        }

        return (line[..space], ArgumentList.Split(InstructionModifiers.Strip(line[space..].TrimStart())));
    }
}
