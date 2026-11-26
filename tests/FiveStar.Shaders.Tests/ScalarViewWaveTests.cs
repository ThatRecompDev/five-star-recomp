// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Tests.Toolchain;
using FiveStar.Shaders.Tests.Warp;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// A scalar load's view index comes from SGPRs, the same on every lane of a console wave, but a hardware wave wider than
/// the program wave holds several program waves whose SGPRs differ (here readfirstlane of the thread index, as a loop
/// over materials does). Each must read its own view: SBufLoad indexes the heap through NonUniformResourceIndex.
/// Without it AMD (RX 7800 XT) read every lane's view at the first lane's index; WARP reads the right one either way, so
/// the case shows the bug with FIVESTAR_TEST_ADAPTER=hardware.
/// </summary>
public sealed class ScalarViewWaveTests : IClassFixture<WarpFixture>
{
    private const int Threads = 64;
    private const int OutputSlot = 1;
    private const int FirstView = 2;
    private const int Views = 4;
    private const int Dwords = 4;

    private readonly WarpFixture _fixture;

    public ScalarViewWaveTests(WarpFixture fixture)
    {
        _fixture = fixture;
    }

    [WarpTheory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(32)]
    public void Each_program_wave_of_a_wider_hardware_wave_reads_its_own_scalar_view(int wave)
    {
        // Program waves that the hardware wave holds 1 to 4 of: readfirstlane emulates no more (rdna_prelude.hlsli), and
        // 2- and 4-lane program waves are WARP's test sizes. WARP runs 2 and 4, a 32-lane GPU 16 and 32, a 64-lane one 16
        // and 32 (two program waves of 32 on one hardware wave: the console's wave32 programs on AMD's wave64).
        int programWaves = _fixture.ComputeWaveSize / wave;

        if (programWaves < 1 || programWaves > 4)
        {
            return;
        }

        var shader = _fixture.Workbench.Compile($"sview_{wave}", Program(wave), "cs");
        var buffers = new List<BufferResource> { new(OutputSlot, new uint[Threads], Writable: true) };

        for (var view = 0; view < Views; view++)
        {
            buffers.Add(new BufferResource(FirstView + view, Enumerable.Range(0, Dwords).Select(i => (uint)((100 * (view + 1)) + i)).ToArray(), Writable: false));
        }

        var words = _fixture.Runner.Compute(new ComputeJob(shader, 1, 1, 1, new Bindings { Buffers = [.. buffers] })).Buffers[OutputSlot];
        var mismatches = new List<string>();

        for (var thread = 0; thread < Threads; thread++)
        {
            var expected = (uint)((100 * (((thread / wave) % Views) + 1)) + (thread % Dwords));

            if (words[thread] != expected)
            {
                mismatches.Add($"thread {thread}: expected {expected} actual {words[thread]}");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(16)));
    }

    private static string Program(int wave)
    {
        return $$"""
            #define VW_WAVE {{wave}}
            #define VW_EMULATE 1
            #include "rdna_prelude.hlsli"
            [numthreads({{Threads}}, 1, 1)]
            void main(uint gi : SV_GroupIndex) {
                RWByteAddressBuffer o = ResourceDescriptorHeap[{{OutputSlot}}];
                uint first = readfirstlane(gi, true);
                uint view = {{FirstView}}u + (first / VW_WAVE) % {{Views}}u;
                o.Store(gi * 4u, SBufLoad(view, (gi % {{Dwords}}u) * 4u));
            }
            """;
    }
}
