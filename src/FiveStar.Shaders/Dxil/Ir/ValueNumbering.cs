// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

// Parts follow VIctory (fork https://github.com/fxceless/victory-fork, crates/victory-shader/src/emit/bitcode/opt.rs,
// commit 1684989; marked where they are): Copyright (c) the VIctory contributors, GPL-3.0-or-later. See
// THIRD_PARTY_NOTICES.md.

namespace FiveStar.Shaders.Dxil.Ir;

/// <summary>
/// Common-subexpression removal over the dominator tree: an instruction that computes what an instruction in a
/// dominating block already computed is replaced by that one. Pure instructions (arithmetic, comparisons, casts,
/// selects, extracts, addresses, readnone <c>dx.op</c> calls and constant-buffer loads) match in any dominated block;
/// other memory reads (raw buffer and texture loads, samples) in a dominated block too, but only while nothing with an
/// effect (a write, an atomic, a barrier) and no block of more than one predecessor (a join or a loop header, which a
/// path with a write may reach) came between them in the walk: a generation counter, as LLVM's EarlyCSE keeps. Matched
/// within a block only, the reads of one descriptor's words (SPtrLoad) were made again in every block, and so were the
/// handles made from them: LLVM's early-cse and gvn over the backend's output still removed 40 to 80% of the handles of
/// the hot programs. Wave operations are not readnone (their result depends on the active lanes) and
/// never match. <see cref="DeadCode"/> then drops what is
/// no longer read. The translator repeats the same handle creations, address-map lookups and range arithmetic for
/// every access to one buffer; a large compute program loses about a fifth of its instructions.
/// </summary>
public static class ValueNumbering
{
    private const string CbufferLoad = "dx.op.cbufferLoadLegacy";
    private const string AnnotateHandle = "dx.op.annotateHandle";

    public static void Run(IrFunction function)
    {
        if (function.Blocks.Count > 0)
        {
            new Pass(function).Run();
        }
    }

    private static Kind KindOf(Instruction instruction)
    {
        switch (instruction.Opcode)
        {
            case Opcode.Binary or Opcode.Cast or Opcode.Compare or Opcode.Select or Opcode.ExtractValue or Opcode.GetElementPtr:
                return Kind.Pure;
            case Opcode.Call:
                return CallKind(instruction.Callee!);
            case Opcode.Load:
                return LoadKind(instruction);
            case Opcode.Alloca or Opcode.Phi:
                return Kind.Other;
            default:
                return Kind.Effect;
        }
    }

    /// <summary>
    /// A load from a constant table reads the same whatever is stored (pure); one from local or shared memory is a read
    /// (matched later in its block until a write, as in VIctory's fork: another thread's unsynchronised store may fall
    /// between any two reads or before both, which the title's code cannot tell apart).
    /// </summary>
    private static Kind LoadKind(Instruction load)
    {
        var pointer = load.Operands[0];

        var address = pointer as Instruction;

        if (address is not null && address.Opcode == Opcode.GetElementPtr)
        {
            pointer = address.Operands[0];
        }

        var global = pointer as GlobalVariable;

        if (global is not null && global.Initializer is not null)
        {
            return Kind.Pure;
        }

        return Kind.Read;
    }

    private static Kind CallKind(IrFunction callee)
    {
        if (callee.Attribute == AttributeSet.ReadNone || callee.Name.StartsWith(CbufferLoad, StringComparison.Ordinal))
        {
            return Kind.Pure;
        }

        if (callee.Attribute == AttributeSet.ReadOnly)
        {
            return Kind.Read;
        }

        return Kind.Effect;
    }

    private static void Rewrite(Instruction instruction, Dictionary<Value, Value> replaced)
    {
        if (replaced.Count == 0)
        {
            return;
        }

        var operands = instruction.Operands;

        for (var index = 0; index < operands.Count; index++)
        {
            var value = operands[index];

            while (replaced.TryGetValue(value, out var replacement))
            {
                value = replacement;
            }

            operands[index] = value;
        }
    }

