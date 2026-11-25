// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// Command-list slots 47 and 48, IASetIndexBuffer and IASetVertexBuffers (desktop 43 and 44). The views carry console
/// GPU addresses (on the console GPU address == CPU address); each becomes the desktop address of the buffer over
/// that memory (<see cref="XboxGameMemoryBuffers.TryTranslate"/>). A view outside known memory is unbound (address
/// and size 0), warned once. Translated vertex programs read their attributes through the input layout, so without these the
/// attributes read 0.
/// </summary>
internal static unsafe class InputBuffers
{
    public const int SetIndexBufferSlot = 47;
    public const int SetVertexBuffersSlot = 48;

    /// <summary>ID3D12GraphicsCommandList::IASetIndexBuffer and IASetVertexBuffers in the desktop vtable (d3d12.h).</summary>
    private const int DesktopSetIndexBuffer = 43;
    private const int DesktopSetVertexBuffers = 44;

    /// <summary>D3D12_IA_VERTEX_INPUT_RESOURCE_SLOT_COUNT (d3d12.h).</summary>
    private const uint MaxVertexBuffers = 32;

    [HotPath]
    [UnmanagedCallersOnly]
    public static void SetIndexBuffer(XboxObject* self, VertexBufferView* view)
    {
        try
        {
            nint inner = self->Inner;
            var set = (delegate* unmanaged<nint, VertexBufferView*, void>)ComObject.Slot(inner, DesktopSetIndexBuffer);
            var state = ListReplayState.Of(self);

            if (view == null)
            {
                state->Set &= ~ListReplayState.IndexBit;
                set(inner, null);

                return;
            }

            var desktop = Translated(*view);
            state->Index = desktop;
            state->Set |= ListReplayState.IndexBit;
            set(inner, &desktop);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "InputBuffers.SetIndexBuffer");
        }
    }

    [HotPath]
    [UnmanagedCallersOnly]
    public static void SetVertexBuffers(XboxObject* self, uint startSlot, uint count, VertexBufferView* views)
    {
        try
        {
            nint inner = self->Inner;
            var set = (delegate* unmanaged<nint, uint, uint, VertexBufferView*, void>)ComObject.Slot(inner, DesktopSetVertexBuffers);

            if (views == null || count == 0 || startSlot >= MaxVertexBuffers)
            {
                Unset(ListReplayState.Of(self), startSlot, count);
                set(inner, startSlot, count, null);

                return;
            }

            uint used = count;

            if (used > MaxVertexBuffers - startSlot)
            {
                used = MaxVertexBuffers - startSlot;
            }

            VertexBufferView* desktop = stackalloc VertexBufferView[(int)MaxVertexBuffers];

            for (uint i = 0; i < used; i++)
            {
                desktop[i] = Translated(views[i]);
            }

            ShimContext.Runtime.D3D12X.DrawTrace?.VertexBuffers(self, startSlot, used, views);
            Keep(ListReplayState.Of(self), startSlot, used, desktop);
            set(inner, startSlot, used, desktop);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "InputBuffers.SetVertexBuffers");
        }
    }

    /// <summary>The desktop views of the slots a split list sets again (<see cref="ListReplayState"/>).</summary>
    [HotPath]
    private static void Keep(ListReplayState* state, uint startSlot, uint count, VertexBufferView* views)
    {
        for (uint i = 0; i < count && startSlot + i < ListReplayState.MaxVertexBuffers; i++)
        {
            *state->VertexView(startSlot + i) = views[i];
            state->VertexMask |= 1u << (int)(startSlot + i);
        }
    }

    [HotPath]
    private static void Unset(ListReplayState* state, uint startSlot, uint count)
    {
        for (uint i = 0; i < count && startSlot + i < ListReplayState.MaxVertexBuffers; i++)
        {
            state->VertexMask &= ~(1u << (int)(startSlot + i));
        }
    }

    /// <summary>The view with its desktop address (<see cref="Fit"/>); one outside known memory is unbound, warned once.</summary>
    [HotPath]
    private static VertexBufferView Translated(VertexBufferView view)
    {
        if (view.Location == 0 || !ShimContext.Runtime.D3D12X.GameMemory.TryFind(view.Location, out var range))
        {
            if (view.Location != 0)
            {
                ShimContext.Runtime.D3D12X.Warnings.Warn("ia-address", "[d3d12_xs] IASetVertexBuffers/IASetIndexBuffer: a GPU address outside known game memory, buffer unbound");
            }

            return Fit(view, null);
        }

        var fitted = Fit(view, range);

        if (fitted.Size != view.Size)
        {
            WarnCut(view, range);
        }

        return fitted;
    }

    private static void WarnCut(VertexBufferView view, AddressRange range)
    {
        ShimContext.Runtime.D3D12X.Warnings.Warn("ia-size", $"[d3d12_xs] IASetVertexBuffers/IASetIndexBuffer: a view of 0x{view.Size:x} bytes at 0x{view.Location:x} runs past its desktop buffer (0x{range.Size:x} bytes from 0x{range.GameAddress:x}): its size is cut to the buffer");
    }

    /// <summary>
    /// The view at its desktop address in <paramref name="range"/>, its size cut to what the range holds after it (a view
    /// past its buffer reads unmapped desktop memory: a page fault, not the zeros the console's range check gives); with
    /// no range, unbound (address and size 0, as the desktop requires).
    /// </summary>
    public static VertexBufferView Fit(VertexBufferView view, AddressRange? range)
    {
        if (range is null || view.Location < range.Value.GameAddress || view.Location - range.Value.GameAddress >= range.Value.Size)
        {
            view.Location = 0;
            view.Size = 0;

            return view;
        }

        ulong offset = view.Location - range.Value.GameAddress;
        ulong left = range.Value.Size - offset;
        view.Location = range.Value.DesktopAddress + offset;

        if (view.Size > left)
        {
            view.Size = (uint)left;
        }

        return view;
    }
}
