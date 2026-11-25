// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Frozen;
using FiveStar.Shaders.Dxil.Ops;

namespace FiveStar.Shaders.Dxil.Ir;

/// <summary>
/// Turns small branches that only compute into selects, as LLVM's SimplifyCFG does (FoldTwoEntryPHINode): a block that
/// branches to one or two arms, each reached only from it and running on to a common join, takes the arms' instructions
/// and branches to the join, and each phi there selects on the branch condition between the values the two paths
/// bring. An arm qualifies only when every instruction is safe to run on lanes that skipped it: arithmetic (no integer
/// division), compares, casts, selects, extracts and the dx.op math functions. No load, wave, quad or derivative
/// operation moves, so results are the same bit for bit. The driver's compile memory follows the number of blocks.
/// </summary>
public static class IfConversion
{
    /// <summary>The most instructions an arm may hold (LLVM speculates about four; GPU branches cost more).</summary>
    public const int ArmLimit = 8;

    /// <summary>The most phis a join may select (each costs a select on every lane).</summary>
    public const int PhiLimit = 8;

    /// <summary>The dx.op functions that read only their operands and the same on every lane.</summary>
    private static readonly FrozenSet<int> MathOperations = new[]
    {
        DxOpCode.FAbs, DxOpCode.Saturate, DxOpCode.IsNaN, DxOpCode.IsInf, DxOpCode.IsFinite, DxOpCode.Cos, DxOpCode.Sin,
        DxOpCode.Tan, DxOpCode.Acos, DxOpCode.Asin, DxOpCode.Atan, DxOpCode.Exp, DxOpCode.Frc, DxOpCode.Log, DxOpCode.Sqrt,
        DxOpCode.Rsqrt, DxOpCode.RoundNe, DxOpCode.RoundNi, DxOpCode.RoundPi, DxOpCode.RoundZ, DxOpCode.Bfrev,
        DxOpCode.Countbits, DxOpCode.FirstbitLo, DxOpCode.FirstbitHi, DxOpCode.FirstbitSHi, DxOpCode.FMax, DxOpCode.FMin,
        DxOpCode.IMax, DxOpCode.IMin, DxOpCode.UMax, DxOpCode.UMin, DxOpCode.FMad, DxOpCode.Fma, DxOpCode.IMad,
        DxOpCode.UMad, DxOpCode.Ibfe, DxOpCode.Ubfe, DxOpCode.Bfi, DxOpCode.MakeDouble, DxOpCode.SplitDouble,
        DxOpCode.LegacyF32ToF16, DxOpCode.LegacyF16ToF32,
    }.Select(code => (int)code).ToFrozenSet();

    /// <summary>Converts every qualifying branch of <paramref name="function"/>; how many it converted.</summary>
    public static int Run(IrFunction function, DxilModule module)
    {
        var predecessors = Predecessors(function);
        var removed = new HashSet<BasicBlock>();
        var converted = 0;

        foreach (var head in function.Blocks)
        {
            if (!removed.Contains(head) && Convert(head, predecessors, removed, module))
            {
                converted++;
            }
        }

        function.Blocks.RemoveAll(removed.Contains);

        return converted;
    }

