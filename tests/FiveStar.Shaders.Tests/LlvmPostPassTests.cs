// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Compilation;
using FiveStar.Shaders.Dxil;
using FiveStar.Shaders.Tests.Toolchain;
using FiveStar.Shaders.Tests.Warp;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// <see cref="LlvmPostPass"/>: a backend program with what LLVM's passes could move wrongly (the same wave sum in both
/// arms of a branch, a ballot under a per-lane condition, loads and a division in small arms, floating point that
/// fast-math would change) computes the same words on WARP before and after the passes.
/// </summary>
public sealed class LlvmPostPassTests : IClassFixture<WarpFixture>
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
          float f = asfloat(x & 0x807fffffu | 0x3f000000u);
          uint s = 0u, a = x, q = 0u, l = 0u;
          if ((x & 1u) != 0u) { s = WaveActiveSum(x); } else { s = WaveActiveSum(x); }
          if ((x & 2u) != 0u) { a = WaveActiveBallot((x & 4u) != 0u).x; }
          if ((x & 8u) != 0u) { l = fetch(id.x + 3u); }
          if ((x & 48u) != 0u) { q = 999u / (x & 48u); }
          float g = (f + 1.0e8f) - 1.0e8f;
          if ((x & 64u) != 0u) { g = g * 3.0f + f; } else { g = sqrt(abs(f)) - g; }
          output.Store(id.x * 4u, s + a * 3u + q * 5u + l * 7u + asuint(g) * 11u + WaveGetLaneIndex());
        }
        """;

    private readonly WarpFixture _fixture;

    public LlvmPostPassTests(WarpFixture fixture)
    {
        _fixture = fixture;
    }

    [WarpFact]
    public void The_passes_leave_the_words_as_they_were()
    {
        var container = DxilBackend.Compile(Program, "cs");
        var optimized = new LlvmPostPass(ShaderToolchain.Find().Dxc).Optimize(container);

        var plain = Run(container);

        Assert.Equal(plain, Run(optimized));
        Assert.NotEqual(new uint[Threads], plain);
        Assert.NotEqual(container, optimized);
    }

    private uint[] Run(byte[] container)
    {
        var input = new BufferResource(InputSlot, Enumerable.Range(0, Threads).Select(index => (uint)(index * 2654435761u)).ToArray(), Writable: false);
        var output = new BufferResource(OutputSlot, new uint[Threads], Writable: true);
        var result = _fixture.Runner.Compute(new ComputeJob(container, 1, 1, 1, new Bindings { Buffers = [output, input] }));

        return result.Buffers[OutputSlot];
    }
}
