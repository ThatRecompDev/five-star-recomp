// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

// Follows VIctory (fork https://github.com/fxceless/victory-fork, crates/victory-shader/src/emit/bitcode/opt.rs, the
// FACTS pass: Module::learn and Module::merge, commit 1684989): Copyright (c) the VIctory contributors,
// GPL-3.0-or-later. See THIRD_PARTY_NOTICES.md.

namespace FiveStar.Shaders.Dxil.Ir;

/// <summary>
/// What a branch taken says about its condition: in a block reached only from one side of a conditional branch, and in
/// every block it dominates, the condition is that side's constant, and so is what it was made of (both terms of an
/// <c>and</c> that held, neither of an <c>or</c> that did not, the bit an <c>xor</c> with a constant flips, the
/// opposite comparison of the same operands). The translator branches on a mask or SCC and then guards each statement
/// behind the branch with the same mask; <see cref="Simplifier"/> folds what the constants decide.
/// </summary>
internal static class BranchFacts
{
    // How far a condition is followed into what it was made of.
    private const int Depth = 8;
    private const int CompareOperands = 2;

    /// <summary>Replaces the known conditions in <paramref name="function"/>; whether anything changed.</summary>
    public static bool Apply(IrFunction function, DxilModule module)
    {
        if (function.Blocks.Count == 0)
        {
            return false;
        }

        return new Walk(module).Run(function);
    }

    /// <summary>The comparison that holds exactly when <paramref name="predicate"/> does not.</summary>
    private static Predicate Opposite(Predicate predicate)
    {
        const int FloatInverse = 15;

        return predicate switch
        {
            <= Predicate.FloatTrue => (Predicate)(FloatInverse - (int)predicate),
            Predicate.Equal => Predicate.NotEqual,
            Predicate.NotEqual => Predicate.Equal,
            Predicate.UnsignedGreater => Predicate.UnsignedLessOrEqual,
            Predicate.UnsignedLessOrEqual => Predicate.UnsignedGreater,
            Predicate.UnsignedGreaterOrEqual => Predicate.UnsignedLess,
            Predicate.UnsignedLess => Predicate.UnsignedGreaterOrEqual,
            Predicate.SignedGreater => Predicate.SignedLessOrEqual,
            Predicate.SignedLessOrEqual => Predicate.SignedGreater,
            Predicate.SignedGreaterOrEqual => Predicate.SignedLess,
            _ => Predicate.SignedGreaterOrEqual,
        };
    }

    private sealed class Walk
    {
        private readonly DxilModule _module;
        private readonly Dictionary<Value, bool> _bits = [];
        private readonly Dictionary<(Predicate Predicate, Value Left, Value Right), bool> _compares = [];
        private readonly Dictionary<Value, Value> _replaced = [];
        private bool _changed;

        public Walk(DxilModule module)
        {
            _module = module;
        }

        public bool Run(IrFunction function)
        {
            var tree = DominatorTree.Of(function);
            var predecessors = Predecessors(function);
            var steps = new Stack<(BasicBlock Block, Learned? Leave)>();
            steps.Push((function.Blocks[0], null));

            while (steps.Count > 0)
            {
                var (block, leave) = steps.Pop();

                if (leave is not null)
                {
                    leave.Bits.ForEach(value => _bits.Remove(value));
                    leave.Compares.ForEach(key => _compares.Remove(key));

                    continue;
                }

                var learned = new Learned();

                if (predecessors.TryGetValue(block, out var from) && from.Count == 1)
                {
                    var branch = from[0].Terminator!;

                    if (branch.Opcode == Opcode.ConditionalBranch && !ReferenceEquals(branch.Targets[0], branch.Targets[1]))
                    {
                        Learn(Resolve(branch.Operands[0]), ReferenceEquals(block, branch.Targets[0]), learned, Depth);
                    }
                }

                Visit(block);
                steps.Push((block, learned));

                foreach (var child in tree.Children(block))
                {
                    steps.Push((child, null));
                }
            }

            if (_replaced.Count > 0)
            {
                foreach (var block in function.Blocks)
                {
                    block.Phis.ForEach(Rewrite);
                    block.Body.ForEach(Rewrite);
                    Rewrite(block.Terminator!);
                }
            }

            return _changed;
        }

