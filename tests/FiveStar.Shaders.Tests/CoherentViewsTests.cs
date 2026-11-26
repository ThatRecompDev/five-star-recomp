// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Containers;
using FiveStar.Shaders.Disassembly;
using FiveStar.Shaders.Dxil.Lowering;
using FiveStar.Shaders.Hardware;
using FiveStar.Shaders.Translation;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// A compute program that talks across groups (a memory atomic, a glc load, a cache invalidation) declares its writable
/// views globallycoherent, and the DXIL backend marks such a view's handle (resource property bit 14); a program without
/// such work keeps plain views.
/// </summary>
public sealed class CoherentViewsTests
{
    [Fact]
    public void Compute_programs_that_talk_across_groups_get_globally_coherent_views()
    {
        string atomic = Translate("buffer_atomic_add v20, v1, s[4:7], 0 offen glc");
        string glcLoad = Translate("buffer_load_dword v20, v1, s[4:7], 0 offen glc");
        string plain = Translate("buffer_load_dword v20, v1, s[4:7], 0 offen");

        Assert.Contains("globallycoherent RWByteAddressBuffer", atomic, StringComparison.Ordinal);
        Assert.Contains("#define FIVESTAR_COHERENT globallycoherent", glcLoad, StringComparison.Ordinal);
        Assert.Contains("#define FIVESTAR_COHERENT globallycoherent", atomic, StringComparison.Ordinal);
        Assert.DoesNotContain("globallycoherent", plain, StringComparison.Ordinal);

        var coherent = HlslType.Coherent(HlslType.Resource("RWByteAddressBuffer", null));
        var (kind, _) = ResourceProperties.Of(coherent, null!);
        var (plainKind, _) = ResourceProperties.Of(HlslType.Resource("RWByteAddressBuffer", null), null!);

        Assert.Equal(ResourceProperties.GloballyCoherentBit, kind & ResourceProperties.GloballyCoherentBit);
        Assert.Equal(0u, plainKind & ResourceProperties.GloballyCoherentBit);
    }

    private static string Translate(string memory)
    {
        string[] text = [memory, "s_endpgm"];
        var instructions = text.Select((line, index) => new Instruction(index * 8, 8, line)).ToList();
        var compute = new ComputeConfig { Rsrc2 = 0, ScratchEnable = false, GroupSizeEnable = false, ThreadIdComponentCount = 1, LdsBlocks = 0, NumThreads = new ThreadGroupSize(64, 1, 1), GroupIdSgprs = [] };
        var config = new ShaderConfig { Stage = HardwareStage.Compute, WaveSize = 64, UserSgprs = 12, Compute = compute };
        var program = new ShaderProgram(ShaderStage.Compute, instructions, new byte[instructions.Count * 8], ShaderSignatures.None, config, null);

        return HlslTranslator.Translate(program, new TranslationOptions(StructuredControlFlow: true, PinWaveSize: true, EmulateWaveSize: true)).Hlsl;
    }
}
