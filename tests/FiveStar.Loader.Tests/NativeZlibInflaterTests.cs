// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using FiveStar.Formats.Compression;
using FiveStar.Loader.Shims.Storage;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// DirectStorage zlib requests inflated by the runtime's native zlib: the same bytes as the managed inflater, and the same
/// refusals (a stream that is damaged, ends early, or would produce more than the request's size).
/// </summary>
public sealed class NativeZlibInflaterTests
{
    [Fact]
    public void The_runtimes_zlib_inflates_requests_as_the_managed_inflater_does()
    {
        using var native = NativeZlibInflater.Create();
        Assert.NotNull(native);

        var managed = new ZlibInflater();
        var random = new Random(7);

        foreach (int size in new[] { 1, 4096, 65536, 0x40000 })
        {
            byte[] raw = new byte[size];

            for (int i = 0; i < size; i++)
            {
                raw[i] = (byte)(i * 13 % 29);

                if (i % 61 >= 40)
                {
                    raw[i] = (byte)random.Next(256);
                }
            }

            byte[] stream = Compress(raw);

            Assert.True(native.Inflate(stream, size));
            Assert.Equal(raw, native.Output.ToArray());
            Assert.Equal(InflateStatus.Complete, managed.Inflate(stream, DeflateWrapper.Zlib, size).Status);
            Assert.Equal(managed.Output.ToArray(), native.Output.ToArray());
        }
    }

    [Fact]
    public void A_damaged_short_or_long_stream_fails_and_the_next_request_still_inflates()
    {
        using var native = NativeZlibInflater.Create();
        Assert.NotNull(native);

        byte[] raw = new byte[10000];
        new Random(3).NextBytes(raw.AsSpan(0, 5000));
        byte[] stream = Compress(raw);

        // Longer than the request: the stream does not end within its size.
        Assert.False(native.Inflate(stream, raw.Length - 1));

        // Shorter: the request expects more than the stream holds.
        Assert.False(native.Inflate(stream, raw.Length + 1));

        // Cut off before its trailer.
        Assert.False(native.Inflate(stream.AsSpan(0, stream.Length - 3), raw.Length));

        // A wrong Adler-32 trailer.
        byte[] damaged = (byte[])stream.Clone();
        damaged[^1] ^= 0xFF;
        Assert.False(native.Inflate(damaged, raw.Length));

        Assert.True(native.Inflate(stream, raw.Length));
        Assert.Equal(raw, native.Output.ToArray());
    }

    private static byte[] Compress(byte[] raw)
    {
        var output = new MemoryStream();

        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return output.ToArray();
    }
}
