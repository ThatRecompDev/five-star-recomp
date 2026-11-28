// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Shims.D3D12X.Present;
using FiveStar.Loader.Shims.D3D12X.States;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// <c>--watch-address &lt;address&gt;[:bytes]</c>: what reaches a range of console memory a texture is made over (or each
/// texture of a shape, <c>texture:WxH:format</c>). Logged:
/// each mark of written memory over it (a DirectStorage request, a page mapped) with whether the texture there takes a
/// refresh from it (not when GPU-written or copied into), each GPU copy into a texture there, and every
/// <see cref="SampleEvery"/> presents the bytes the game holds there: how many 8-byte blocks are BC1 blocks of pure
/// magenta (both endpoint colours 0xF81F), how many are zero, how many are neither. A texture that stays magenta while
/// the game's bytes do not misses a write path. Off by default; debug only.
/// </summary>
internal static unsafe class AddressWatch
{
    /// <summary>Presents between samples of the game's bytes.</summary>
    public const long SampleEvery = 300;

    /// <summary>Events logged one by one, then every <see cref="LogEvery"/>-th.</summary>
    private const long LoggedFirst = 200;
    private const long LogEvery = 100;

    private const int BlockBytes = 8;
    private const uint MagentaEndpoints = 0xF81FF81Fu;
    private const ulong DefaultBytes = 1UL << 20;
    private const string HexPrefix = "0x";
    private const string TexturePrefix = "texture";
    private const int TextureParts = 3;
    private const int FormatPart = 2;

    /// <summary>D3D12_PLACED_SUBRESOURCE_FOOTPRINT within a copy location: format, width, height, then (past depth) row pitch.</summary>
    private const int FootprintFormat = 24;
    private const int FootprintWidth = 28;
    private const int FootprintHeight = 32;
    private const int FootprintPitch = 40;

    /// <summary>Texel rows in a BC block row: a footprint's height is in texels.</summary>
    private const uint BlockRows = 4;

    /// <summary>The most bytes of a copy's source sampled.</summary>
    private const ulong MaxSampled = 1UL << 22;

    /// <summary>The most textures of one shape watched.</summary>
    private const int MaxRanges = 8;

    private static readonly Lock Gate = new();

    private static long _events;
    private static WatchedRange[] _ranges = [];

    public static bool Enabled => Volatile.Read(ref _ranges).Length != 0;

    /// <summary>A texture's shape the watch takes the ranges of as they are made (<c>texture:WxH:format</c>); null for none.</summary>
    public static (uint Width, uint Height, int Format)? Shape { get; set; }

    /// <summary>
    /// <c>address[:bytes]</c> in hex (with or without 0x; bytes default to 1 MiB), or <c>texture:WxH:format</c>: the
    /// ranges of the textures made in that shape, up to <see cref="MaxRanges"/> (a texture's address moves from boot to
    /// boot, and the game may make several: three 5808x5808 BC1 atlases).
    /// </summary>
    public static void Configure(string text)
    {
        var parts = text.Split(':');

        if (parts[0] == TexturePrefix && parts.Length == TextureParts)
        {
            var size = parts[1].Split('x');
            Shape = (uint.Parse(size[0], CultureInfo.InvariantCulture), uint.Parse(size[1], CultureInfo.InvariantCulture), int.Parse(parts[FormatPart], CultureInfo.InvariantCulture));

            return;
        }

        ulong bytes = DefaultBytes;

        if (parts.Length > 1)
        {
            bytes = Hex(parts[1]);
        }

        Volatile.Write(ref _ranges, [new WatchedRange(Hex(parts[0]), bytes)]);
    }

    /// <summary>A texture was made: when it has <see cref="Shape"/>, its range is watched too.</summary>
    public static void Made(ulong address, ulong bytes, ulong width, uint height, int format)
    {
        var shape = Shape;

        if (shape is null || shape.Value.Width != width || shape.Value.Height != height || shape.Value.Format != format)
        {
            return;
        }

        int index;

        lock (Gate)
        {
            var ranges = _ranges;

            if (ranges.Length >= MaxRanges || Array.Exists(ranges, range => range.Address == address))
            {
                return;
            }

            index = ranges.Length;
            Volatile.Write(ref _ranges, [.. ranges, new WatchedRange(address, bytes)]);
        }

        ShimContext.Log?.Info($"  [watch] texture {width}x{height} format {format} made at 0x{address:x} + 0x{bytes:x}: watched as range {index}");
    }

    /// <summary>The index of the watched range starting at <paramref name="address"/>, or -1.</summary>
    private static int RangeAt(ulong address)
    {
        return Array.FindIndex(Volatile.Read(ref _ranges), range => range.Address == address);
    }