    /// <summary>
    /// Phis read values of later blocks over back edges: rewritten once every block is numbered. A phi whose
    /// operands are then all one value (itself aside) is that value, which can make others trivial in turn.
    /// </summary>
    private static void RewritePhis(IrFunction function, Dictionary<Value, Value> replaced)
    {
        bool changed = true;

        while (changed)
        {
            changed = false;

            foreach (var block in function.Blocks)
            {
                foreach (var phi in block.Phis)
                {
                    Rewrite(phi, replaced);
                }

                changed |= block.Phis.RemoveAll(phi => Trivial(phi, replaced)) > 0;
            }
        }

        // Instructions read the removed phis too: one more pass over everything.
        foreach (var block in function.Blocks)
        {
            block.Phis.ForEach(phi => Rewrite(phi, replaced));
            block.Body.ForEach(instruction => Rewrite(instruction, replaced));
            Rewrite(block.Terminator!, replaced);
        }
    }

    private static bool Trivial(Instruction phi, Dictionary<Value, Value> replaced)
    {
        Value? only = null;

        foreach (var operand in phi.Operands)
        {
            if (ReferenceEquals(operand, phi) || ReferenceEquals(operand, only))
            {
                continue;
            }

            if (only is not null)
            {
                return false;
            }

            only = operand;
        }

        if (only is null)
        {
            return false;
        }

        replaced[phi] = only;

        return true;
    }

    /// <summary>One run: the walk over the dominator tree with its tables.</summary>
    private sealed class Pass
    {
        private readonly IrFunction _function;
        private readonly BasicBlock _entry;
        private readonly Dictionary<Value, Value> _replaced = [];
        private readonly Dictionary<Key, Value> _available = [];
        private readonly Dictionary<Key, (Value Value, int Generation, BasicBlock Block)> _reads = [];
        private readonly HashSet<BasicBlock> _joins = [];
        private int _generation;
        private int _lastGeneration;
        private BasicBlock? _block;
        private readonly HashSet<Value> _entryValues = [];
        private readonly List<Instruction> _hoisted = [];

        public Pass(IrFunction function)
        {
            _function = function;
            _entry = function.Blocks[0];
        }

        public void Run()
        {
            var tree = DominatorTree.Of(_function);
            FindJoins();
            var scopes = new Stack<Scope>();
            scopes.Push(new Scope(_entry, [], [], 0, false));

            while (scopes.Count > 0)
            {
                var scope = scopes.Pop();

                if (scope.Entered)
                {
                    scope.Added.ForEach(key => _available.Remove(key));
                    Undo(scope.Reads);

                    continue;
                }

                // A block starts from its immediate dominator's generation as that block ended: what a sibling subtree
                // wrote reaches it only through a join, which starts a generation of its own.
                _generation = scope.Generation;
                scopes.Push(scope with { Entered = true });
                Number(scope.Block, scope.Added, scope.Reads);

                foreach (var child in tree.Children(scope.Block))
                {
                    scopes.Push(new Scope(child, [], [], _generation, false));
                }
            }

            _entry.Body.AddRange(_hoisted);
            RewritePhis(_function, _replaced);
        }

        /// <summary>The blocks more than one block branches to (joins and loop headers): reads before them do not reach past them.</summary>
        private void FindJoins()
        {
            var seen = new HashSet<BasicBlock>();

            foreach (var block in _function.Blocks)
            {
                foreach (var successor in block.Successors.Distinct())
                {
                    if (!seen.Add(successor))
                    {
                        _joins.Add(successor);
                    }
                }
            }
        }

        /// <summary>Leaving a dominator subtree: the reads it made available go, those of its dominators come back.</summary>
        private void Undo(List<ReadUndo> reads)
        {
            for (int index = reads.Count - 1; index >= 0; index--)
            {
                var undo = reads[index];

                if (undo.Had)
                {
                    _reads[undo.Key] = undo.Previous;
                }
                else
                {
                    _reads.Remove(undo.Key);
                }
            }
        }

