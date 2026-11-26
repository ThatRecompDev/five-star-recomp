// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// A program's DXIL in native memory (<see cref="NativeMemory"/>), not the managed heap. The libraries held about 475
/// MB of it as byte arrays (w85d), all on the large object heap, which the GC compacts under ConserveMemory with every
/// managed thread stopped; a pipeline stream also copied each program into native memory again for every creation.
/// The bytes live as long as this object is reachable: a library that drops a program, a pipeline recipe and a stream
/// each hold the object, and the finalizer frees the bytes once none does. The GC is told of the native bytes
/// (<see cref="GC.AddMemoryPressure"/>), so a run that lets programs go still collects them. A pointer taken from
/// <see cref="Pointer"/> is valid only while the object is kept alive (<see cref="GC.KeepAlive"/> after the native call).
/// </summary>
internal sealed unsafe class ProgramCode
{
    private static long _liveBytes;
    private static long _liveCount;

    private readonly byte* _bytes;

    private ProgramCode(int length)
    {
        Length = length;
        _bytes = (byte*)NativeMemory.Alloc((nuint)Math.Max(length, 1));
        GC.AddMemoryPressure(Math.Max(length, 1));
        Interlocked.Add(ref _liveBytes, length);
        Interlocked.Increment(ref _liveCount);
    }

    ~ProgramCode()
    {
        NativeMemory.Free(_bytes);
        GC.RemoveMemoryPressure(Math.Max(Length, 1));
        Interlocked.Add(ref _liveBytes, -Length);
        Interlocked.Decrement(ref _liveCount);
    }

    /// <summary>Bytes of DXIL in native memory now, over every program not yet finalized.</summary>
    public static long LiveBytes => Interlocked.Read(ref _liveBytes);

    /// <summary>Programs whose native bytes are not freed yet.</summary>
    public static long LiveCount => Interlocked.Read(ref _liveCount);

    public int Length { get; }

    /// <summary>The bytes; valid while this object is reachable.</summary>
    public byte* Pointer => _bytes;

    public ReadOnlySpan<byte> Span => new(_bytes, Length);

    /// <summary>A copy of <paramref name="bytes"/>.</summary>
    public static ProgramCode Copy(ReadOnlySpan<byte> bytes)
    {
        var code = new ProgramCode(bytes.Length);
        bytes.CopyTo(new Span<byte>(code._bytes, code.Length));

        return code;
    }

    /// <summary>
    /// Opens <paramref name="path"/> for reading, trying again for a short while on a sharing violation: another worker
    /// may be moving a fresh program over it (a warm-up of 3,715 pipelines ended on a link file being replaced as it was
    /// opened).
    /// </summary>
    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenShared(string path)
    {
        const int SharingViolation = unchecked((int)0x80070020);
        const int Attempts = 20;
        const int PauseMilliseconds = 50;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            }
            catch (IOException exception) when (exception.HResult == SharingViolation && attempt < Attempts)
            {
                Thread.Sleep(PauseMilliseconds);
            }
        }
    }

    /// <summary>The file at <paramref name="path"/>, read straight into native memory (no managed copy).</summary>
    public static ProgramCode Read(string path)
    {
        using var file = OpenShared(path);
        long size = RandomAccess.GetLength(file);

        if (size > int.MaxValue)
        {
            throw new InvalidDataException($"{path}: {size} bytes is no program");
        }

        var code = new ProgramCode((int)size);
        var target = new Span<byte>(code._bytes, code.Length);
        int read = 0;

        while (read < target.Length)
        {
            int got = RandomAccess.Read(file, target[read..], read);

            if (got == 0)
            {
                throw new EndOfStreamException($"{path}: ended after {read} of {size} bytes");
            }

            read += got;
        }

        return code;
    }
}
