// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

// The layout of the export patch table and the choice of its 32_ABGR slot follow VIctory
// (https://github.com/marvisak/victory, crates/victory-shader/src/native.rs, NativeProgram::patched_code and ps_io,
// commit 13f91ca): Copyright (c) the VIctory contributors, GPL-3.0-or-later. See THIRD_PARTY_NOTICES.md.

namespace FiveStar.Shaders.Hardware;

/// <summary>
/// A pixel program's export patches. Its code ends each render target's export in the form for one colour format (an
/// FP16 pair: <c>v_cvt_pkrtz_f16_f32</c> and <c>exp ... compr</c>), and the program's stage data holds, per render target,
/// where that sequence sits and its code for every SPI_SHADER_COL_FORMAT (ZERO, 32_R, 32_GR, 32_AR, FP16, UNORM16,
/// SNORM16, UINT16, SINT16, 32_ABGR), which the console's driver writes over it at pipeline creation for the target's
/// format. The 32_ABGR form exports the program's 32-bit values unconverted; the desktop's output merger then converts
/// them to the target's format as the console's colour buffer converts the export. FSSM: the patch size (dwords) at
/// 0x25C, then 8 patches of a code offset (dwords from the code block's start, which carries a 24-byte hash ahead of the
/// code; -1 for none) and 10 slots of 10 dwords.
/// </summary>
public static class ExportPatches
{
    private const int SizeField = 0x25C;
    private const int PatchesField = 0x260;
    private const int RenderTargets = 8;
    private const int SlotDwords = 10;
    private const int Slots = 10;
    private const int FullSlot = 9;
    private const int WordBytes = 4;
    private const int PatchBytes = WordBytes + (Slots * SlotDwords * WordBytes);
    private const int HashPrefix = 0x18;
    private const uint None = uint.MaxValue;

    /// <summary>
    /// <paramref name="code"/> (<see cref="FssmHeader.ProgramCode"/> of <paramref name="program"/>) with each render
    /// target's export patched to its 32_ABGR form; as it is when the program has no patches or they do not fit it.
    /// </summary>
    public static byte[] Apply(ReadOnlySpan<byte> program, byte[] code)
    {
        if (program.Length < PatchesField + (RenderTargets * PatchBytes))
        {
            return code;
        }

        int size = (int)FssmHeader.U32(program, SizeField);

        if (size <= 0 || size > SlotDwords)
        {
            return code;
        }

        var patched = code.ToArray();

        for (int target = 0; target < RenderTargets; target++)
        {
            int at = PatchesField + (target * PatchBytes);
            uint offset = FssmHeader.U32(program, at);

            if (offset == None)
            {
                continue;
            }

            long start = ((long)offset * WordBytes) - HashPrefix;

            if (start < 0 || start + (size * WordBytes) > patched.Length)
            {
                return code;
            }

            program.Slice(at + WordBytes + (FullSlot * SlotDwords * WordBytes), size * WordBytes).CopyTo(patched.AsSpan((int)start));
        }

        return patched;
    }
}