        /// <summary>A generation no read was made in yet.</summary>
        private void NewGeneration()
        {
            _generation = ++_lastGeneration;
        }

        private void Number(BasicBlock block, List<Key> added, List<ReadUndo> reads)
        {
            _block = block;

            if (_joins.Contains(block))
            {
                NewGeneration();
            }

            foreach (var phi in block.Phis)
            {
                Rewrite(phi, _replaced);
            }

            block.Body.RemoveAll(instruction => Replace(block, instruction, added, reads));
            Rewrite(block.Terminator!, _replaced);

            if (ReferenceEquals(block, _entry))
            {
                _entryValues.UnionWith(block.Body);
            }
        }

        /// <summary>
        /// Rewrites the operands, then replaces the instruction by an earlier equal one or, when it is pure and reads
        /// only constants and entry values, moves it to the entry, where every block sees it (true: remove it here).
        /// </summary>
        private bool Replace(BasicBlock block, Instruction instruction, List<Key> added, List<ReadUndo> reads)
        {
            Rewrite(instruction, _replaced);
            var kind = KindOf(instruction);

            if (kind == Kind.Effect)
            {
                NewGeneration();

                return false;
            }

            if (kind == Kind.Other)
            {
                return false;
            }

            var key = new Key(instruction);

            if (kind == Kind.Read)
            {
                return ReplaceRead(instruction, key, reads);
            }

            if (_available.TryGetValue(key, out var earlier))
            {
                _replaced[instruction] = earlier;

                return true;
            }

            _available[key] = instruction;

            if (kind == Kind.Pure && !ReferenceEquals(block, _entry) && Hoistable(instruction))
            {
                // Its key stays available to the end: no block scope removes it.
                _hoisted.Add(instruction);
                _entryValues.Add(instruction);

                return true;
            }

            if (kind == Kind.Pure)
            {
                added.Add(key);
            }

            return false;
        }

        /// <summary>A memory read: replaced by an equal one of a dominating block made in this generation, else kept as the latest.</summary>
        private bool ReplaceRead(Instruction instruction, Key key, List<ReadUndo> reads)
        {
            bool had = _reads.TryGetValue(key, out var earlier);

            if (had && earlier.Generation == _generation && (ReferenceEquals(earlier.Block, _block) || Unchanging(instruction)))
            {
                _replaced[instruction] = earlier.Value;

                return true;
            }

            reads.Add(new ReadUndo(key, had, earlier));
            _reads[key] = (instruction, _generation, _block!);

            return false;
        }

