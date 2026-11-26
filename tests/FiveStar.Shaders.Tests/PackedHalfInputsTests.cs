// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Containers;
using FiveStar.Shaders.Disassembly;
using FiveStar.Shaders.Hardware;
using FiveStar.Shaders.Translation;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>
/// A pixel program takes from its inputs only the f16 halves it reads. With both halves of every read parameter, the
/// trimmed PS_TexturedDeferred kept 20 input registers and its linked MS_VS_TransformD passed the 32 KB of mesh
/// outputs (w97): the link failed, the unlinked pair missed ATTRLO/ATTRHI (error #666) and the pipeline was refused.
/// </summary>
public sealed class PackedHalfInputsTests
{
    [Fact]
    public void Only_the_low_halves_read_are_taken_from_the_inputs()
    {
        // attr0: padded layout (both halves declared for the registers), only the low one taken.
        string padded = Translate(0);

        Assert.Contains("pin.attrlo0", padded, StringComparison.Ordinal);
        Assert.DoesNotContain("pin.attrhi0", padded, StringComparison.Ordinal);
        Assert.DoesNotContain(" : ATTRHI0;", StageLinker.TrimInputs(padded), StringComparison.Ordinal);

        // attr20: too many halves for the padded layout, so only the half read is declared.
        string unpadded = Translate(20);

        Assert.Contains(" : ATTRLO20;", unpadded, StringComparison.Ordinal);
        Assert.DoesNotContain("ATTRHI", unpadded, StringComparison.Ordinal);
    }

    private static string Translate(int attribute)
    {
        string[] text =
        [
            "s_mov_b32 m0, s2",
            $"v_interp_p1ll_f16 v3, v0, attr{attribute}.x",
            $"v_interp_p2_f16 v4, v1, attr{attribute}.x, v3",
            "v_cvt_f32_f16_e32 v4, v4",
            "exp mrt0 v4, v4, v4, v4 done vm",
            "s_endpgm",
        ];
        var instructions = text.Select((line, index) => new Instruction(index * 8, 8, line)).ToList();
        var config = new ShaderConfig { Stage = HardwareStage.Pixel, WaveSize = 32 };
        var program = new ShaderProgram(ShaderStage.Pixel, instructions, new byte[instructions.Count * 8], ShaderSignatures.None, config, null);

        return HlslTranslator.Translate(program, new TranslationOptions(StructuredControlFlow: true, PinWaveSize: false, EmulateWaveSize: true)).Hlsl;
    }
}
