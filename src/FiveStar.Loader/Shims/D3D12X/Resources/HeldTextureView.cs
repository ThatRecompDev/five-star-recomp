// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// A view of a registered texture a descriptor holds (<see cref="TextureViewHandles"/>): its SRV or UAV dimension, the
/// desktop resource it names (the texture's, or a shadow's) and, when written with one, its desc, so a release can
/// write it again over the replacing texture.
/// </summary>
internal readonly struct HeldTextureView
{
    public HeldTextureView(GpuTexture texture, int dimension, bool unordered, nint target, bool described, TextureViewDescription description)
    {
        Texture = texture;
        Dimension = dimension;
        Unordered = unordered;
        Target = target;
        Described = described;
        Description = description;
    }

    public GpuTexture Texture { get; }

    public int Dimension { get; }

    /// <summary>Whether it is a UAV (else an SRV).</summary>
    public bool Unordered { get; }

    /// <summary>The desktop resource the view names.</summary>
    public nint Target { get; }

    /// <summary>Whether <see cref="Description"/> holds the desc it was written with (else it was written without one).</summary>
    public bool Described { get; }

    public TextureViewDescription Description { get; }
}
