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

    private const int SelectOperands = 3;

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
        var shape = Shape(head, predecessors);

        if (shape is null)
        {
            return false;
        }

        var (join, trueArm, falseArm) = shape.Value;
        var terminator = head.Terminator!;
        var (whenTrue, whenFalse) = (terminator.Targets[0], terminator.Targets[1]);
        var truePath = trueArm ? whenTrue : head;
        var falsePath = falseArm ? whenFalse : head;

        if (trueArm)
        {
            Hoist(whenTrue, head, join, predecessors, removed);
        }

        if (falseArm)
        {
            Hoist(whenFalse, head, join, predecessors, removed);
        }

        foreach (var phi in join.Phis)
        {
            Select(phi, head, terminator.Operands[0], truePath, falsePath);
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
    /// The join of a branch of <paramref name="head"/> on two arms (a diamond) or one (a triangle), and which targets are
    /// arms; null when the branch has neither shape, loops back to the head, or its join's phis cannot all be selected.
    /// </summary>
    private static (BasicBlock Join, bool TrueArm, bool FalseArm)? Shape(BasicBlock head, Dictionary<BasicBlock, List<BasicBlock>> predecessors)
    {
        var terminator = head.Terminator!;

        if (terminator.Opcode != Opcode.ConditionalBranch || ReferenceEquals(terminator.Targets[0], terminator.Targets[1]))
        {
            return null;
        }

        var (whenTrue, whenFalse) = (terminator.Targets[0], terminator.Targets[1]);
        var trueArm = IsArm(whenTrue, head, predecessors);
        var falseArm = IsArm(whenFalse, head, predecessors);
        (BasicBlock Join, bool TrueArm, bool FalseArm) shape;

        if (trueArm && falseArm && ReferenceEquals(Next(whenTrue), Next(whenFalse)) && !predecessors[Next(whenTrue)].Contains(head))
        {
            shape = (Next(whenTrue), true, true);
        }
        else if (trueArm && ReferenceEquals(Next(whenTrue), whenFalse))
        {
            shape = (whenFalse, true, false);
        }
        else if (falseArm && ReferenceEquals(Next(whenFalse), whenTrue))
        {
            shape = (whenTrue, false, true);
        }
        else
        {
            return null;
        }

        var join = shape.Join;

        if (ReferenceEquals(join, head) || join.Phis.Count > PhiLimit || join.Phis.Any(phi => !IsSelectable(phi.Type)))
        {
            return null;
        }

        return shape;
    }

    /// <summary>Moves an arm's instructions to the end of <paramref name="head"/> and drops the arm.</summary>
    private static void Hoist(BasicBlock arm, BasicBlock head, BasicBlock join, Dictionary<BasicBlock, List<BasicBlock>> predecessors, HashSet<BasicBlock> removed)
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

    /// <summary>The phi's values along the two paths become one from <paramref name="head"/>: a select on the branch condition.</summary>
    private static void Select(Instruction phi, BasicBlock head, Value condition, BasicBlock truePath, BasicBlock falsePath)
    {
        var valueTrue = Incoming(phi, truePath);
        var valueFalse = Incoming(phi, falsePath);
        var value = valueTrue;

        if (!ReferenceEquals(valueTrue, valueFalse))
        {
            var select = new Instruction(Opcode.Select, phi.Type, head, SelectOperands);
            select.Operands.AddRange([condition, valueTrue, valueFalse]);
            head.Body.Add(select);
            value = select;
        }

        RemoveIncoming(phi, truePath);
        RemoveIncoming(phi, falsePath);
        phi.Operands.Add(value);
        phi.Targets.Add(head);
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
                return instruction.Callee!.Attribute == AttributeSet.ReadNone && instruction.Operands.Count > 0 && IsMath(instruction.Operands[0]);
            default:
                return false;
        }
    }

    /// <summary>A dx.op call's opcode operand naming one of <see cref="MathOperations"/>.</summary>
    private static bool IsMath(Value opcode)
    {
        var constant = opcode as Constant;

        return constant is not null && constant.IsInteger && MathOperations.Contains((int)constant.Bits);
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
