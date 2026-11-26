// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Shaders.Tests.Cases;
using FiveStar.Shaders.Tests.Toolchain;
using FiveStar.Shaders.Tests.Warp;
using FiveStar.Shaders.Translation;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// The prelude's wide store (BufStoreN): a wave whose lanes store consecutive n-dword records writes the block a dword a
/// lane, any other wave each lane's own dwords. Either way the buffer must hold exactly what per-dword stores leave.
/// </summary>
public sealed class WideStoreTests : IClassFixture<WarpFixture>
{
    private const int Threads = 64;
    private const int OutputSlot = 1;
    private const uint Untouched = 0xDEADBEEF;

    private readonly WarpFixture _fixture;

    public WideStoreTests(WarpFixture fixture)
    {
        _fixture = fixture;
    }

    [WarpTheory]
    [InlineData(2, 0, false)]
    [InlineData(3, 0, false)]
    [InlineData(4, 0, false)]
    [InlineData(4, 1, false)]
    [InlineData(4, 0, true)]
    public void A_wide_store_leaves_the_bytes_of_per_dword_stores(int dwords, int gap, bool oddLanesOnly)
    {
        int stride = dwords + gap;
        var shader = _fixture.Workbench.Compile($"wide_{dwords}_{gap}_{oddLanesOnly}", Program(dwords, stride, oddLanesOnly), "cs");
        var output = Enumerable.Repeat(Untouched, Threads * stride).ToArray();
        var bindings = new Bindings { Buffers = [new BufferResource(OutputSlot, output, Writable: true)] };
        var words = _fixture.Runner.Compute(new ComputeJob(shader, 1, 1, 1, bindings)).Buffers[OutputSlot];

        var expected = Enumerable.Repeat(Untouched, Threads * stride).ToArray();

        for (int thread = 0; thread < Threads; thread++)
        {
            for (int dword = 0; dword < dwords && (!oddLanesOnly || thread % 2 == 1); dword++)
            {
                expected[(thread * stride) + dword] = (uint)((thread * 16) + dword + 1);
            }
        }

        Assert.Equal(expected, words);
    }

    /// <summary>A translated global_store_dwordx4 of consecutive records into a mapped page of game memory (PtrStoreN).</summary>
    [WarpFact]
    public void A_translated_store_of_consecutive_records_reaches_game_memory()
    {
        const string Program = """
            v_lshlrev_b32_e32 v1, 4, v41
            v_mov_b32 v2, 5
            v_mov_b32 v10, v41
            v_add_nc_u32_e32 v11, 0x100, v41
            v_add_nc_u32_e32 v12, 0x200, v41
            v_add_nc_u32_e32 v13, 0x300, v41
            global_store_dwordx4 v[1:2], v[10:13], off
            v_mov_b32 v20, 0
            """;
        const int PageHeap = 12;
        const int NullView = 8;

        var map = new AddressMap(1, NullView);
        var bytes = new byte[map.Bytes];
        map.Clear(bytes);
        map.Map(bytes, 0x5_0000_0000, 0x10000, PageHeap - 1, 0x5_0000_0000);
        var stored = Enumerable.Range(0, Threads * 4).Select(word => (uint)((word / 4) + ((word % 4) * 0x100))).ToArray();

        var testCase = new ComputeCase("cs_wide_store", Program, 1, _ => [Expected.Exact(0)])
        {
            Buffers =
            [
                new BufferResource((int)AddressMap.HeapSlot, MemoryMarshal.Cast<byte, uint>(bytes).ToArray(), Writable: false),
                new BufferResource(PageHeap, new uint[Threads * 4], Writable: true),
            ],
            Check = written => Differences(stored, written[PageHeap]),
        };

        Assert.Empty(new ComputeCaseRunner(_fixture).Run(testCase));
    }

    private static IEnumerable<string> Differences(uint[] expected, uint[] actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            yield return $"the page holds {string.Join(", ", actual.Take(8))}..., not {string.Join(", ", expected.Take(8))}...";
        }
    }

    private static string Program(int dwords, int stride, bool oddLanesOnly)
    {
        string condition = "true";

        if (oddLanesOnly)
        {
            condition = "(gi & 1u) == 1u";
        }

        return $$"""
            #include "rdna_prelude.hlsli"
            [numthreads({{Threads}}, 1, 1)]
            void main(uint gi : SV_GroupIndex) {
                uint4 v = uint4(gi * 16u + 1u, gi * 16u + 2u, gi * 16u + 3u, gi * 16u + 4u);
                if ({{condition}}) BufStoreN({{OutputSlot}}u, gi * {{stride * 4}}u, v, {{dwords}}u);
            }
            """;
    }
}