    private static ulong Hex(string text)
    {
        string digits = text.Trim();

        if (digits.StartsWith(HexPrefix, StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[HexPrefix.Length..];
        }

        return ulong.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>Whether [<paramref name="address"/>, + <paramref name="bytes"/>) touches a watched range.</summary>
    public static bool Touches(ulong address, ulong bytes)
    {
        foreach (var range in Volatile.Read(ref _ranges))
        {
            if (address < range.Address + range.Bytes && range.Address < address + bytes)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Logs an event over the watched range (the first ones, then every <see cref="LogEvery"/>-th).</summary>
    public static void Note(string what, ulong address, ulong bytes)
    {
        long count = Interlocked.Increment(ref _events);

        if (count <= LoggedFirst || count % LogEvery == 0)
        {
            ShimContext.Log?.Info($"  [watch] #{count} {what}: 0x{address:x} + 0x{bytes:x}");
        }
    }

    /// <summary>
    /// A CopyTextureRegionX (slot 85) into the texture at the watched address, before its sides are unwrapped: where it
    /// lands and what it reads (a buffer's footprint and the blocks of its bytes, or a texture and its state).
    /// </summary>
    public static void CopiedRegion(XboxObjects objects, GpuTextures textures, TextureCopyLocation* destination, uint x, uint y, uint z, TextureCopyLocation* source, CopyBox* box)
    {
        int index = Watched(objects, destination->Resource);

        if (index < 0)
        {
            return;
        }

        string region = string.Empty;

        if (box != null)
        {
            region = $", box ({box->Left},{box->Top})-({box->Right},{box->Bottom})";
        }

        var range = Volatile.Read(ref _ranges)[index];
        Note($"a GPU copy into range {index} at ({x},{y},{z}) subresource {destination->Subresource}{region}, from {Origin(objects, textures, source)}", range.Address, range.Bytes);
    }

    /// <summary>A CopyResourceX (slot 86) into the texture at a watched address, as <see cref="CopiedRegion"/>.</summary>
    public static void CopiedWhole(XboxObjects objects, GpuTextures textures, nint destination, TextureCopyLocation* source)
    {
        int index = Watched(objects, destination);

        if (index >= 0)
        {
            var range = Volatile.Read(ref _ranges)[index];
            Note($"a GPU copy of the whole texture in range {index} (CopyResource), from {Origin(objects, textures, source)}", range.Address, range.Bytes);
        }
    }

    /// <summary>The watched range <paramref name="resource"/> starts, or -1.</summary>
    private static int Watched(XboxObjects objects, nint resource)
    {
        var target = objects.AsResource(resource);

        if (target == null)
        {
            return -1;
        }

        return RangeAt(target->GameAddress);
    }

    /// <summary>The source of a watched copy: a buffer's footprint and bytes, or a texture with its state and bytes.</summary>
    private static string Origin(XboxObjects objects, GpuTextures textures, TextureCopyLocation* source)
    {
        var origin = objects.AsResource(source->Resource);

        if (origin == null)
        {
            return $"desktop resource 0x{source->Resource:x}";
        }

        if (origin->Dimension == ResourceStates.BufferDimension)
        {
            byte* footprint = (byte*)source;
            uint width = *(uint*)(footprint + FootprintWidth);
            uint height = *(uint*)(footprint + FootprintHeight);
            uint pitch = *(uint*)(footprint + FootprintPitch);
            ulong at = origin->GameAddress + source->FootprintOffset;
            ulong bytes = Math.Min((ulong)pitch * ((height + BlockRows - 1) / BlockRows), MaxSampled);

            return $"buffer bytes at 0x{at:x} (footprint {width}x{height}, format {*(int*)(footprint + FootprintFormat)}, pitch {pitch}): {Blocks(textures.Source, at, bytes)}";
        }

        var texture = textures.Registry.Find(origin->GameAddress);

        if (texture is null)
        {
            return $"texture 0x{origin->GameAddress:x} subresource {source->Subresource} (not registered)";
        }

        string state = "filled from memory";

        if (texture.GpuWritten)
        {
            state = "GPU-written";
        }
        else if (texture.CopiedInto)
        {
            state = "copied into";
        }

        var console = texture.Console;

        return $"texture 0x{texture.Address:x} subresource {source->Subresource} ({console.Width}x{console.Height}, format {console.Format}, {state}; its memory: {Blocks(textures.Source, texture.Address, Math.Min(texture.Size, MaxSampled))})";
    }

    /// <summary>The blocks of the game's bytes at <paramref name="address"/> as counts, or that none of them is mapped.</summary>
    private static string Blocks(GameMemorySource source, ulong address, ulong bytes)
    {
        if (bytes == 0)
        {
            return "no bytes";
        }

        byte* copy = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)bytes);

        try
        {
            if (source.Gather(address, bytes, copy) == 0)
            {
                return "not mapped";
            }

            var (magenta, zero, other) = Count(copy, bytes);

            return $"{magenta} magenta, {zero} zero, {other} other blocks";
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(copy);
        }
    }

    /// <summary>The blocks of the game's bytes at each watched range, as counts, when <paramref name="frame"/> is due.</summary>
    public static void Sample(GameMemorySource source, LoaderLog log, long frame)
    {
        if (frame % SampleEvery != 0)
        {
            return;
        }

        var ranges = Volatile.Read(ref _ranges);

        for (int index = 0; index < ranges.Length; index++)
        {
            var (address, bytes) = ranges[index];
            byte* copy = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)bytes);

            try
            {
                long pages = source.Gather(address, bytes, copy);
                var (magenta, zero, other) = Count(copy, bytes);
                log.Info($"  [watch] present {frame}: range {index} 0x{address:x} + 0x{bytes:x} ({pages} pages mapped; {source.Describe(address)}): {magenta} magenta BC1 blocks, {zero} zero blocks, {other} other blocks");
            }
            finally
            {
                System.Runtime.InteropServices.NativeMemory.Free(copy);
            }
        }
    }

    /// <summary>One watched range of console memory.</summary>
    private readonly record struct WatchedRange(ulong Address, ulong Bytes);

    /// <summary>The 8-byte blocks of <paramref name="bytes"/> that are pure magenta BC1, zero, or neither.</summary>
    internal static (long Magenta, long Zero, long Other) Count(byte* bytes, ulong length)
    {
        long magenta = 0;
        long zero = 0;
        long other = 0;

        for (ulong offset = 0; offset + BlockBytes <= length; offset += BlockBytes)
        {
            ulong block = *(ulong*)(bytes + offset);

            if (block == 0)
            {
                zero++;
            }
            else if ((uint)block == MagentaEndpoints)
            {
                magenta++;
            }
            else
            {
                other++;
            }
        }

        return (magenta, zero, other);
    }
}
