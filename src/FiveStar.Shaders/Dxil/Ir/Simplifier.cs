// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Frozen;
using FiveStar.Shaders.Dxil.Ops;

namespace FiveStar.Shaders.Dxil.Ir;

/// <summary>
/// The clean-ups after <see cref="ValueNumbering"/> that change the blocks or touch wave operations. A block that
/// only its predecessor's unconditional branch reaches joins that predecessor (every inlined call and structured
/// region ends in such a branch); a branch on a constant or to one block twice becomes an unconditional branch, a phi
/// of one value that value, and what their operands then decide is folded again (<see cref="ConstantFolding"/>).
/// Wave and quad operations that only read are declared nounwind, as dxc declares them, so nothing removed them: one
/// nothing reads goes, and one with the operands of an earlier one in its block, with no effect between them, is that
/// one (a discard or a barrier between them may change the lanes). Two rounds, as a folded branch frees blocks to merge.
/// </summary>
public static class Simplifier
{
    private const int Rounds = 2;
    private const int MinimumJoins = 2;

    /// <summary>Wave and quad operations whose value depends only on their operands and the lanes that run them.</summary>
    private static readonly FrozenSet<int> WaveReadOperations = new[]
    {
        DxOpCode.WaveIsFirstLane, DxOpCode.WaveAnyTrue, DxOpCode.WaveAllTrue, DxOpCode.WaveActiveAllEqual,
        DxOpCode.WaveActiveBallot, DxOpCode.WaveReadLaneAt, DxOpCode.WaveReadLaneFirst, DxOpCode.WaveActiveOp,
        DxOpCode.WaveActiveBit, DxOpCode.WavePrefixOp, DxOpCode.WaveAllBitCount, DxOpCode.WavePrefixBitCount,
        DxOpCode.QuadReadLaneAt, DxOpCode.QuadOp,
    }.Select(code => (int)code).ToFrozenSet();

    /// <summary>Simplifies <paramref name="function"/>, numbered and cleaned; cleans it again after each round.</summary>
    public static void Run(IrFunction function, DxilModule module)
    {
        for (var round = 0; round < Rounds && function.Blocks.Count > 0; round++)
        {
            BranchFacts.Apply(function, module);
            var replaced = new Dictionary<Value, Value>();
            MergeChains(function, replaced);
            var tree = DominatorTree.Of(function);
            var pending = new Stack<BasicBlock>();
            pending.Push(function.Blocks[0]);

            // Dominator-tree preorder: an instruction's operands other than phis are folded before it.
            while (pending.Count > 0)
            {
                var block = pending.Pop();
                Simplify(block, module, replaced);

                foreach (var child in tree.Children(block))
                {
                    pending.Push(child);
                }
            }

            foreach (var block in function.Blocks)
            {
                block.Phis.ForEach(phi => Resolve(phi, replaced));
                block.Body.ForEach(instruction => Resolve(instruction, replaced));
                Resolve(block.Terminator!, replaced);
            }

            RemoveUnreadWaveReads(function);
            DeadCode.Remove(function);
        }
    }

    /// <summary>Appends each block reached only by its predecessor's unconditional branch to that predecessor.</summary>
    private static void MergeChains(IrFunction function, Dictionary<Value, Value> replaced)
    {
        var entry = function.Blocks[0];
        var edges = new Dictionary<BasicBlock, int>();

        foreach (var successor in function.Blocks.SelectMany(block => block.Successors))
        {
            edges[successor] = edges.GetValueOrDefault(successor) + 1;
        }

        var merged = new HashSet<BasicBlock>();

        foreach (var block in function.Blocks)
        {
            if (merged.Contains(block))
            {
                continue;
            }

            while (block.Terminator!.Opcode == Opcode.Branch)
            {
                var next = block.Terminator.Targets[0];

                if (ReferenceEquals(next, block) || ReferenceEquals(next, entry) || edges[next] != 1)
                {
                    break;
                }

                Absorb(block, next, replaced);
                merged.Add(next);
            }
        }

        function.Blocks.RemoveAll(merged.Contains);
    }

