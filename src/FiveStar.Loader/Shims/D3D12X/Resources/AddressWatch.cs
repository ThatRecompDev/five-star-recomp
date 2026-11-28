// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Shims.D3D12X.Present;
using FiveStar.Loader.Shims.D3D12X.States;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// <c>--watch-address &lt;address&gt;[:bytes]</c>: what reaches one range of console memory a texture is made over. Logged:
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

    private static long _events;

    /// <summary>The watched range's start; 0 when off.</summary>
    public static ulong Address { get; set; }

    /// <summary>The watched range's length.</summary>
    public static ulong Bytes { get; set; } = DefaultBytes;

    public static bool Enabled => Address != 0;

    /// <summary>A texture's shape the watch takes the range of when one is made (<c>texture:WxH:format</c>); null for none.</summary>
    public static (uint Width, uint Height, int Format)? Shape { get; set; }

    /// <summary>
    /// <c>address[:bytes]</c> in hex (with or without 0x; bytes default to 1 MiB), or <c>texture:WxH:format</c>: the range
    /// of the first texture made in that shape (a texture's address moves from boot to boot).
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

        Address = Hex(parts[0]);

        if (parts.Length > 1)
        {
            Bytes = Hex(parts[1]);
        }
    }

    /// <summary>A texture was made: when it has <see cref="Shape"/> and nothing is watched yet, its range is.</summary>
    public static void Made(ulong address, ulong bytes, ulong width, uint height, int format)
    {
        var shape = Shape;

        if (Enabled || shape is null || shape.Value.Width != width || shape.Value.Height != height || shape.Value.Format != format)
        {
            return;
        }

        Bytes = bytes;
        Address = address;
        ShimContext.Log?.Info($"  [watch] texture {width}x{height} format {format} made at 0x{address:x} + 0x{bytes:x}: watched");
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

    /// <summary>Whether [<paramref name="address"/>, + <paramref name="bytes"/>) touches the watched range.</summary>
    public static bool Touches(ulong address, ulong bytes)
    {
        return Enabled && address < Address + Bytes && Address < address + bytes;
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
        if (!Watches(objects, destination->Resource))
        {
            return;
        }

        string region = string.Empty;

        if (box != null)
        {
            region = $", box ({box->Left},{box->Top})-({box->Right},{box->Bottom})";
        }

        Note($"a GPU copy into the texture at ({x},{y},{z}) subresource {destination->Subresource}{region}, from {Origin(objects, textures, source)}", Address, Bytes);
    }

    /// <summary>A CopyResourceX (slot 86) into the texture at the watched address, as <see cref="CopiedRegion"/>.</summary>
    public static void CopiedWhole(XboxObjects objects, GpuTextures textures, nint destination, TextureCopyLocation* source)
    {
        if (Watches(objects, destination))
        {
            Note($"a GPU copy of the whole texture (CopyResource), from {Origin(objects, textures, source)}", Address, Bytes);
        }
    }

    private static bool Watches(XboxObjects objects, nint resource)
    {
        var target = objects.AsResource(resource);

        return target != null && target->GameAddress == Address;
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

    /// <summary>The blocks of the game's bytes at the watched range, as counts, when <paramref name="frame"/> is due.</summary>
    public static void Sample(GameMemorySource source, LoaderLog log, long frame)
    {
        if (!Enabled || frame % SampleEvery != 0)
        {
            return;
        }

        byte* copy = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)Bytes);

        try
        {
            long pages = source.Gather(Address, Bytes, copy);
            var (magenta, zero, other) = Count(copy, Bytes);
            log.Info($"  [watch] present {frame}: 0x{Address:x} + 0x{Bytes:x} ({pages} pages mapped; {source.Describe(Address)}): {magenta} magenta BC1 blocks, {zero} zero blocks, {other} other blocks");
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(copy);
        }
    }

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
