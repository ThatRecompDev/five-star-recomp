// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Dxil;
using FiveStar.Shaders.Dxil.Ir;
using FiveStar.Shaders.Dxil.Ops;
using FiveStar.Shaders.Tests.Toolchain;
using FiveStar.Shaders.Tests.Warp;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// The clean-ups ported from VIctory's fork: branch facts (<see cref="BranchFacts"/>), joins of the same values,
/// commutative and repeated reads in value numbering, and the folds of casts, selects and float constants. A program
/// shaped like the translator's (a branch on a mask, then each statement guarded by the same mask; an and of two
/// conditions; registers that hold one value along every path) computes the same words on WARP optimized and plain;
/// and on hand-built functions each clean-up does what it says.
/// </summary>
public sealed class DxilCleanUpTests : IClassFixture<WarpFixture>
{
    private const int OutputSlot = 1;
    private const int InputSlot = 2;
    private const int Threads = 64;

    private const string Program = """
        static const uint table[4] = { 3u, 5u, 7u, 11u };
        [numthreads(64, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID) {
          RWByteAddressBuffer output = ResourceDescriptorHeap[1];
          ByteAddressBuffer input = ResourceDescriptorHeap[2];
          uint x = input.Load(id.x * 4u);
          bool odd = (x & 1u) != 0;
          bool big = x > 0x4000u;
          uint v = 1u, w = 1u;
          uint local[4] = { x, x + 1u, x + 2u, x + 3u };
          if (odd && big) {
            v = odd ? x + 2u : x;
            v = big ? v * 3u : v;
            v += (x > 0x4000u) ? 7u : 9u;
            v += (x <= 0x4000u) ? 100u : 200u;
            w = v;
          } else if (!odd) {
            v = odd ? 0u : x ^ 0x55u;
            w = v;
          }
          uint sum = (x + v) ^ (v + x);
          uint keep = asuint(asfloat(x)) + local[x & 3u] + local[x & 3u] + table[x & 3u] + table[x & 3u];
          bool same = (1.0f < 2.0f) && !(asfloat(0x7fc00000u) == 1.0f);
          output.Store(id.x * 4u, v + w * 3u + sum + keep + (same ? 13u : 17u));
        }
        """;

    private readonly WarpFixture _fixture;

    public DxilCleanUpTests(WarpFixture fixture)
    {
        _fixture = fixture;
    }

    [WarpFact]
    public void A_cleaned_program_computes_the_same_words()
    {
        var plain = Run(optimize: false);
        var cleaned = Run(optimize: true);

        Assert.Equal(plain, cleaned);
        Assert.NotEqual(new uint[Threads], plain);
    }

    [Fact]
    public void A_branch_taken_decides_its_condition_and_its_terms_below_it()
    {
        var module = new DxilModule();
        var types = module.Types;
        var main = module.Function("main", types.Function(types.Void, []), AttributeSet.None, hasBody: true);

        var entry = module.NewBlock();
        var taken = module.NewBlock();
        var other = module.NewBlock();
        main.Blocks.AddRange([entry, taken, other]);

        var builder = new IrBuilder(module, entry);
        var ops = new DxOps(builder);
        var thread = ops.ThreadIdComponent(DxOpCode.ThreadId, "threadId", 0);
        var odd = builder.Compare(Predicate.NotEqual, builder.Binary(BinaryOp.And, thread, module.Int32(1)), module.Int32(0));
        var big = builder.Compare(Predicate.UnsignedGreater, thread, module.Int32(8));
        builder.ConditionalBranch(builder.Binary(BinaryOp.And, odd, big), taken, other);

        builder.Block = taken;
        var guarded = builder.Select(odd, module.Int32(2), thread);
        var opposite = builder.Select(builder.Compare(Predicate.UnsignedLessOrEqual, thread, module.Int32(8)), module.Int32(5), guarded);
        Store(builder, ops, module, thread, opposite);
        builder.Return();

        builder.Block = other;
        Store(builder, ops, module, thread, builder.Select(odd, module.Int32(3), thread));
        builder.Return();

        Simplifier.Run(main, module);

        // Below the taken side odd held and thread <= 8 did not: the select chain is the constant 2.
        Assert.Contains(taken.Body, instruction => instruction.Opcode == Opcode.Call && instruction.Operands.Contains(module.Int32(2)));
        Assert.DoesNotContain(taken.Body, instruction => instruction.Opcode == Opcode.Select);

        // The other side knows nothing about odd alone (the and failed): its select stays.
        Assert.Contains(other.Body, instruction => instruction.Opcode == Opcode.Select);
    }