        /// <summary>Pure, cannot trap (no division), and reads only constants and values the entry computes.</summary>
        private bool Hoistable(Instruction instruction)
        {
            // A load whose block may not run could read past its table; a division could divide by zero.
            if (instruction.Opcode == Opcode.Load
                || (instruction.Opcode == Opcode.Binary && (BinaryOp)instruction.Code is BinaryOp.UDiv or BinaryOp.SDiv or BinaryOp.URem or BinaryOp.SRem))
            {
                return false;
            }

            foreach (var operand in instruction.Operands)
            {
                if (operand is not (Constant or IrFunction) && !_entryValues.Contains(operand))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>D3D12_RESOURCE_KIND of the handle annotation with the UAV bit (<see cref="Ops.DxOps.UavBit"/>).</summary>
    private const ulong UavBit = Ops.DxOps.UavBit;

    /// <summary>
    /// Whether no invocation can change what <paramref name="read"/> returns while the dispatch or draw runs: a read
    /// through a shader-resource or constant-buffer handle (an annotated handle without the UAV bit). Only such reads are
    /// reused across blocks. A read of a UAV, of groupshared or of local memory is matched only later in its own block, as
    /// before: other lanes of the wave, other groups or another queue may write it between two blocks of one iteration
    /// (the translator's wave emulation passes values between lanes through memory), and a reused read could then keep a
    /// loop's condition from ever changing (w131 and w140 hung with 406723d8, which reused every read).
    /// </summary>
    private static bool Unchanging(Instruction read)
    {
        if (read.Opcode != Opcode.Call)
        {
            return false;
        }

        if (read.Callee!.Name.StartsWith(CbufferLoad, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var operand in read.Operands)
        {
            var handle = operand as Instruction;

            if (handle is null || handle.Opcode != Opcode.Call || !handle.Callee!.Name.StartsWith(AnnotateHandle, StringComparison.Ordinal))
            {
                continue;
            }

            var properties = handle.Operands[^1] as Constant;

            return properties is not null && properties.Elements.Count > 0 && (properties.Elements[0].Unsigned & UavBit) == 0;
        }

        return false;
    }

    /// <summary>A block of the dominator-tree walk: what it made available, and the generation it starts from.</summary>
    private readonly record struct Scope(BasicBlock Block, List<Key> Added, List<ReadUndo> Reads, int Generation, bool Entered);

    /// <summary>A read made available in a scope, and what the key held before (restored on leaving it).</summary>
    private readonly record struct ReadUndo(Key Key, bool Had, (Value Value, int Generation, BasicBlock Block) Previous);

    private enum Kind
    {
        /// <summary>Its value depends only on its operands: matched in dominated blocks.</summary>
        Pure,

        /// <summary>A memory read: matched later in its block until a write.</summary>
        Read,

        /// <summary>Writes or synchronizes: never matched, ends the block's reads.</summary>
        Effect,

        /// <summary>Not matched (loads from local or shared memory, phis, allocas), no effect.</summary>
        Other,
    }

    /// <summary>What an instruction computes: its operator, codes, types and operands (by reference; constants are interned).</summary>
    internal sealed class Key : IEquatable<Key>
    {
        private readonly Instruction _instruction;
        private readonly int _hash;
        private const int BinaryOperands = 2;

        private readonly bool _commutes;

        public Key(Instruction instruction)
        {
            _instruction = instruction;
            var hash = HashCode.Combine(instruction.Opcode, instruction.Code, instruction.Type, instruction.Callee, instruction.SourceType);
            _commutes = Commutes(instruction);

            if (_commutes)
            {
                // Either order hashes alike: the operands' hashes are combined in sorted order.
                var (first, second) = (instruction.Operands[0].GetHashCode(), instruction.Operands[1].GetHashCode());
                hash = HashCode.Combine(hash, Math.Min(first, second), Math.Max(first, second));
            }
            else
            {
                foreach (var operand in instruction.Operands)
                {
                    hash = HashCode.Combine(hash, operand);
                }
            }

            _hash = hash;
        }

        /// <summary>
        /// Integer sums, products and bit operations match whichever way round their operands are (VIctory's fork,
        /// opt.rs Module::key); float ones only as written (which NaN an operation of two NaNs gives is the hardware's
        /// choice by operand).
        /// </summary>
        private static bool Commutes(Instruction instruction)
        {
            return instruction.Opcode == Opcode.Binary && instruction.Type.IsInteger && instruction.Operands.Count == BinaryOperands
                && (BinaryOp)instruction.Code is BinaryOp.Add or BinaryOp.Mul or BinaryOp.And or BinaryOp.Or or BinaryOp.Xor;
        }

        public bool Equals(Key? other)
        {
            if (other is null)
            {
                return false;
            }

            var a = _instruction;
            var b = other._instruction;

            if (a.Opcode != b.Opcode || a.Code != b.Code || !ReferenceEquals(a.Type, b.Type) || !ReferenceEquals(a.Callee, b.Callee)
                || !ReferenceEquals(a.SourceType, b.SourceType) || a.Operands.Count != b.Operands.Count)
            {
                return false;
            }

            if (_commutes && ReferenceEquals(a.Operands[0], b.Operands[1]) && ReferenceEquals(a.Operands[1], b.Operands[0]))
            {
                return true;
            }

            for (var index = 0; index < a.Operands.Count; index++)
            {
                if (!ReferenceEquals(a.Operands[index], b.Operands[index]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as Key);
        }

        public override int GetHashCode()
        {
            return _hash;
        }
    }
}
