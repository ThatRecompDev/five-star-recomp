// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using FiveStar.Shaders.Dxil.Container;

namespace FiveStar.Shaders.Compilation;

/// <summary>
/// Runs LLVM passes over the DXIL part of a container through dxcompiler.dll's IDxcOptimizer (the interface dxopt.exe
/// wraps) and writes the container again with the new bitcode. The passes are dxc's own cleanups of a lowered module
/// (<see cref="Passes"/>); none uses fast-math, so floating-point results stay as they were. Opt-in
/// (<c>--dxil-llvm-opt</c>): the driver's pipeline build follows the number of blocks, which SimplifyCFG cuts. Each call
/// makes its own optimizer object, so calls may run on any threads.
/// </summary>
public sealed unsafe class LlvmPostPass
{
    /// <summary>The passes: SimplifyCFG and InstCombine twice, then dead code, blocks, declarations and globals (the validator refuses unused ones).</summary>
    public static readonly IReadOnlyList<string> Passes =
    [
        "-opt-mod-passes", "-simplifycfg", "-instcombine,NoSink=0", "-simplifycfg", "-instcombine,NoSink=0", "-simplifycfg",
        "-dce", "-dxil-remove-dead-blocks", "-dce", "-strip-dead-prototypes", "-globaldce",
    ];

    private const int ReleaseSlot = 2;
    private const int PointerSlot = 3;
    private const int SizeSlot = 4;
    private const int CreateBlobSlot = 6;
    private const int RunOptimizerSlot = 5;
    private const int ProgramHeaderBytes = 24;
    private const int ProgramSizeOffset = 4;
    private const int BitcodeSizeOffset = 20;
    private const int PartCountOffset = 28;
    private const int PartTableOffset = 32;

    private static readonly Guid OptimizerClass = new("ae2cd79f-cc22-453f-9b6b-b124e7a5204c");
    private static readonly Guid OptimizerInterface = new("25740e2e-9cba-401b-9119-4fb42f39f270");
    private static readonly Guid UtilsClass = new("6245d6af-66e0-48fd-80b4-4d271796748c");
    private static readonly Guid UtilsInterface = new("4605c4cb-2019-492a-ada4-65f20bb7d67f");

    private readonly Lazy<nint> _create;

    /// <param name="path">dxc.exe, dxcompiler.dll or their folder.</param>
    public LlvmPostPass(string path)
    {
        var library = DxcLibraryCompiler.LibraryPath(path);
        _create = new Lazy<nint>(() => NativeLibrary.GetExport(NativeLibrary.Load(library), "DxcCreateInstance"));
    }

    /// <summary>What the output depends on: the passes (the library's bytes are the caller's identity to add).</summary>
    public static string Identity => "llvm-post-pass:" + string.Join(' ', Passes);

    /// <summary>The container with its DXIL part's bitcode optimized; the other parts as they were.</summary>
    public byte[] Optimize(byte[] container)
    {
        var parts = Parts(container);
        var index = parts.FindIndex(part => part.FourCc == "DXIL");

        if (index < 0)
        {
            throw new InvalidOperationException("the container has no DXIL part");
        }

        var program = parts[index].Data;
        var bitcode = Run(program);
        var part = new byte[ProgramHeaderBytes + bitcode.Length];
        program.AsSpan(0, ProgramHeaderBytes).CopyTo(part);
        BinaryPrimitives.WriteUInt32LittleEndian(part.AsSpan(ProgramSizeOffset), (uint)(part.Length / sizeof(uint)));
        BinaryPrimitives.WriteUInt32LittleEndian(part.AsSpan(BitcodeSizeOffset), (uint)bitcode.Length);
        bitcode.CopyTo(part, ProgramHeaderBytes);
        parts[index] = ("DXIL", part);

        return ContainerWriter.Build(parts);
    }

    private byte[] Run(byte[] program)
    {
        var options = Passes.Select(Marshal.StringToHGlobalUni).ToArray();
        nint optimizer = 0, utils = 0, blob = 0, module = 0, text = 0;

        try
        {
            optimizer = Create(OptimizerClass, OptimizerInterface);
            utils = Create(UtilsClass, UtilsInterface);

            fixed (byte* data = program)
            {
                var create = (delegate* unmanaged<nint, void*, uint, uint, nint*, int>)Slot(utils, CreateBlobSlot);
                Check(create(utils, data, (uint)program.Length, 0, &blob), "CreateBlob");
            }

            fixed (nint* argv = options)
            {
                var run = (delegate* unmanaged<nint, nint, nint*, uint, nint*, nint*, int>)Slot(optimizer, RunOptimizerSlot);
                var result = run(optimizer, blob, argv, (uint)options.Length, &module, &text);

                if (result < 0 || module == 0)
                {
                    throw new InvalidOperationException($"RunOptimizer failed: 0x{result:x8} {Text(text)}");
                }
            }

            return Bytes(module);
        }
        finally
        {
            foreach (var option in options)
            {
                Marshal.FreeHGlobal(option);
            }

            Release(text);
            Release(module);
            Release(blob);
            Release(utils);
            Release(optimizer);
        }
    }

    private static List<(string FourCc, byte[] Data)> Parts(byte[] container)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(container.AsSpan(PartCountOffset));
        var parts = new List<(string, byte[])>(count);

        for (var index = 0; index < count; index++)
        {
            var offset = BinaryPrimitives.ReadInt32LittleEndian(container.AsSpan(PartTableOffset + (index * sizeof(uint))));
            var size = BinaryPrimitives.ReadInt32LittleEndian(container.AsSpan(offset + sizeof(uint)));
            parts.Add((Encoding.ASCII.GetString(container, offset, sizeof(uint)), container.AsSpan(offset + (2 * sizeof(uint)), size).ToArray()));
        }

        return parts;
    }

    private nint Create(Guid type, Guid iid)
    {
        nint instance = 0;
        var create = (delegate* unmanaged<Guid*, Guid*, nint*, int>)_create.Value;
        Check(create(&type, &iid, &instance), "DxcCreateInstance");

        return instance;
    }

    private static string Text(nint blob)
    {
        if (blob == 0)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(Bytes(blob)).TrimEnd('\0');
    }

    private static byte[] Bytes(nint blob)
    {
        var pointer = ((delegate* unmanaged<nint, nint>)Slot(blob, PointerSlot))(blob);
        var size = ((delegate* unmanaged<nint, nuint>)Slot(blob, SizeSlot))(blob);

        return new ReadOnlySpan<byte>((void*)pointer, checked((int)size)).ToArray();
    }

    private static nint Slot(nint instance, int slot)
    {
        return (*(nint**)instance)[slot];
    }

    private static void Release(nint instance)
    {
        if (instance != 0)
        {
            ((delegate* unmanaged<nint, uint>)Slot(instance, ReleaseSlot))(instance);
        }
    }

    private static void Check(int hr, string call)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException($"{call} failed: 0x{hr:x8}");
        }
    }
}
