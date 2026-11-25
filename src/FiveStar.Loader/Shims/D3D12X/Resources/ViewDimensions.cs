// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// Whether a game SRV or UAV desc's view dimension fits its desktop resource's dimension (d3d12.h): a 3D view of a 2D
/// texture, or the reverse, is not a valid desktop view, and programs read zeros through it and their stores are lost.
/// The scattering pass's distance volume (record 0x6a0, a 2304x576 R16_FLOAT texture its programs read and write with
/// 3D image instructions) stayed all zero in w82; a mismatch is logged once per resource dimension and view dimension.
/// </summary>
internal static unsafe class ViewDimensions
{
    /// <summary>ID3D12Resource::GetDesc (d3d12.h).</summary>
    private const int GetDescSlot = 10;

    /// <summary>D3D12_RESOURCE_DIMENSION: BUFFER, TEXTURE1D, TEXTURE2D, TEXTURE3D.</summary>
    private const int Buffer = 1;
    private const int Texture1D = 2;
    private const int Texture2D = 3;
    private const int Texture3D = 4;

    /// <summary>The ViewDimension dword of D3D12_SHADER_RESOURCE_VIEW_DESC and D3D12_UNORDERED_ACCESS_VIEW_DESC.</summary>
    private const int ViewDimensionDword = 1;

    /// <summary>Dwords of the view desc the warning shows: format, dimension, and the union's first four.</summary>
    private const int ShownDwords = 7;

    /// <summary>The resource dimension each D3D12_SRV_DIMENSION takes (0 unknown; 11 is an acceleration structure, a buffer).</summary>
    private static readonly int[] ShaderResource = [0, Buffer, Texture1D, Texture1D, Texture2D, Texture2D, Texture2D, Texture2D, Texture3D, Texture2D, Texture2D, Buffer];

    /// <summary>The resource dimension each D3D12_UAV_DIMENSION takes (6 and 7 are 2D multisample views).</summary>
    private static readonly int[] UnorderedAccess = [0, Buffer, Texture1D, Texture1D, Texture2D, Texture2D, Texture2D, Texture2D, Texture3D];

    /// <summary>The resource dimension <paramref name="viewDimension"/> takes, or 0 when unknown.</summary>
    public static int ResourceDimension(int viewDimension, bool unordered)
    {
        int[] table = unordered ? UnorderedAccess : ShaderResource;

        if (viewDimension < 0 || viewDimension >= table.Length)
        {
            return 0;
        }

        return table[viewDimension];
    }

    /// <summary>Whether a view of <paramref name="viewDimension"/> does not fit a resource of <paramref name="resourceDimension"/>.</summary>
    public static bool Mismatch(int resourceDimension, int viewDimension, bool unordered)
    {
        int expected = ResourceDimension(viewDimension, unordered);

        return expected != 0 && resourceDimension != 0 && expected != resourceDimension;
    }

    /// <summary>
    /// After a game view <paramref name="description"/> of desktop <paramref name="resource"/> was written: a warning when
    /// its view dimension does not fit the resource. Nothing without a desc or a resource.
    /// </summary>
    [HotPath]
    public static void Check(nint resource, void* description, bool unordered)
    {
        if (resource == 0 || description == null)
        {
            return;
        }

        ResourceDescription desc;
        _ = ((delegate* unmanaged<nint, ResourceDescription*, ResourceDescription*>)ComObject.Slot(resource, GetDescSlot))(resource, &desc);
        int viewDimension = ((int*)description)[ViewDimensionDword];

        if (Mismatch(desc.Dimension, viewDimension, unordered))
        {
            Warn(resource, desc, (int*)description, unordered);
        }
    }

    /// <summary>"view dimension 8 (3D) of a 2D resource 0x... 2304x576x1 format 54: view desc ...", once per kind and dimensions.</summary>
    public static string Describe(nint resource, ResourceDescription desc, int* view, bool unordered)
    {
        string kind = unordered ? "UAV" : "SRV";
        var dwords = string.Join(' ', Enumerable.Range(0, ShownDwords).Select(index => view[index].ToString("x8", CultureInfo.InvariantCulture)));

        return $"[d3d12_xs] {kind} of view dimension {view[ViewDimensionDword]} on resource 0x{resource:x} of dimension {desc.Dimension} "
            + $"({desc.Width}x{desc.Height}x{desc.DepthOrArraySize}, {desc.MipLevels} mips, format {desc.Format}): the desktop makes no valid view, "
            + $"programs read zeros and lose their stores (view desc {dwords})";
    }

    private static void Warn(nint resource, ResourceDescription desc, int* view, bool unordered)
    {
        string key = $"view-dimension {unordered} {desc.Dimension} {view[ViewDimensionDword]} {desc.Width}x{desc.Height}x{desc.DepthOrArraySize}";
        ShimContext.Runtime.D3D12X.Warnings.Warn(key, Describe(resource, desc, view, unordered));
    }
}