    private static bool Convert(BasicBlock head, Dictionary<BasicBlock, List<BasicBlock>> predecessors, HashSet<BasicBlock> removed, DxilModule module)
    {
        var terminator = head.Terminator!;

        if (terminator.Opcode != Opcode.ConditionalBranch || ReferenceEquals(terminator.Targets[0], terminator.Targets[1]))
        {
            return false;
        }

        var (whenTrue, whenFalse) = (terminator.Targets[0], terminator.Targets[1]);
        var trueArm = IsArm(whenTrue, head, predecessors);
        var falseArm = IsArm(whenFalse, head, predecessors);
        BasicBlock join;

        if (trueArm && falseArm && ReferenceEquals(Next(whenTrue), Next(whenFalse)))
        {
            join = Next(whenTrue);
        }
        else if (trueArm && ReferenceEquals(Next(whenTrue), whenFalse))
        {
            (join, falseArm) = (whenFalse, false);
        }
        else if (falseArm && ReferenceEquals(Next(whenFalse), whenTrue))
        {
            (join, trueArm) = (whenTrue, false);
        }
        else
        {
            return false;
        }

        // A diamond's join reached from the head too, or a loop back to the head, stays a branch.
        if (ReferenceEquals(join, head) || (trueArm && falseArm && predecessors[join].Contains(head)) || join.Phis.Count > PhiLimit
            || join.Phis.Any(phi => !IsSelectable(phi.Type)))
        {
            return false;
        }

        var condition = terminator.Operands[0];
        var truePath = trueArm ? whenTrue : head;
        var falsePath = falseArm ? whenFalse : head;

        var arms = new List<BasicBlock>();

        if (trueArm)
        {
            arms.Add(whenTrue);
        }

        if (falseArm)
        {
            arms.Add(whenFalse);
        }

        foreach (var arm in arms)
        {
            foreach (var instruction in arm.Body)
            {
                instruction.Block = head;
                head.Body.Add(instruction);
            }

            arm.Body.Clear();
            removed.Add(arm);
            predecessors[join].Remove(arm);
        }

        foreach (var phi in join.Phis)
        {
            var valueTrue = Incoming(phi, truePath);
            var valueFalse = Incoming(phi, falsePath);
            var value = valueTrue;

            if (!ReferenceEquals(valueTrue, valueFalse))
            {
                var select = new Instruction(Opcode.Select, phi.Type, head, 3);
                select.Operands.AddRange([condition, valueTrue, valueFalse]);
                head.Body.Add(select);
                value = select;
            }

            RemoveIncoming(phi, truePath);
            RemoveIncoming(phi, falsePath);
            phi.Operands.Add(value);
            phi.Targets.Add(head);
        }

        var branch = new Instruction(Opcode.Branch, module.Types.Void, head);
        branch.Targets.Add(join);
        head.Terminator = branch;

        if (!predecessors[join].Contains(head))
        {
            predecessors[join].Add(head);
        }

        return true;
    }

    /// <summary>
    /// A block only <paramref name="head"/> reaches, without phis, that runs on with an unconditional branch elsewhere and
    /// holds at most <see cref="ArmLimit"/> instructions, each safe to run on every lane.
    /// </summary>
    private static bool IsArm(BasicBlock block, BasicBlock head, Dictionary<BasicBlock, List<BasicBlock>> predecessors)
    {
        var sources = predecessors[block];

        return !ReferenceEquals(block, head) && sources.Count == 1 && ReferenceEquals(sources[0], head) && block.Phis.Count == 0
            && block.Terminator!.Opcode == Opcode.Branch && !ReferenceEquals(Next(block), block) && block.Body.Count <= ArmLimit
            && block.Body.All(IsSpeculatable);
    }

    private static BasicBlock Next(BasicBlock block)
    {
        return block.Terminator!.Targets[0];
    }

    private static bool IsSpeculatable(Instruction instruction)
    {
        switch (instruction.Opcode)
        {
            case Opcode.Binary:
                // Integer division and remainder by zero are undefined; the floating-point forms are not.
                return instruction.Type.IsFloatingPoint || (BinaryOp)instruction.Code is not (BinaryOp.UDiv or BinaryOp.SDiv or BinaryOp.URem or BinaryOp.SRem);
            case Opcode.Cast:
            case Opcode.Compare:
            case Opcode.Select:
            case Opcode.ExtractValue:
                return true;
            case Opcode.Call:
                return instruction.Callee!.Attribute == AttributeSet.ReadNone && instruction.Operands.Count > 0
                    && instruction.Operands[0] is Constant { IsInteger: true } code && MathOperations.Contains((int)code.Bits);
            default:
                return false;
        }
    }

    /// <summary>Scalar values only: DXIL keeps handles and other aggregates out of selects.</summary>
    private static bool IsSelectable(IrType type)
    {
        return type.IsInteger || type.IsFloatingPoint;
    }

    private static Value Incoming(Instruction phi, BasicBlock source)
    {
        return phi.Operands[phi.Targets.IndexOf(source)];
    }

    private static void RemoveIncoming(Instruction phi, BasicBlock source)
    {
        var index = phi.Targets.IndexOf(source);
        phi.Operands.RemoveAt(index);
        phi.Targets.RemoveAt(index);
    }

    private static Dictionary<BasicBlock, List<BasicBlock>> Predecessors(IrFunction function)
    {
        var predecessors = function.Blocks.ToDictionary(block => block, _ => new List<BasicBlock>());

        foreach (var block in function.Blocks)
        {
            foreach (var successor in block.Successors.Distinct())
            {
                predecessors[successor].Add(block);
            }
        }

        return predecessors;
    }
}
