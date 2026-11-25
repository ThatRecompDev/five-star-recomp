// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using FiveStar.Loader.Shims.D3D12X.Pipelines;
using FiveStar.Shaders.Hardware;
using Vortice.Direct3D12;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// Programs' DXIL lives in native memory (<see cref="ProgramCode"/>): read from a file without a managed copy, freed
/// once nothing holds it, kept while a pipeline stream points at it, and handed to pipeline creation by pointer.
/// </summary>
[Collection(nameof(ProgramCodeTests))]
public sealed unsafe class ProgramCodeTests
{
    private const int Size = 3000;
    private const int StreamPayloadOffset = 8;

    [Fact]
    public void A_program_read_from_a_file_holds_its_bytes_natively_and_is_freed_once_unreachable()
    {
        var path = Path.GetTempFileName();

        try
        {
            byte[] bytes = [.. Enumerable.Range(0, Size).Select(index => (byte)(index * 7))];
            File.WriteAllBytes(path, bytes);

            var code = ReadAndCompare(path, bytes);

            // Nothing holds it any more: the finalizer frees the native bytes.
            Settle();
            Assert.False(code.IsAlive);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_pipeline_stream_points_at_the_program_without_a_copy_and_keeps_it_alive()
    {
        var stream = StreamOverNewProgram(out nint pointer, out var code);

        // Only the stream holds the program now.
        Settle();
        Assert.True(code.IsAlive);

        var desc = (nint*)stream.Describe();
        Assert.Equal(pointer, *(nint*)((byte*)desc[1] + StreamPayloadOffset));
        Assert.Equal(Size, *(long*)((byte*)desc[1] + StreamPayloadOffset + sizeof(nint)));

        stream.Dispose();
        GC.KeepAlive(stream);
        stream = null;
        Settle();
        Assert.False(code.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ReadAndCompare(string path, byte[] expected)
    {
        var program = new TranslatedProgram("cs", HardwareStage.Compute, ProgramCode.Read(path));
        Assert.True(program.Dxil.SequenceEqual(expected));
        Assert.Equal(Size, program.Code.Length);

        return new WeakReference(program.Code);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static DesktopStream StreamOverNewProgram(out nint pointer, out WeakReference code)
    {
        var program = TranslatedProgram.Of("cs", HardwareStage.Compute, new byte[Size]);
        pointer = (nint)program.Code.Pointer;
        code = new WeakReference(program.Code);
        var stream = new DesktopStream();
        stream.AddShader(PipelineStateSubObjectType.ComputeShader, program.Code);

        return stream;
    }

    private static void Settle()
    {
        for (int round = 0; round < 3; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}
