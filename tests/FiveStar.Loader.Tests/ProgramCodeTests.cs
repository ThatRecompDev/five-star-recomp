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
/// once nothing holds it, kept while a pipeline stream points at it, and handed to pipeline creation by pointer. Every
/// object under test is made and dropped inside a method of its own (not inlined), so no local of the test method can
/// keep it alive in an unoptimized or tier-0 build, and its release is waited for over several collections.
/// </summary>
[Collection(nameof(ProgramCodeTests))]
public sealed unsafe class ProgramCodeTests
{
    private const int Size = 3000;
    private const int StreamPayloadOffset = 8;
    private const int Collections = 20;
    private const int PauseMilliseconds = 10;

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
            Assert.True(Freed(code));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_pipeline_stream_points_at_the_program_without_a_copy_and_keeps_it_alive()
    {
        var code = DescribeAndDrop(out bool keptByStream, out nint pointer, out nint described, out long length);

        Assert.True(keptByStream);
        Assert.Equal(pointer, described);
        Assert.Equal(Size, length);

        // The stream is gone as well: the program goes with it.
        Assert.True(Freed(code));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ReadAndCompare(string path, byte[] expected)
    {
        var program = new TranslatedProgram("cs", HardwareStage.Compute, ProgramCode.Read(path));
        Assert.True(program.Dxil.SequenceEqual(expected));
        Assert.Equal(Size, program.Code.Length);

        return new WeakReference(program.Code);
    }

    /// <summary>
    /// A stream over a new program that only the stream holds: whether the program outlives collections while the stream
    /// lives, and what the stream's desc points at; the stream is disposed and dropped on return.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DescribeAndDrop(out bool keptByStream, out nint pointer, out nint described, out long length)
    {
        var stream = StreamOverNewProgram(out pointer, out var code);

        Collect();
        keptByStream = code.IsAlive;

        var desc = (nint*)stream.Describe();
        described = *(nint*)((byte*)desc[1] + StreamPayloadOffset);
        length = *(long*)((byte*)desc[1] + StreamPayloadOffset + sizeof(nint));
        stream.Dispose();

        return code;
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

    /// <summary>Whether the object behind <paramref name="code"/> is collected (and finalized) within a bounded number of collections.</summary>
    private static bool Freed(WeakReference code)
    {
        for (int round = 0; round < Collections; round++)
        {
            Collect();

            if (!code.IsAlive)
            {
                return true;
            }

            Thread.Sleep(PauseMilliseconds);
        }

        return false;
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
