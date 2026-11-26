// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using FiveStar.Loader.Shims.D3D12X.Binding;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// With <c>--trace-views</c>, for a draw: the views its material names, which no table holds. The G-buffer programs
/// (PS_DeferredTextured_c5fd36bb, read in its HLSL) load the V# of the materials' buffer from table 0 + 0xac0, read
/// view indices in it at the byte offset in user SGPR 14 (+0x4 colour, +0x10 normal, +0x0, +0x14, +0xc) and load each
/// T# from the window at SGPR 15 + index * 32. Each index is logged with what its record resolves to, its view's
/// levels, and its texture's address, desc, uploaded hash and first bytes in game memory. CPU only.
/// </summary>
internal static unsafe class MaterialViewTrace
{
    private const uint TableSgpr = 8;
    private const uint MaterialRecord = 0xac0 / DescriptorWindow.ViewRecordBytes;
    private const uint OffsetSgpr = 14;
    private const uint ViewsSgpr = 15;
    private const int Indices = 6;
    private const int ShownSgprs = 16;

    private const int RecordsDword = 2;
    private const uint HighMask = 0xFFFF;
    private const int HighShift = 32;
    private const int MostDetailedMipDword = 4;
    private const int MipLevelsDword = 5;

    /// <summary>Appends the user SGPRs and the material's views of a graphics list whose user data is <paramref name="data"/>.</summary>
    public static void Append(StringBuilder text, ShaderVisibleHeap views, uint* data)
    {
        text.Append(CultureInfo.InvariantCulture, $"\n      user SGPRs 8 to 23: {string.Join(' ', Enumerable.Range((int)TableSgpr, ShownSgprs).Select(sgpr => data[sgpr].ToString("x8", CultureInfo.InvariantCulture)))}");
        uint table = data[TableSgpr];
        uint window = data[ViewsSgpr];

        if (table >= DescriptorWindow.NullTableBase || window >= DescriptorWindow.NullTableBase || window % DescriptorWindow.ViewRecordBytes != 0)
        {
            return;
        }

        uint record = ((table - DescriptorWindow.ViewBase) / DescriptorWindow.ViewRecordBytes) + MaterialRecord;
        ulong high = record < views.Count ? views.RecordDword(record, 1) & HighMask : 0;

        if (high == 0 || data[OffsetSgpr] + ((ulong)Indices * sizeof(uint)) > views.RecordDword(record, RecordsDword))
        {
            return;
        }

        ulong address = (views.RecordDword(record, 0) | (high << HighShift)) + data[OffsetSgpr];
        uint* indices = stackalloc uint[Indices];
        nuint done = 0;

        if (Native.Kernel32.ReadProcessMemory(Native.Kernel32.GetCurrentProcess(), (nint)address, indices, Indices * sizeof(uint), &done) == 0)
        {
            return;
        }

        for (int i = 0; i < Indices; i++)
        {
            uint named = ((window - DescriptorWindow.ViewBase) / DescriptorWindow.ViewRecordBytes) + indices[i];
            string what = named < views.Count ? $"{ViewTrace.Resolve(views, named)}{Texture(views.Source(named))}" : "outside the heap";
            text.Append(CultureInfo.InvariantCulture, $"\n      material 0x{address:x} +0x{i * sizeof(uint):x}: index {indices[i]} (record {named}): {what}");
        }
    }

    /// <summary>"; view mips a+n of the texture at ..., hash, first bytes" for a handle holding a registered texture's view; else empty.</summary>
    private static string Texture(nint handle)
    {
        var textures = ShimContext.Runtime.D3D12X.Textures;
        var held = textures.Views.At(handle);

        if (held is null)
        {
            return string.Empty;
        }

        var view = held.Value;
        var texture = view.Texture;
        var description = view.Description;
        int* dwords = (int*)&description;
        string levels = view.Described ? $"{dwords[MostDetailedMipDword]}+{dwords[MipLevelsDword]}" : "all";

        return string.Create(CultureInfo.InvariantCulture, $"; view mips {levels} of the texture at 0x{texture.Address:x} ({texture.Console.Width}x{texture.Console.Height}, {texture.Console.MipLevels} mips, 0x{texture.Size:x} bytes, GPU-written {texture.GpuWritten}, uploaded hash 0x{texture.UploadedHash:x}, first bytes in memory 0x{textures.FirstBytes(texture):x16})");
    }
}
