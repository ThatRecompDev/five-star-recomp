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
/// <para>
/// Depth-stencil views likewise: a depth the game moved to a read state without DEPTH_READ (GENERIC_READ, or shader
/// resource), as the console's layouts allow for a depth it then tests against, is moved to DEPTH_READ with those
/// read states when a direct list binds a read-only view of it, and to DEPTH_WRITE when the view is writable.
/// </para>
/// </summary>
internal sealed unsafe class RenderTargetStates
{
    /// <summary>ID3D12Device::GetDescriptorHandleIncrementSize and D3D12_DESCRIPTOR_HEAP_TYPE_RTV (d3d12.h).</summary>
    private const int IncrementSlot = 15;
    private const int RenderTargetHeapType = 2;

    /// <summary>D3D12_DEPTH_STENCIL_VIEW_DESC.Flags (dword 2) and D3D12_DSV_FLAG_READ_ONLY_DEPTH | _READ_ONLY_STENCIL.</summary>
    private const int DepthViewFlagsDword = 2;
    private const int ReadOnlyFlags = 0x3;

    /// <summary>The read states DEPTH_READ is kept with: both shader-resource states and COPY_SOURCE.</summary>
    private const int ReadKeptWithDepth = ResourceStates.NonPixelShaderResource | ResourceStates.PixelShaderResource | ResourceStates.CopySource;

    private readonly ConcurrentDictionary<nint, (nint Resource, int Home)> _views = new();
    private readonly ConcurrentDictionary<nint, (nint Resource, int Home, bool ReadOnly)> _depthViews = new();
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

    /// <summary>
    /// A depth-stencil view of the game's <paramref name="resource"/> in <paramref name="description"/> (null: the whole
    /// resource, writable) was written at desktop handle <paramref name="handle"/>.
    /// </summary>
    public void NoteDepth(XboxObjects objects, nint handle, nint resource, void* description)
    {
        var wrapper = objects.AsResource(resource);

        if (wrapper == null || (wrapper->Flags & ResourceStates.AllowDepthStencil) == 0)
        {
            _depthViews.TryRemove(handle, out _);

            return;
        }

        bool readOnly = description != null && (((int*)description)[DepthViewFlagsDword] & ReadOnlyFlags) == ReadOnlyFlags;
        _depthViews[handle] = (objects.Unwrap(resource), wrapper->HomeState, readOnly);
    }

    /// <summary>Before a direct list draws with the depth-stencil view at <paramref name="handle"/>: the depth in a state the view can use.</summary>
    [HotPath]
    public void EnsureDepth(XboxObject* self, nint list, nint handle)
    {
        if (handle == 0 || !_depthViews.TryGetValue(handle, out var view))
        {
            return;
        }

        var states = ShimContext.Runtime.D3D12X.ListStates.For(self);

        if ((states.LegalStates & ResourceStates.DepthRead) == 0)
        {
            return;
        }

        int current = states.Current(view.Resource, view.Home);
        int needed = DepthState(current, view.ReadOnly);

        if (needed == current)
        {
            return;
        }

        BarrierWriter.Transition(list, view.Resource, current, needed);
        states.Set(view.Resource, view.Home, needed);
    }

    /// <summary>
    /// The state a depth in <paramref name="current"/> needs for a view: as it is when it holds DEPTH_WRITE, or DEPTH_READ
    /// for a read-only view; else DEPTH_READ with its shader-resource and copy-source states (read-only), or DEPTH_WRITE.
    /// </summary>
    public static int DepthState(int current, bool readOnly)
    {
        if ((current & ResourceStates.DepthWrite) != 0 || (readOnly && (current & ResourceStates.DepthRead) != 0))
        {
            return current;
        }

        if (readOnly)
        {
            return ResourceStates.DepthRead | (current & ReadKeptWithDepth);
        }

        return ResourceStates.DepthWrite;
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
