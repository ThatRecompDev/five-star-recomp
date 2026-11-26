// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Formats.Compression;

namespace FiveStar.Loader.Shims.Storage;

/// <summary>
/// Inflates a queue's zlib requests with the native zlib the .NET runtime ships (System.IO.Compression.Native, zlib-ng):
/// one inflate state per queue, reset per request, the output written straight into a reused buffer, so a request
/// allocates nothing. The managed <see cref="ZlibInflater"/> ran at 61 MB/s in a Debug build and 266 MB/s in Release,
/// zlib-ng at about 850 MB/s in both; a boot's loading screens inflated 10.4 GB in 320 s of the queues' time. The
/// exports are the runtime's own (what ZLibStream calls), not a public API: when they are missing or the state does
/// not inflate a known stream, <see cref="Create"/> returns null and the queue keeps the managed inflater.
/// </summary>
internal sealed unsafe class NativeZlibInflater : IDisposable
{
    private const string Library = "System.IO.Compression.Native";
    private const int ZlibWindowBits = 15;
    private const int FlushFinish = 4;
    private const int StreamEnd = 1;

    private static readonly byte[] CheckStream = [0x78, 0x9c, 0xcb, 0x48, 0xcd, 0xc9, 0xc9, 0x07, 0x00, 0x06, 0x2c, 0x02, 0x15];
    private static readonly byte[] CheckText = "hello"u8.ToArray();

    private readonly ZStream* _stream;
    private byte[] _output = [];
    private int _length;

    private NativeZlibInflater(ZStream* stream)
    {
        _stream = stream;
    }

    /// <summary>Gets the bytes the last <see cref="Inflate"/> produced.</summary>
    public ReadOnlySpan<byte> Output => _output.AsSpan(0, _length);

    /// <summary>An inflater over the runtime's zlib, or null when its exports are missing or fail a known stream.</summary>
    public static NativeZlibInflater? Create()
    {
        var stream = (ZStream*)NativeMemory.AllocZeroed((nuint)sizeof(ZStream));

        try
        {
            if (InflateInit2(stream, ZlibWindowBits) != 0)
            {
                NativeMemory.Free(stream);

                return null;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            NativeMemory.Free(stream);

            return null;
        }

        var inflater = new NativeZlibInflater(stream);

        if (!inflater.Inflate(CheckStream, CheckText.Length) || !inflater.Output.SequenceEqual(CheckText))
        {
            inflater.Dispose();

            return null;
        }

        return inflater;
    }

    /// <summary>
    /// Inflates one zlib stream that must produce exactly <paramref name="size"/> bytes and end there (its Adler-32
    /// trailer checked); false for a damaged, shorter or longer stream.
    /// </summary>
    [HotPath]
    public bool Inflate(ReadOnlySpan<byte> input, int size)
    {
        if (_output.Length < size + 1)
        {
            Grow(size + 1);
        }

        _length = 0;

        if (InflateReset2(_stream, ZlibWindowBits) != 0)
        {
            return false;
        }

        int result;

        fixed (byte* source = input)
        fixed (byte* output = _output)
        {
            // One byte more than expected: a stream that would produce more does not reach its end.
            _stream->NextIn = source;
            _stream->AvailIn = (uint)input.Length;
            _stream->NextOut = output;
            _stream->AvailOut = (uint)(size + 1);

            result = InflateNative(_stream, FlushFinish);
            _length = size + 1 - (int)_stream->AvailOut;
            _stream->NextIn = null;
            _stream->NextOut = null;
        }

        return result == StreamEnd && _length == size;
    }

    public void Dispose()
    {
        _ = InflateEnd(_stream);
        NativeMemory.Free(_stream);
    }

    private void Grow(int size)
    {
        _output = new byte[size];
    }

    [DllImport(Library, EntryPoint = "CompressionNative_InflateInit2_")]
    private static extern int InflateInit2(ZStream* stream, int windowBits);

    [DllImport(Library, EntryPoint = "CompressionNative_InflateReset2_")]
    private static extern int InflateReset2(ZStream* stream, int windowBits);

    [DllImport(Library, EntryPoint = "CompressionNative_Inflate")]
    private static extern int InflateNative(ZStream* stream, int flush);

    [DllImport(Library, EntryPoint = "CompressionNative_InflateEnd")]
    private static extern int InflateEnd(ZStream* stream);

    /// <summary>The runtime's PAL_ZStream (pal_zlib.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ZStream
    {
        public byte* NextIn;
        public byte* NextOut;
        public nint Message;
        public nint State;
        public uint AvailIn;
        public uint AvailOut;
    }
}
