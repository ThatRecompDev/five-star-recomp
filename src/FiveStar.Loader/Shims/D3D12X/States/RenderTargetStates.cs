// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.States;

/// <summary>
/// Render targets that rest in UNORDERED_ACCESS (a texture with both flags, <see cref="ResourceStates.Home"/>, so compute
/// lists can use it): a direct list that binds or clears one moves it to RENDER_TARGET first, and Close moves it home
/// again (<see cref="ResourceBarrierSlots"/>). The game draws to its targets without a barrier, as the console allows.
/// Render-target views are known by the desktop handle they were written to, per runtime (a handle of another runtime's heap may name a freed resource).
/// </summary>
internal sealed unsafe class RenderTargetStates
{
    /// <summary>ID3D12Device::GetDescriptorHandleIncrementSize and D3D12_DESCRIPTOR_HEAP_TYPE_RTV (d3d12.h).</summary>
    private const int IncrementSlot = 15;
    private const int RenderTargetHeapType = 2;

    private readonly ConcurrentDictionary<nint, (nint Resource, int Home)> _views = new();
    private uint _increment;

    /// <summary>
    /// A render-target view of the game's <paramref name="resource"/> was written at desktop handle <paramref name="handle"/>:
    /// kept when the resource rests in UNORDERED_ACCESS, as its desktop resource and home (no wrapper is read later).
    /// </summary>
    public void Note(XboxObjects objects, nint handle, nint resource)
    {
        var wrapper = objects.AsResource(resource);

        if (wrapper == null || wrapper->HomeState != ResourceStates.UnorderedAccess || (wrapper->Flags & ResourceStates.AllowRenderTarget) == 0)
        {
            _views.TryRemove(handle, out _);

            return;
        }

        _views[handle] = (objects.Unwrap(resource), wrapper->HomeState);
    }

    /// <summary>
    /// Before <paramref name="list"/> (the desktop list of <paramref name="self"/>) draws to the <paramref name="count"/>
    /// targets at <paramref name="handles"/> (one handle to a range when <paramref name="single"/>): each that rests in
    /// UNORDERED_ACCESS is moved to RENDER_TARGET.
    /// </summary>
    [HotPath]
    public void Ensure(XboxObject* self, nint list, uint count, nint* handles, bool single)
    {
        if (_views.IsEmpty || handles == null || count == 0)
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
            Ensure(list, states, Handle(handles, i, single));
        }
    }

    [HotPath]
    private void Ensure(nint list, ListResourceStates states, nint handle)
    {
        if (!_views.TryGetValue(handle, out var view))
        {
            return;
        }

        int current = states.Current(view.Resource, view.Home);

        if (current == ResourceStates.RenderTarget)
        {
            return;
        }

        BarrierWriter.Transition(list, view.Resource, current, ResourceStates.RenderTarget);
        states.Set(view.Resource, view.Home, ResourceStates.RenderTarget);
    }

    [HotPath]
    private nint Handle(nint* handles, uint index, bool single)
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
