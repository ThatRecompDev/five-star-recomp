// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

// Facts after VIctory-7900XTX (https://github.com/avatarmanz/VIctory-7900XTX, commits e151e71 and 45cad4c,
// resolution.rs; GPL-3.0-or-later): where the title applies its output size and its back-buffer pool check. The bytes and
// the stub are read from our build in IDA and written anew here. See THIRD_PARTY_NOTICES.md.

using System.Buffers.Binary;

namespace FiveStar.Loader.Mods;

/// <summary>
/// <c>--output WxH</c>: the title renders and presents at one of its own output sizes instead of the 3840x2160 its
/// settings hold (a Series X's). Its settings apply (+0x22434a0) hands the settings' size (+0x204, +0x208) to the screen
/// size setter (+0x2478130) only when it differs from the current width; at +0x224352b a jump goes to a stub that writes
/// the asked size into the live settings and continues in the title's own else-path (+0x2243548: the setter, and
/// +0xd8c3a0 when the height changed), so the device parameters and the render size (2/3 of it: 1280x720 at 1080p)
/// follow. The back-buffer pool (64 MiB on a Series X) breaks into the debugger unless all but 2 MiB of it was used
/// (+0x247af25); with smaller back buffers that check is jumped over. Build 1.0.2.1 only (SHA-256 checked); every site's
/// bytes are compared before anything is written.
/// </summary>
internal static class OutputSizePatches
{
    public const string Module = "game_scarlett_master.exe";

    /// <summary>The SHA-256 of game_scarlett_master.exe 1.0.2.1, whose bytes the patterns below are.</summary>
    public const string Sha256 = "E95020B2FA4A721DB3DF59FDECF75621730AC559D8835252F6C3B658D5A08993";

    /// <summary>The title's own output modes (+0x615c0c0).</summary>
    public static readonly IReadOnlyList<(int Width, int Height)> Modes = [(1280, 720), (1664, 936), (1920, 1080), (2560, 1440), (3840, 2160)];

    /// <summary>
    /// +0x224352b: <c>mov ecx, [width]; mov eax, [rsi+204h]; cmp ecx, eax; jnz else; mov edx, [rsi+208h]; call setter;
    /// jmp after</c>, up to the else-path at +0x1d.
    /// </summary>
    private const string SizeSite = "8B 0D 9B 03 70 08 8B 86 04 02 00 00 39 C1 75 0D 8B 96 08 02 00 00 E8 EA 4B 23 00 EB 20";
    private const int ElsePath = 0x1D;

    /// <summary>+0x247af25: <c>cmp rax, 1FFFFFh; jbe +1; int3; lea rdx, ...</c>; the <c>jbe</c> at +6 becomes a <c>jmp</c>.</summary>
    private const string PoolCheck = "48 3D FF FF 1F 00 76 01 CC 48 8D 15";
    private const int PoolBranch = 6;
    private const byte ShortJump = 0xEB;

    /// <summary>The 4K back buffers fill the pool as the check expects: no jump over it at that size.</summary>
    private const int NativeWidth = 3840;
    private const int NativeHeight = 2160;

    // mov dword [rsi+204h], imm32; mov dword [rsi+208h], imm32; mov eax, imm32.
    private static readonly byte[] StoreWidth = [0xC7, 0x86, 0x04, 0x02, 0x00, 0x00];
    private static readonly byte[] StoreHeight = [0xC7, 0x86, 0x08, 0x02, 0x00, 0x00];
    private const byte MoveEax = 0xB8;

    /// <summary>Whether the title has the mode <paramref name="width"/>x<paramref name="height"/>.</summary>
    public static bool IsMode(int width, int height)
    {
        return Modes.Contains((width, height));
    }

    /// <summary>The patches that make the title output <paramref name="width"/>x<paramref name="height"/>.</summary>
    public static IReadOnlyList<CodePatch> For(int width, int height)
    {
        var patches = new List<CodePatch>
        {
            new(
                "output-size", Module, Sha256, true, $"the title's output size set to {width}x{height}",
                BytePattern.Parse(SizeSite), 1, 0, null, null, "--output", Stub(width, height), ElsePath),
        };

        if (width != NativeWidth || height != NativeHeight)
        {
            patches.Add(new CodePatch(
                "output-size-pool", Module, Sha256, true, "the back-buffer pool's all-but-2-MiB check jumped over",
                BytePattern.Parse(PoolCheck), 1, PoolBranch, [ShortJump], null, "--output", null, 0));
        }

        return patches;
    }

    /// <summary>The stub: the asked size into the live settings and into eax, as the else-path expects.</summary>
    internal static byte[] Stub(int width, int height)
    {
        var stub = new List<byte>();
        stub.AddRange(StoreWidth);
        stub.AddRange(Immediate(width));

        stub.AddRange(StoreHeight);
        stub.AddRange(Immediate(height));
        stub.Add(MoveEax);
        stub.AddRange(Immediate(width));

        return [.. stub];
    }

    private static byte[] Immediate(int value)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);

        return bytes;
    }
}
