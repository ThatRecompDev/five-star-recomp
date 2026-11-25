// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using FiveStar.Shaders.Hardware;
using Xunit;

namespace FiveStar.Shaders.Tests;

/// <summary>A pixel program's export is patched to its 32_ABGR form, as the console's driver does for a 32-bit format.</summary>
public sealed class ExportPatchesTests
{
    private const int SizeField = 0x25C;
    private const int PatchesField = 0x260;
    private const int PatchBytes = 4 + (10 * 10 * 4);
    private const int FullSlot = 9;
    private const uint Nop = 0xBF800000;

    [Fact]
    public void Each_targets_export_takes_its_32_bit_form_at_its_offset_past_the_hash()
    {
        // The code as TL_PS_PBR_MATERIAL_STANDARD stores mrt0's FP16 form: two nops, two v_cvt_pkrtz_f16_f32 and
        // exp mrt0 compr; its 32_ABGR form: four nops and exp mrt0 v14, v11, v16, v9 (en 0xf).
        uint[] fp16 = [Nop, Nop, 0x5E04170E, 0x5E061310, 0xF800040F, 0x00000302];
        uint[] full = [Nop, Nop, Nop, Nop, 0xF800000F, 0x09100B0E];
        var code = new byte[64];
        Write(code, 16, fp16);

        var program = new byte[PatchesField + (8 * PatchBytes)];
        BinaryPrimitives.WriteUInt32LittleEndian(program.AsSpan(SizeField), 6);

        for (int target = 0; target < 8; target++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(program.AsSpan(PatchesField + (target * PatchBytes)), uint.MaxValue);
        }

        // Offsets count dwords from the code block's start, 24 bytes of hash ahead of the code: byte 16 is dword 10.
        BinaryPrimitives.WriteUInt32LittleEndian(program.AsSpan(PatchesField), 10);
        Write(program, PatchesField + 4 + (FullSlot * 40), full);

        var patched = ExportPatches.Apply(program, code);

        Assert.Equal(full, Read(patched, 16, full.Length));
        Assert.Equal(new byte[16], patched[..16]);
        Assert.Equal(fp16, Read(code, 16, fp16.Length));

        // A program without patches (size 0) keeps its code.
        BinaryPrimitives.WriteUInt32LittleEndian(program.AsSpan(SizeField), 0);
        Assert.Same(code, ExportPatches.Apply(program, code));
    }

    private static void Write(byte[] bytes, int at, uint[] words)
    {
        for (int i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + (4 * i)), words[i]);
        }
    }

    private static uint[] Read(byte[] bytes, int at, int count)
    {
        return [.. Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + (4 * i))))];
    }
}
