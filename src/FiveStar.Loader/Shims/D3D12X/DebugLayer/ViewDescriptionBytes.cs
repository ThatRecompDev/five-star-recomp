// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// The 40 bytes of the D3D12_SHADER_RESOURCE_VIEW_DESC or D3D12_UNORDERED_ACCESS_VIEW_DESC a view was written with
/// (<see cref="ViewLiveness"/> keeps them for <see cref="DispatchCaptures"/>); <see cref="Present"/> false for a view
/// written without a desc (the whole resource).
/// </summary>
internal unsafe struct ViewDescriptionBytes
{
    public const int Size = Binding.BufferViewDescription.Size;

    public fixed byte Bytes[Size];

    public bool Present;

    /// <summary>The bytes at <paramref name="description"/>, or none.</summary>
    public static ViewDescriptionBytes Of(void* description)
    {
        var copy = default(ViewDescriptionBytes);

        if (description == null)
        {
            return copy;
        }

        Buffer.MemoryCopy(description, copy.Bytes, Size, Size);
        copy.Present = true;

        return copy;
    }

    /// <summary>The bytes as an array, empty when written without a desc.</summary>
    public readonly byte[] ToArray()
    {
        if (!Present)
        {
            return [];
        }

        fixed (byte* bytes = Bytes)
        {
            return new ReadOnlySpan<byte>(bytes, Size).ToArray();
        }
    }
}
