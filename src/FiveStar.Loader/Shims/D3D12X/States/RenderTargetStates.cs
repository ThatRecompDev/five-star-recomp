// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.States;

/// <summary>
/// Render targets that rest in UNORDERED_ACCESS (a texture with both flags, <see cref="ResourceStates.Home"/>, so compute
/// lists can use it): a direct list that binds or clears one moves it to RENDER_TARGET first, and Close moves it home
/// again (<see cref="ResourceBarrierSlots"/>). The game draws to its targets without a barrier, as the console allows.
/// Render-target views are known by the desktop handle they were written to.
/// </summary>
internal static unsafe class RenderTargetStates
{
    /// <summary>ID3D12Device::GetDescriptorHandleIncrementSize and D3D12_DESCRIPTOR_HEAP_TYPE_RTV (d3d12.h).</summary>
    private const int IncrementSlot = 15;
    private const int RenderTargetHeapType = 2;

    private static readonly ConcurrentDictionary<nint, nint> Views = new();
    private static uint _increment;

    /// <summary>A render-target view of the game's <paramref name="resource"/> was written at desktop handle <paramref name="handle"/>.</summary>
    public static void Note(nint handle, nint resource)
    {
        Views[handle] = resource;
    }

    /// <summary>
    /// Before <paramref name="list"/> (the desktop list of <paramref name="self"/>) draws to the <paramref name="count"/>
    /// targets at <paramref name="handles"/> (one handle to a range when <paramref name="single"/>): each that rests in
    /// UNORDERED_ACCESS is moved to RENDER_TARGET.
    /// </summary>
    [HotPath]
    public static void Ensure(XboxObject* self, nint list, uint count, nint* handles, bool single)
    {
        if (Views.IsEmpty || handles == null || count == 0)
        {
            return;
        }

        var d3d12x = ShimContext.Runtime.D3D12X;
        var states = d3d12x.ListStates.For(self);

        if ((states.LegalStates & ResourceStates.RenderTarget) == 0)
        {
            return;
        }

        for (uint i = 0; i < count; i++)
        {
            Ensure(d3d12x.Objects, list, states, Handle(handles, i, single));
        }
    }

    [HotPath]
    private static void Ensure(XboxObjects objects, nint list, ListResourceStates states, nint handle)
    {
        if (!Views.TryGetValue(handle, out nint game))
        {
            return;
        }

        var wrapper = objects.AsResource(game);

        if (wrapper == null || wrapper->HomeState != ResourceStates.UnorderedAccess || (wrapper->Flags & ResourceStates.AllowRenderTarget) == 0)
        {
            return;
        }

        nint resource = objects.Unwrap(game);
        int current = states.Current(resource, wrapper->HomeState);

        if (current == ResourceStates.RenderTarget)
        {
            return;
        }

        BarrierWriter.Transition(list, resource, current, ResourceStates.RenderTarget);
        states.Set(resource, wrapper->HomeState, ResourceStates.RenderTarget);
    }

    [HotPath]
    private static nint Handle(nint* handles, uint index, bool single)
    {
        if (!single)
        {
            return handles[index];
        }

        if (_increment == 0)
        {
            nint device = ShimContext.Runtime.D3D12X.Objects.DesktopDevice;
            _increment = ((delegate* unmanaged<nint, int, uint>)ComObject.Slot(device, IncrementSlot))(device, RenderTargetHeapType);
        }

        return handles[0] + (nint)(index * _increment);
    }
}
