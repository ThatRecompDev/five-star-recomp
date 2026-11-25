// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>A depth texture's colour twin (<see cref="DepthTwins"/>): the desktop depth, the twin, its format and size.</summary>
internal sealed class DepthTwin(nint depth, nint twin, int format, uint width, uint height)
{
    private int _behind = DepthTwinSides.TwinBehind;

    /// <summary>
    /// Where the sides stand between submissions (<see cref="DepthTwinSides"/> bits): a new twin is behind its plane, so
    /// the first list that uses it fills it.
    /// </summary>
    public int Behind
    {
        get => Volatile.Read(ref _behind);
        set => Volatile.Write(ref _behind, value);
    }

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

    /// <summary>The shader-visible view heap <see cref="ViewHandle"/> is in.</summary>
    public nint ViewHeap { get; set; }

    /// <summary>The game heap index of the twin's UAV; a view written there later is not the twin's.</summary>
    public uint ViewIndex { get; set; }

    /// <summary>The twin's own UAV in the prelude's heap (<see cref="DepthTwinPrelude"/>); -1 before the first prelude copy.</summary>
    public int PreludeSlot { get; set; } = -1;

    /// <summary>The format of the depth-stencil view a list drew with last (the copy into the plane writes through one); 0 before.</summary>
    public int DepthFormat { get; set; }
}