    private static void Absorb(BasicBlock block, BasicBlock next, Dictionary<Value, Value> replaced)
    {
        // One incoming edge: every phi has the one value from this block.
        foreach (var phi in next.Phis)
        {
            replaced[phi] = phi.Operands[0];
        }

        foreach (var instruction in next.Body)
        {
            instruction.Block = block;
            block.Body.Add(instruction);
        }

        var terminator = next.Terminator!;
        terminator.Block = block;
        block.Terminator = terminator;

        foreach (var phi in terminator.Targets.Distinct().SelectMany(successor => successor.Phis))
        {
            for (var index = 0; index < phi.Targets.Count; index++)
            {
                if (ReferenceEquals(phi.Targets[index], next))
                {
                    phi.Targets[index] = block;
                }
            }
        }
    }

    /// <summary>Phis of one value, folds, wave reads the block already made, then the block's branch.</summary>
    private static void Simplify(BasicBlock block, DxilModule module, Dictionary<Value, Value> replaced)
    {
        foreach (var phi in block.Phis)
        {
            Resolve(phi, replaced);
            var single = SingleValue(phi);

            if (single is not null)
            {
                replaced[phi] = single;
            }
        }

        MergeJoins(block, replaced);
        var waveReads = new Dictionary<ValueNumbering.Key, Value>();

        block.Body.RemoveAll(instruction => Replace(instruction, module, replaced, waveReads));
        Resolve(block.Terminator!, replaced);
        FoldBranch(block, module);
    }

    /// <summary>
    /// Two phis of a block that take the same values from the same blocks are one (VIctory's fork, opt.rs
    /// Module::merge): the translator's registers often hold one value along every path into a join.
    /// </summary>
    private static void MergeJoins(BasicBlock block, Dictionary<Value, Value> replaced)
    {
        if (block.Phis.Count < MinimumJoins)
        {
            return;
        }

        var joins = new Dictionary<int, List<Instruction>>();

        foreach (var phi in block.Phis.Where(phi => !replaced.ContainsKey(phi)))
        {
            // Order-free: the same pairs in any order hash alike; SameIncoming decides.
            var key = HashCode.Combine(phi.Type, phi.Operands.Count);

            for (var index = 0; index < phi.Operands.Count; index++)
            {
                key += HashCode.Combine(phi.Targets[index], phi.Operands[index]);
            }

            if (!joins.TryGetValue(key, out var candidates))
            {
                joins[key] = [phi];

                continue;
            }

            var first = candidates.FirstOrDefault(candidate => ReferenceEquals(candidate.Type, phi.Type) && SameIncoming(candidate, phi));

            if (first is not null)
            {
                replaced[phi] = first;

                continue;
            }

            candidates.Add(phi);
        }
    }