        /// <summary>Gives each statement of the block what is known of its operands; a comparison known whole is that constant.</summary>
        private void Visit(BasicBlock block)
        {
            foreach (var instruction in block.Body)
            {
                Substitute(instruction);

                if (instruction.Opcode == Opcode.Compare && instruction.Operands.Count == CompareOperands
                    && _compares.TryGetValue(((Predicate)instruction.Code, instruction.Operands[0], instruction.Operands[1]), out var holds))
                {
                    // Every reader of the comparison is in a block it dominates, where the fact holds too.
                    _replaced[instruction] = _module.Bool(holds);
                    _changed = true;
                }
            }

            Substitute(block.Terminator!);
        }

        private void Substitute(Instruction instruction)
        {
            var operands = instruction.Operands;

            for (var index = 0; index < operands.Count; index++)
            {
                var value = Resolve(operands[index]);

                if (_bits.TryGetValue(value, out var known))
                {
                    value = _module.Bool(known);
                    _changed = true;
                }

                operands[index] = value;
            }
        }

        /// <summary>Notes that the bit <paramref name="value"/> is <paramref name="holds"/> here, and what follows for what it was made of.</summary>
        private void Learn(Value value, bool holds, Learned learned, int depth)
        {
            if (value is not Instruction instruction || value.Type.Bits != 1 || _bits.ContainsKey(value))
            {
                return;
            }

            _bits[value] = holds;
            learned.Bits.Add(value);

            if (depth == 0)
            {
                return;
            }

            var operands = instruction.Operands;

            switch (instruction.Opcode)
            {
                case Opcode.Binary when (BinaryOp)instruction.Code == BinaryOp.And && holds:
                    Learn(Resolve(operands[0]), true, learned, depth - 1);
                    Learn(Resolve(operands[1]), true, learned, depth - 1);
                    break;
                case Opcode.Binary when (BinaryOp)instruction.Code == BinaryOp.Or && !holds:
                    Learn(Resolve(operands[0]), false, learned, depth - 1);
                    Learn(Resolve(operands[1]), false, learned, depth - 1);
                    break;
                case Opcode.Binary when (BinaryOp)instruction.Code == BinaryOp.Xor:
                    LearnFlipped(Resolve(operands[0]), Resolve(operands[1]), holds, learned, depth - 1);
                    break;
                case Opcode.Compare when operands.Count == CompareOperands:
                    LearnCompare((Predicate)instruction.Code, Resolve(operands[0]), Resolve(operands[1]), holds, learned);
                    break;
            }
        }

        /// <summary><c>x ^ k</c> of a constant bit k: x is the condition, flipped when k is set.</summary>
        private void LearnFlipped(Value left, Value right, bool holds, Learned learned, int depth)
        {
            var (constant, other) = (right as Constant, left);

            if (constant is null)
            {
                (constant, other) = (left as Constant, right);
            }

            if (constant is not null && constant.IsInteger && other is not Constant)
            {
                Learn(other, holds != (constant.Bits != 0), learned, depth);
            }
        }

        private void LearnCompare(Predicate predicate, Value left, Value right, bool holds, Learned learned)
        {
            foreach (var (key, value) in new[] { ((predicate, left, right), holds), ((Opposite(predicate), left, right), !holds) })
            {
                if (_compares.TryAdd(key, value))
                {
                    learned.Compares.Add(key);
                }
            }
        }

        private Value Resolve(Value value)
        {
            while (_replaced.TryGetValue(value, out var next))
            {
                value = next;
            }

            return value;
        }

        private void Rewrite(Instruction instruction)
        {
            var operands = instruction.Operands;

            for (var index = 0; index < operands.Count; index++)
            {
                operands[index] = Resolve(operands[index]);
            }
        }

        private static Dictionary<BasicBlock, List<BasicBlock>> Predecessors(IrFunction function)
        {
            var predecessors = new Dictionary<BasicBlock, List<BasicBlock>>();

            foreach (var block in function.Blocks)
            {
                foreach (var successor in block.Successors.Distinct())
                {
                    if (!predecessors.TryGetValue(successor, out var list))
                    {
                        list = [];
                        predecessors[successor] = list;
                    }

                    list.Add(block);
                }
            }

            return predecessors;
        }
    }

    /// <summary>The facts a block added, forgotten when the walk leaves its part of the dominator tree.</summary>
    private sealed class Learned
    {
        public List<Value> Bits { get; } = [];

        public List<(Predicate Predicate, Value Left, Value Right)> Compares { get; } = [];
    }
}
