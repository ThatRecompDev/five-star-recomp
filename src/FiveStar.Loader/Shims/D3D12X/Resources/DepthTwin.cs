// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>A depth texture's colour twin (<see cref="DepthTwins"/>): the desktop depth, the twin, its format and size.</summary>
internal sealed class DepthTwin(nint depth, nint twin, int format, uint width, uint height)
{
    private int _dirty;
    private int _used;

    public nint Depth { get; } = depth;

    public nint Twin { get; } = twin;

    public int Format { get; } = format;

    /// <summary>Whether this is the stencil plane's twin (R8_UINT), written into the stencil instead of the depth.</summary>
    public bool Stencil => Format == DepthTwins.Uint8;

    /// <summary>The buffer the plane went through on its way into the twin (<see cref="DepthTwinFill"/>); kept for the twin's life.</summary>
    public IDisposable? FillBuffer { get; set; }

    public uint Width { get; } = width;

    public uint Height { get; } = height;

    /// <summary>The copy pass's own writable depth-stencil view of the depth (<see cref="DepthTwinCopy"/>); 0 before the first copy.</summary>
    public nint CopyView { get; set; }

    /// <summary>The depth texture's home state (where each list starts it, <see cref="States.ResourceBarrierSlots"/>).</summary>
    public int Home { get; set; }

    /// <summary>The desktop CPU handle of the twin's UAV in the shader-visible view heap; 0 before the game makes one.</summary>
    public nint ViewHandle { get; set; }

    /// <summary>Whether this is the first pass that binds the twin (the plane is copied in before it); later calls are false.</summary>
    public bool TakeFirstUse()
    {
        return Interlocked.Exchange(ref _used, 1) == 0;
    }

    /// <summary>A pass that binds the twin ran since the depth last took the twin's values.</summary>
    public void MarkWritten()
    {
        Volatile.Write(ref _dirty, 1);
    }

    /// <summary>Whether the twin holds values the depth lacks; clears the mark.</summary>
    public bool TakeWritten()
    {
        return Interlocked.Exchange(ref _dirty, 0) != 0;
    }
}
