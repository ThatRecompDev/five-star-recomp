// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;

namespace FiveStar.Graphics.Replay;

/// <summary>
/// Whether a captured view desc fits the resource it names, as CreateShaderResourceView and CreateUnorderedAccessView
/// require: the view's dimension of the resource's, its format the resource's or of a typeless resource's family, its
/// mips and slices inside the resource. A view that does not fit removes the device at creation without a debug-layer
/// message (DXGI_ERROR_INVALID_CALL as the removal reason on an RTX 4090); the replay writes a null view in its place.
/// </summary>
public static class ViewFit
{
    /// <summary>D3D12_SRV_DIMENSION / D3D12_UAV_DIMENSION values (d3d12.h; the same numbers up to TEXTURE3D).</summary>
    private const int Buffer = 1;
    private const int Texture1D = 2;
    private const int Texture1DArray = 3;
    private const int Texture2D = 4;
    private const int Texture2DArray = 5;
    private const int Texture2DMultisampleArray = 7;
    private const int Texture3D = 8;
    private const int TextureCube = 9;
    private const int TextureCubeArray = 10;

    /// <summary>D3D12_RESOURCE_DIMENSION: TEXTURE1D, TEXTURE2D, TEXTURE3D.</summary>
    private const int Resource1D = 2;
    private const int Resource2D = 3;
    private const int Resource3D = 4;

    /// <summary>Dwords of the descs: format, dimension; an SRV's union from dword 4, a UAV's from dword 2.</summary>
    private const int DimensionDword = 1;
    private const int SrvUnion = 4;
    private const int UavUnion = 2;

    /// <summary>The union's fields: an SRV's MostDetailedMip (at <see cref="SrvUnion"/>), MipLevels, FirstArraySlice, ArraySize; a UAV's MipSlice (at <see cref="UavUnion"/>), FirstArraySlice, ArraySize.</summary>
    private const int SrvMipLevels = SrvUnion + 1;
    private const int SrvFirstSlice = SrvUnion + 2;
    private const int SrvArraySize = SrvUnion + 3;
    private const int UavFirstSlice = UavUnion + 1;
    private const int UavArraySize = UavUnion + 2;

    private const int CubeFaces = 6;

    /// <summary>DXGI_FORMAT_R32G32B32A32_FLOAT: the format of a null view that had none it could keep.</summary>
    private const int NullFormat = 2;

    private const uint All = uint.MaxValue;

    /// <summary>Why the desc does not fit <paramref name="resource"/>; null when it does (or has no desc).</summary>
    public static string? Problem(CapturedResource resource, CapturedViewKind kind, ReadOnlySpan<byte> description)
    {
        if (description.Length != CapturedView.DescriptionBytes)
        {
            return null;
        }

        int format = Dword(description, 0);
        int dimension = Dword(description, DimensionDword);

        if (RequiredDimension(dimension) != resource.Dimension)
        {
            return $"view dimension {dimension} of a resource of dimension {resource.Dimension}";
        }

        if (resource.IsBuffer)
        {
            return null;
        }

        if (!FormatFamilies.Views(resource.Format, format))
        {
            return $"view format {format} of a format {resource.Format} texture";
        }

        if (kind == CapturedViewKind.ShaderResource)
        {
            return ShaderResourceRange(resource, description, dimension);
        }

        return UnorderedAccessRange(resource, description, dimension);
    }

    /// <summary>A null view desc of the same kind, format and dimension, one mip and one slice.</summary>
    public static byte[] Null(CapturedResource resource, CapturedViewKind kind, ReadOnlySpan<byte> description)
    {
        var view = new byte[CapturedView.DescriptionBytes];
        int format = Dword(description, 0);
        int dimension = Dword(description, DimensionDword);
        int head = kind == CapturedViewKind.ShaderResource ? SrvUnion : UavUnion;
        description[..(head * sizeof(uint))].CopyTo(view);

        if (format == 0 || (FormatFamilies.Members(format)[0] == format && FormatFamilies.Members(format).Count > 1))
        {
            BinaryPrimitives.WriteInt32LittleEndian(view, NullFormat);
        }

        if (kind == CapturedViewKind.ShaderResource && dimension != Buffer)
        {
            Write(view, SrvMipLevels, 1);

            if (dimension is Texture1DArray or Texture2DArray or TextureCubeArray)
            {
                Write(view, SrvArraySize, 1);
            }
        }
        else if (kind == CapturedViewKind.UnorderedAccess && dimension is Texture1DArray or Texture2DArray or Texture3D)
        {
            Write(view, UavArraySize, 1);
        }

        return view;
    }

    private static string? ShaderResourceRange(CapturedResource resource, ReadOnlySpan<byte> description, int dimension)
    {
        uint most = (uint)Dword(description, SrvUnion);
        uint levels = (uint)Dword(description, SrvMipLevels);

        if (dimension is <= Buffer or (> Texture2DArray and < Texture3D))
        {
            return null;
        }

        if (most >= resource.MipLevels || (levels != All && most + levels > resource.MipLevels))
        {
            return $"mips {most}+{levels} of a texture of {resource.MipLevels}";
        }

        uint first = (uint)Dword(description, SrvFirstSlice);
        uint count = (uint)Dword(description, SrvArraySize);

        if (dimension == TextureCubeArray)
        {
            count *= CubeFaces;
        }

        if (dimension is Texture1DArray or Texture2DArray or TextureCubeArray && count != All && first + count > resource.DepthOrArraySize)
        {
            return $"slices {first}+{count} of a texture of {resource.DepthOrArraySize}";
        }

        return null;
    }

    private static string? UnorderedAccessRange(CapturedResource resource, ReadOnlySpan<byte> description, int dimension)
    {
        uint mip = (uint)Dword(description, UavUnion);

        if (mip >= resource.MipLevels)
        {
            return $"mip {mip} of a texture of {resource.MipLevels}";
        }

        uint first = (uint)Dword(description, UavFirstSlice);
        uint count = (uint)Dword(description, UavArraySize);
        uint slices = resource.DepthOrArraySize;

        if (dimension == Texture3D)
        {
            slices = Math.Max(1u, slices >> (int)mip);
        }

        if (dimension is Texture1DArray or Texture2DArray or Texture3D && count != All && first + count > slices)
        {
            return $"slices {first}+{count} of a texture of {slices}";
        }

        return null;
    }

    /// <summary>The resource dimension a view dimension takes; 0 for one no resource takes.</summary>
    private static int RequiredDimension(int dimension)
    {
        return dimension switch
        {
            Buffer => CapturedResource.BufferDimension,
            Texture1D or Texture1DArray => Resource1D,
            (>= Texture2D and <= Texture2DMultisampleArray) or TextureCube or TextureCubeArray => Resource2D,
            Texture3D => Resource3D,
            _ => 0,
        };
    }

    private static int Dword(ReadOnlySpan<byte> bytes, int index)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(bytes[(index * sizeof(uint))..]);
    }

    private static void Write(byte[] bytes, int index, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), value);
    }
}
