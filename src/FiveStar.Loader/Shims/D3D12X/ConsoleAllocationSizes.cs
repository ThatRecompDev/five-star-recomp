// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.Xg;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// Sizes of textures in a console layout (0x100 + swizzle mode) as the console reports them: the XG layout's size and
/// alignment (docs/xg.md), not the desktop driver's. The game sizes streamed textures with slot 25 (0x140343e80) and
/// reads exactly that many bytes from its archives, whose compressed chunks were cut to the console size; a smaller
/// desktop size ends a read inside a chunk and the game breaks at 0x144ffd879.
/// </summary>
internal static unsafe class ConsoleAllocationSizes
{
    /// <summary>D3D12_TEXTURE_LAYOUT_UNKNOWN.</summary>
    private const int UnknownLayout = 0;

    /// <summary>ALLOW_RENDER_TARGET, ALLOW_DEPTH_STENCIL and ALLOW_UNORDERED_ACCESS.</summary>
    private const int TargetFlags = 0x7;

    /// <summary>DXGI_SAMPLE_DESC.Count of a texture that is not multisampled.</summary>
    private const uint SingleSample = 1;

    /// <summary>
    /// Replaces <paramref name="result"/> (and <paramref name="infos"/>, when given) with the console sizes when every
    /// desc is a texture in a console layout; false, leaving them as they are, otherwise.
    /// </summary>
    public static bool TryApply(ResourceDescription* descriptions, uint count, ResourceAllocationInfo* result, ResourceAllocationInfo1* infos)
    {
        if (count == 0 || !AllConsoleTextures(descriptions, count))
        {
            return false;
        }

        ulong offset = 0;
        ulong alignment = 1;

        for (uint i = 0; i < count; i++)
        {
            XgLayoutBuilder.Build(*(XgResourceDesc*)(descriptions + i), out var surface);
            offset = XgLayoutBuilder.AlignUp(offset, surface.Alignment);

            if (infos != null)
            {
                infos[i] = new ResourceAllocationInfo1 { Offset = offset, Alignment = surface.Alignment, Size = surface.Size };
            }

            offset += surface.Size;
            alignment = Math.Max(alignment, surface.Alignment);
        }

        result->Size = XgLayoutBuilder.AlignUp(offset, alignment);
        result->Alignment = alignment;

        return true;
    }

    /// <summary>
    /// The console size of a texture the game creates committed, render and depth targets included (XG lays those out
    /// with the mode it suggests); false for buffers and multisampled textures, which keep the desktop size.
    /// </summary>
    public static bool TryCommittedTextureSize(ResourceDescription* description, out ulong size)
    {
        size = 0;

        if (description->Dimension == ResourceDescription.BufferDimension || description->SampleCount > SingleSample)
        {
            return false;
        }

        XgLayoutBuilder.Build(*(XgResourceDesc*)description, out var surface);
        size = surface.Size;

        return true;
    }

    /// <summary>
    /// Whether a desc is a texture the console lays out with XG: its layout field carries a swizzle mode, or it is
    /// UNKNOWN on a texture that is neither render target, depth target nor UAV (XG suggests the mode; the game sizes
    /// streamed textures so, 0x1451fbd80 builds their desc without a layout).
    /// </summary>
    public static bool IsConsoleTexture(in ResourceDescription description)
    {
        if (description.Dimension == ResourceDescription.BufferDimension)
        {
            return false;
        }

        return (uint)description.Layout >= XgSwizzleModes.ModeBase || (description.Layout == UnknownLayout && (description.Flags & TargetFlags) == 0);
    }

    private static bool AllConsoleTextures(ResourceDescription* descriptions, uint count)
    {
        for (uint i = 0; i < count; i++)
        {
            if (!IsConsoleTexture(descriptions[i]))
            {
                return false;
            }
        }

        return true;
    }
}
