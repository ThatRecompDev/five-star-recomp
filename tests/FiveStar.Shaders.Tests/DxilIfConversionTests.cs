// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Dxil;
using FiveStar.Shaders.Dxil.Container;
using FiveStar.Shaders.Dxil.Hlsl;
using FiveStar.Shaders.Dxil.Ir;
using FiveStar.Shaders.Dxil.Lowering;
using FiveStar.Shaders.Dxil.Ops;
using FiveStar.Shaders.Tests.Toolchain;
using FiveStar.Shaders.Translation;
using FiveStar.Shaders.Tests.Warp;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// The backend's <see cref="IfConversion"/>: a program with small branches of each shape (one arm, two arms, nested)
/// and branches that must stay (an integer division, a wave sum, a load) computes the same words on WARP with and
/// without it, in fewer blocks; on a hand-built function a two-armed branch becomes a select and an arm holding a wave
/// operation stays a branch.
/// </summary>
public sealed class DxilIfConversionTests : IClassFixture<WarpFixture>
{
    private const int OutputSlot = 1;
    private const int InputSlot = 2;
    private const int Threads = 64;

    private const string Program = """
        uint fetch(uint index) { ByteAddressBuffer b = ResourceDescriptorHeap[2]; return b.Load((index & 63u) * 4u); }
        [numthreads(64, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID) {
          RWByteAddressBuffer output = ResourceDescriptorHeap[1];
          uint x = fetch(id.x);
          float f = asfloat(x);
          uint a = x, b = 0u, q = 0u, w = 0u, l = 0u;
          float g = 1.0f;
          if ((x & 1u) != 0u) { a = x * 3u + 7u; }
          if ((x & 2u) != 0u) { b = a ^ 0x55u; g = sqrt(f) * 2.0f; } else { b = a >> 3u; g = f - 1.5f; }
          if ((x & 12u) != 0u) { q = 1000u / (x & 12u); }
          if ((x & 16u) != 0u) { w = WaveActiveSum(x); }
          if ((x & 32u) != 0u) { l = fetch(id.x + 1u); }
          if ((x & 64u) != 0u) { if ((x & 128u) != 0u) { a += 5u; } else { a -= 9u; } }
          output.Store(id.x * 4u, a + b * 3u + q * 5u + w * 7u + l * 11u + asuint(g) * 13u);
        }
        """;

    private readonly WarpFixture _fixture;

    public DxilIfConversionTests(WarpFixture fixture)
    {
        _fixture = fixture;
    }

    [WarpFact]
    public void A_converted_program_computes_the_same_words_in_fewer_blocks()
    {
        var (plain, plainBlocks) = Run(convert: false);
        var (converted, convertedBlocks) = Run(convert: true);

        Assert.Equal(plain, converted);
        Assert.NotEqual(new uint[Threads], plain);
        Assert.True(convertedBlocks < plainBlocks, $"{convertedBlocks} blocks converted, {plainBlocks} plain");
    }

    [Fact]
    public void Two_computing_arms_become_a_select_and_an_arm_with_a_wave_operation_stays()
    {
        var module = new DxilModule();
        var types = module.Types;
        var main = module.Function("main", types.Function(types.Void, []), AttributeSet.None, hasBody: true);

        var entry = module.NewBlock();
        var left = module.NewBlock();
        var right = module.NewBlock();
        var join = module.NewBlock();
        var waveArm = module.NewBlock();
        var end = module.NewBlock();
        main.Blocks.AddRange([entry, left, right, join, waveArm, end]);

        var builder = new IrBuilder(module, entry);
        var ops = new DxOps(builder);
        var thread = ops.ThreadIdComponent(DxOpCode.ThreadId, "threadId", 0);
        var odd = builder.Compare(Predicate.NotEqual, builder.Binary(BinaryOp.And, thread, module.Int32(1)), module.Int32(0));
        builder.ConditionalBranch(odd, left, right);

        builder.Block = left;
        var tripled = builder.Binary(BinaryOp.Mul, thread, module.Int32(3));
        builder.Branch(join);

        builder.Block = right;
        var shifted = builder.Binary(BinaryOp.LShr, thread, module.Int32(2));
        builder.Branch(join);

        builder.Block = join;
        var merged = IrBuilder.Phi(join, types.Int32);
        merged.Operands.AddRange([tripled, shifted]);
        merged.Targets.AddRange([left, right]);
        var high = builder.Compare(Predicate.UnsignedGreater, merged, module.Int32(8));
        builder.ConditionalBranch(high, waveArm, end);

        builder.Block = waveArm;
        var any = ops.Call(DxOpCode.WaveAnyTrue, "waveAnyTrue", AttributeSet.NoUnwind, types.Bool, high);
        builder.Branch(end);

        builder.Block = end;
        var flag = IrBuilder.Phi(end, types.Int32);
        flag.Operands.AddRange([builder.Cast(CastOp.ZExt, any, types.Int32), module.Int32(0)]);
        flag.Targets.AddRange([waveArm, join]);
        var handle = ops.HeapHandle(module.Int32(OutputSlot), sampler: false, DxOps.RawBufferKind | DxOps.UavBit, 0);
        ops.RawStore(handle, thread, [merged, flag]);
        builder.Return();

        DeadCode.Remove(main);
        Simplifier.Run(main, module, convertBranches: true);

        Assert.Equal(3, main.Blocks.Count);
        Assert.Contains(main.Blocks[0].Body, instruction => instruction.Opcode == Opcode.Select);
        Assert.Equal(Opcode.ConditionalBranch, main.Blocks[0].Terminator!.Opcode);
    }

    private (uint[] Words, int Blocks) Run(bool convert)
    {
        byte[] container;
        int blocks;
        var previous = DxilBackend.ConvertBranches;
        DxilBackend.ConvertBranches = convert;

        try
        {
            container = DxilBackend.Compile(Program, "cs");
            blocks = ShaderLowering.Lower(Parser.Parse(Preprocessor.Run(Program, RuntimeIncludes.Read)), DxilShaderKind.Compute).Main.Blocks.Count;
        }
        finally
        {
            DxilBackend.ConvertBranches = previous;
        }

        var input = new BufferResource(InputSlot, Enumerable.Range(0, Threads).Select(index => (uint)(index * 2654435761u) ^ 0x3f80_0000u).ToArray(), Writable: false);
        var output = new BufferResource(OutputSlot, new uint[Threads], Writable: true);
        var result = _fixture.Runner.Compute(new ComputeJob(container, 1, 1, 1, new Bindings { Buffers = [output, input] }));

        return (result.Buffers[OutputSlot], blocks);
    }
}