    [Fact]
    public void Joins_of_the_same_values_are_one_and_sums_match_either_way_round()
    {
        var module = new DxilModule();
        var types = module.Types;
        var main = module.Function("main", types.Function(types.Void, []), AttributeSet.None, hasBody: true);

        var entry = module.NewBlock();
        var left = module.NewBlock();
        var right = module.NewBlock();
        var join = module.NewBlock();
        main.Blocks.AddRange([entry, left, right, join]);

        var builder = new IrBuilder(module, entry);
        var ops = new DxOps(builder);
        var thread = ops.ThreadIdComponent(DxOpCode.ThreadId, "threadId", 0);
        var group = ops.ThreadIdComponent(DxOpCode.GroupId, "groupId", 0);
        builder.ConditionalBranch(builder.Compare(Predicate.Equal, thread, module.Int32(0)), left, right);

        builder.Block = left;
        builder.Branch(join);
        builder.Block = right;
        builder.Branch(join);

        builder.Block = join;
        var first = IrBuilder.Phi(join, types.Int32);
        var second = IrBuilder.Phi(join, types.Int32);

        foreach (var phi in new[] { first, second })
        {
            phi.Operands.AddRange([thread, group]);
            phi.Targets.AddRange([left, right]);
        }

        var sum = builder.Binary(BinaryOp.Add, first, second);
        var swapped = builder.Binary(BinaryOp.Add, group, thread);
        var again = builder.Binary(BinaryOp.Add, thread, group);
        Store(builder, ops, module, thread, builder.Binary(BinaryOp.Xor, sum, builder.Binary(BinaryOp.Sub, swapped, again)));
        builder.Return();

        ValueNumbering.Run(main);
        DeadCode.Remove(main);
        Simplifier.Run(main, module);

        var merged = Assert.Single(main.Blocks.SelectMany(block => block.Phis));
        Assert.DoesNotContain(main.Blocks.SelectMany(block => block.Body), instruction => instruction.Opcode == Opcode.Binary && (BinaryOp)instruction.Code == BinaryOp.Sub);
        Assert.NotNull(merged);
    }

    /// <summary>
    /// A memory read is reused in a block its block dominates while nothing with an effect came between, but not past a
    /// write or into a join (a block more than one block branches to, which a path with a write may reach).
    /// </summary>
    [Fact]
    public void A_read_is_reused_below_until_a_write_or_a_join()
    {
        var module = new DxilModule();
        var types = module.Types;
        var main = module.Function("main", types.Function(types.Void, []), AttributeSet.None, hasBody: true);

        var entry = module.NewBlock();
        var taken = module.NewBlock();
        var other = module.NewBlock();
        var join = module.NewBlock();
        main.Blocks.AddRange([entry, taken, other, join]);

        var builder = new IrBuilder(module, entry);
        var ops = new DxOps(builder);
        var thread = ops.ThreadIdComponent(DxOpCode.ThreadId, "threadId", 0);
        var first = Read(ops, module);
        builder.ConditionalBranch(builder.Compare(Predicate.Equal, first, module.Int32(0)), taken, other);

        // Below the entry and before any write: the entry's read stands for this one.
        builder.Block = taken;
        Store(builder, ops, module, thread, Read(ops, module));

        // After the write: read again.
        Store(builder, ops, module, thread, builder.Binary(BinaryOp.Add, Read(ops, module), thread));
        builder.Branch(join);

        builder.Block = other;
        builder.Branch(join);

        // The join may be reached from the write: read again.
        builder.Block = join;
        Store(builder, ops, module, thread, builder.Binary(BinaryOp.Xor, Read(ops, module), thread));
        builder.Return();

        ValueNumbering.Run(main);
        DeadCode.Remove(main);

        Assert.Equal(1, Reads(entry));
        Assert.Equal(1, Reads(taken));
        Assert.Equal(1, Reads(join));
    }

    [Fact]
    public void Casts_there_and_back_and_float_constants_fold()
    {
        var module = new DxilModule();
        var types = module.Types;

        Assert.Equal(module.Bool(true), ConstantFolding.Compare(module, Predicate.FloatOrderedLess, module.Float(1), module.Float(2)));
        Assert.Equal(module.Bool(false), ConstantFolding.Compare(module, Predicate.FloatOrderedEqual, module.FloatBits(types.Float, 0x7fc00000), module.Float(1)));
        Assert.Equal(module.Bool(true), ConstantFolding.Compare(module, Predicate.FloatUnorderedEqual, module.FloatBits(types.Float, 0x7fc00000), module.Float(1)));
        Assert.Equal(module.Int32(0xffffffff), ConstantFolding.Binary(module, BinaryOp.Or, module.Int32(7), module.Int32(0xffffffff)));
    }

    private static void Store(IrBuilder builder, DxOps ops, DxilModule module, Value index, Value value)
    {
        var handle = ops.HeapHandle(module.Int32(OutputSlot), sampler: false, DxOps.RawBufferKind | DxOps.UavBit, 0);
        ops.RawStore(handle, index, [value]);
    }

    /// <summary>The input buffer's first word.</summary>
    private static Value Read(DxOps ops, DxilModule module)
    {
        var handle = ops.HeapHandle(module.Int32(InputSlot), sampler: false, DxOps.RawBufferKind, 0);

        return ops.RawLoad(handle, module.Int32(0), 1)[0];
    }

    private static int Reads(BasicBlock block)
    {
        return block.Body.Count(instruction => instruction.Opcode == Opcode.Call && instruction.Callee!.Name.Contains("rawBufferLoad", StringComparison.Ordinal));
    }

    private uint[] Run(bool optimize)
    {
        byte[] container;
        var previous = DxilBackend.Optimize;
        DxilBackend.Optimize = optimize;

        try
        {
            container = DxilBackend.Compile(Program, "cs");
        }
        finally
        {
            DxilBackend.Optimize = previous;
        }

        var input = new BufferResource(InputSlot, Enumerable.Range(0, Threads).Select(index => (uint)((index * 7919) ^ 0x5a5a)).ToArray(), Writable: false);
        var output = new BufferResource(OutputSlot, new uint[Threads], Writable: true);
        var result = _fixture.Runner.Compute(new ComputeJob(container, 1, 1, 1, new Bindings { Buffers = [output, input] }));

        return result.Buffers[OutputSlot];
    }
}