    /// <summary>Whether two phis take the same value from each block (the hash key aside).</summary>
    private static bool SameIncoming(Instruction first, Instruction second)
    {
        if (first.Operands.Count != second.Operands.Count)
        {
            return false;
        }

        for (var index = 0; index < second.Operands.Count; index++)
        {
            var position = first.Targets.IndexOf(second.Targets[index]);

            if (position < 0 || !ReferenceEquals(first.Operands[position], second.Operands[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when the instruction is replaced by a constant, an operand or an earlier wave read.</summary>
    private static bool Replace(Instruction instruction, DxilModule module, Dictionary<Value, Value> replaced, Dictionary<ValueNumbering.Key, Value> waveReads)
    {
        Resolve(instruction, replaced);
        var folded = Fold(instruction, module);

        if (folded is not null)
        {
            replaced[instruction] = folded;

            return true;
        }

        if (!IsWaveRead(instruction))
        {
            if (instruction.HasEffect)
            {
                waveReads.Clear();
            }

            return false;
        }

        var key = new ValueNumbering.Key(instruction);

        if (waveReads.TryGetValue(key, out var earlier))
        {
            replaced[instruction] = earlier;

            return true;
        }

        waveReads[key] = instruction;

        return false;
    }

    /// <summary>The value <see cref="ConstantFolding"/> gives the instruction now that its operands are resolved, or null.</summary>
    private static Value? Fold(Instruction instruction, DxilModule module)
    {
        var operands = instruction.Operands;

        switch (instruction.Opcode)
        {
            case Opcode.Binary:
                return ConstantFolding.Binary(module, (BinaryOp)instruction.Code, operands[0], operands[1]);
            case Opcode.Compare:
                return ConstantFolding.Compare(module, (Predicate)instruction.Code, operands[0], operands[1]) ?? WidenedBit(instruction);
            case Opcode.Cast:
                return ConstantFolding.Cast(module, (CastOp)instruction.Code, operands[0], instruction.Type) ?? CastBack(instruction);
            case Opcode.Select:
                return ConstantFolding.Select(operands[0], operands[1], operands[^1]) ?? SelectedBit(instruction);
            default:
                return null;
        }
    }

    // The folds below follow VIctory (fork https://github.com/fxceless/victory-fork,
    // crates/victory-shader/src/emit/bitcode/opt.rs, Module::simplify, commit 1684989): Copyright (c) the VIctory
    // contributors, GPL-3.0-or-later. See THIRD_PARTY_NOTICES.md.

    /// <summary>A bit widened and compared with 0 again (<c>zext i1 x != 0</c>) is the bit.</summary>
    private static Value? WidenedBit(Instruction compare)
    {
        var operands = compare.Operands;

        var widen = Of(operands[0], Opcode.Cast);

        if ((Predicate)compare.Code != Predicate.NotEqual || !IsInteger(operands[1], 0) || widen is null || (CastOp)widen.Code != CastOp.ZExt
            || !ReferenceEquals(widen.Operands[0].Type, compare.Type))
        {
            return null;
        }

        return widen.Operands[0];
    }

    /// <summary>
    /// A bitcast to its own type, a bitcast there and back, and a value widened and narrowed again are the value they
    /// started from (the bits are the same).
    /// </summary>
    private static Value? CastBack(Instruction cast)
    {
        var value = cast.Operands[0];
        var op = (CastOp)cast.Code;

        if (op == CastOp.Bitcast && ReferenceEquals(value.Type, cast.Type))
        {
            return value;
        }

        var inner = Of(value, Opcode.Cast);

        if (inner is null || !ReferenceEquals(inner.Operands[0].Type, cast.Type))
        {
            return null;
        }

        var first = (CastOp)inner.Code;

        if ((op == CastOp.Bitcast && first == CastOp.Bitcast) || (op == CastOp.Trunc && first is CastOp.ZExt or CastOp.SExt))
        {
            return inner.Operands[0];
        }

        return null;
    }

    /// <summary>
    /// <c>select c, true, false</c> of bits is <c>c</c>; and under the same condition an inner select is one of its
    /// operands (two guarded writes of one register in a row), which may leave the select of one value.
    /// </summary>
    private static Value? SelectedBit(Instruction select)
    {
        var operands = select.Operands;
        var condition = operands[0];

        for (var inner = Of(operands[1], Opcode.Select); inner is not null && ReferenceEquals(inner.Operands[0], condition); inner = Of(operands[1], Opcode.Select))
        {
            operands[1] = inner.Operands[1];
        }

        for (var inner = Of(operands[^1], Opcode.Select); inner is not null && ReferenceEquals(inner.Operands[0], condition); inner = Of(operands[^1], Opcode.Select))
        {
            operands[^1] = inner.Operands[^1];
        }

        if (ReferenceEquals(operands[1], operands[^1]))
        {
            return operands[1];
        }

        if (select.Type.Bits == 1 && IsInteger(operands[1], 1) && IsInteger(operands[^1], 0))
        {
            return condition;
        }

        return null;
    }

    /// <summary>The value as an instruction of <paramref name="opcode"/>, or null.</summary>
    private static Instruction? Of(Value value, Opcode opcode)
    {
        var instruction = value as Instruction;

        if (instruction is null || instruction.Opcode != opcode)
        {
            return null;
        }

        return instruction;
    }

    /// <summary>Whether the value is the integer constant <paramref name="bits"/> (a set bit is 1 or -1).</summary>
    private static bool IsInteger(Value value, long bits)
    {
        var constant = value as Constant;

        return constant is not null && constant.IsInteger && (constant.Bits == bits || (bits == 1 && constant.Type.Bits == 1 && constant.Bits != 0));
    }

    private static bool IsWaveRead(Instruction instruction)
    {
        var operands = instruction.Operands;

        if (instruction.Opcode != Opcode.Call || operands.Count == 0 || operands[0] is not Constant code || !code.IsInteger)
        {
            return false;
        }

        return WaveReadOperations.Contains((int)code.Bits);
    }

    /// <summary>Removes the wave reads nothing with an effect depends on; <see cref="DeadCode"/> then removes their operands.</summary>
    private static void RemoveUnreadWaveReads(IrFunction function)
    {
        var work = new Stack<Instruction>();

        foreach (var block in function.Blocks)
        {
            foreach (var instruction in block.Body.Where(instruction => instruction.HasEffect && !IsWaveRead(instruction)))
            {
                MarkLive(instruction, work);
            }

            MarkLive(block.Terminator!, work);
        }

        while (work.Count > 0)
        {
            foreach (var operand in work.Pop().Operands)
            {
                if (operand is Instruction instruction)
                {
                    MarkLive(instruction, work);
                }
            }
        }

        foreach (var block in function.Blocks)
        {
            block.Body.RemoveAll(instruction => IsWaveRead(instruction) && !instruction.Live);
            block.Phis.ForEach(phi => phi.Live = false);
            block.Body.ForEach(instruction => instruction.Live = false);
            block.Terminator!.Live = false;
        }
    }

    private static void MarkLive(Instruction instruction, Stack<Instruction> work)
    {
        if (!instruction.Live)
        {
            instruction.Live = true;
            work.Push(instruction);
        }
    }

    /// <summary>The one value every incoming edge of <paramref name="phi"/> brings (the phi itself aside), or null.</summary>
    private static Value? SingleValue(Instruction phi)
    {
        Value? single = null;

        foreach (var operand in phi.Operands)
        {
            if (ReferenceEquals(operand, phi) || ReferenceEquals(operand, single))
            {
                continue;
            }

            if (single is not null)
            {
                return null;
            }

            single = operand;
        }

        return single;
    }

    /// <summary>A conditional branch on a constant, or to one block twice, becomes a branch; the dropped edge leaves the phis.</summary>
    private static void FoldBranch(BasicBlock block, DxilModule module)
    {
        var terminator = block.Terminator!;

        if (terminator.Opcode != Opcode.ConditionalBranch)
        {
            return;
        }

        var whenTrue = terminator.Targets[0];
        var whenFalse = terminator.Targets[1];
        var constant = terminator.Operands[0] as Constant;
        var known = constant is not null && constant.IsInteger;

        if (!known && !ReferenceEquals(whenTrue, whenFalse))
        {
            return;
        }

        var kept = whenTrue;
        var dropped = whenFalse;

        if (known && constant!.Bits == 0)
        {
            kept = whenFalse;
            dropped = whenTrue;
        }

        foreach (var phi in dropped.Phis)
        {
            var index = phi.Targets.IndexOf(block);
            phi.Operands.RemoveAt(index);
            phi.Targets.RemoveAt(index);
        }

        var branch = new Instruction(Opcode.Branch, module.Types.Void, block);
        branch.Targets.Add(kept);
        block.Terminator = branch;
    }

    private static void Resolve(Instruction instruction, Dictionary<Value, Value> replaced)
    {
        var operands = instruction.Operands;

        for (var index = 0; index < operands.Count; index++)
        {
            var value = operands[index];

            while (replaced.TryGetValue(value, out var next))
            {
                value = next;
            }

            operands[index] = value;
        }
    }
}
