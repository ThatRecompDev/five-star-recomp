// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Numerics;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.Pipelines;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// Device slot 70: an SRV described by a resource desc and a console GPU address. The address resolves to the
/// registered texture there (refreshed from memory; a GPU-written one of the same extent is taken whatever its format),
/// else to a texture made from the desc over readable memory; with neither, or for a depth texture, the descriptor is
/// a null view of the same dimension.
/// </summary>
internal sealed unsafe class AddressShaderViews
{
    /// <summary>ID3D12Device::CreateShaderResourceView (d3d12.h).</summary>
    private const int DesktopShaderResourceViewSlot = 18;

    /// <summary>Views between two log lines with the counters (after the powers of two up to it).</summary>
    private const int SummaryInterval = 4096;

    private readonly GpuTextures _textures;
    private readonly AddressBufferViews _buffers;
    private readonly OnceLog _warnings;
    private readonly LoaderLog _log;

    public AddressShaderViews(GpuTextures textures, AddressBufferViews buffers, OnceLog warnings, LoaderLog log)
    {
        _textures = textures;
        _buffers = buffers;
        _warnings = warnings;
        _log = log;
    }

    /// <summary>Writes the view into <paramref name="handle"/>, a desktop CPU descriptor handle; returns its texture, null for a null view.</summary>
    public GpuTexture? Create(nint device, ResourceDescription* resource, AddressViewDescription* view, nint handle, Binding.BufferRecord record)
    {
        var counters = _textures.Counters;

        long views = counters.Add(GpuResourceEvent.View);

        if (BitOperations.IsPow2(views) || views % SummaryInterval == 0)
        {
            _log.Detail($"  [d3d12_xs] console-address textures: {counters}; {_textures.Registry.Summary}; {_textures.Placed.Summary}; {_textures.MemorySummary}");
        }

        var reason = GpuResourceEvent.ViewNullNoTexture;

        if (IsBuffer(resource) && view->Address != 0 && BufferView(device, view, handle, record, ref reason))
        {
            return null;
        }

        var desktop = *view;
        var texture = Resolve(device, resource, &desktop);
        int viewFormat = DepthFormats.ShaderView(view->Format);
        nint target = 0;

        if (texture is not null)
        {
            desktop.Format = ViewFormats.ForView(texture.DesktopFormat, viewFormat);
            NoteDepth(texture);
            desktop.ComponentMapping = TargetFormats.ComponentMapping(viewFormat, desktop.ComponentMapping);
            target = texture.Desktop;

            // The screen-probe light stand-in fills this R32_UINT texture's UAVs as shared-exponent light (RtStandIns).
            if (Binding.RtStandIns.Enabled && Binding.ImageViewClasses.SharedExponentRead(viewFormat, desktop.Format))
            {
                Binding.UavViews.NoteSharedExponent(target);
            }

            target = Aliased(device, texture, viewFormat, ref desktop);
        }

        if (target == 0 || desktop.Format == ViewFormats.Unknown)
        {
            counters.Add(GpuResourceEvent.ViewNull);
            NoteNull(resource, view, texture, reason);
            desktop.Format = viewFormat;
            target = 0;
            texture = null;
        }

        // A stencil-plane view reads plane 1 and a depth-plane view plane 0, whatever the desc names.
        DepthViewDescriptions.SetPlane((int*)&desktop);
        var create = (delegate* unmanaged<nint, nint, AddressViewDescription*, nint, void>)ComObject.Slot(device, DesktopShaderResourceViewSlot);
        create(device, target, &desktop, handle);
        Noted(handle, texture, target, &desktop);

        return texture;
    }

    /// <summary>
    /// The format the desktop view of a registered texture at the view's address reads in (<see cref="ViewFormats.ForView"/>);
    /// <see cref="ViewFormats.Unknown"/> when no registered texture answers it (one made for the view reads in its own).
    /// </summary>
    public int ReadFormat(ResourceDescription* resource, AddressViewDescription* view)
    {
        if (resource == null || view == null || view->Address == 0 || IsBuffer(resource))
        {
            return ViewFormats.Unknown;
        }

        var texture = _textures.Registry.Find(view->Address);

        if (texture is null || !(texture.Matches(view->Address, *resource) || (texture.GpuWritten && texture.SameExtent(view->Address, *resource))))
        {
            texture = Replaced(texture, view->Address, resource);
        }

        if (texture is null)
        {
            return ViewFormats.Unknown;
        }

        return ViewFormats.ForView(texture.DesktopFormat, DepthFormats.ShaderView(view->Format));
    }

    /// <summary>
    /// The resource the view reads: an R9G9B9E5 view of R32_UINT light reads the texture's R9G9B9E5 shadow, in the view's
    /// format (<see cref="SharedExponentAliases"/>); any other the texture, its lost format noted.
    /// </summary>
    private nint Aliased(nint device, GpuTexture texture, int viewFormat, ref AddressViewDescription desktop)
    {
        if (SharedExponentAliases.Takes(viewFormat, desktop.Format))
        {
            var alias = SharedExponentAliases.For(device, texture);

            if (alias is not null)
            {
                desktop.Format = viewFormat;

                return alias.Shadow;
            }
        }

        NoteLoss(texture, viewFormat, desktop.Format);

        return texture.Desktop;
    }

    /// <summary>
    /// The view written at <paramref name="handle"/>: its texture's handles (<see cref="TextureViewHandles"/>, nulled or
    /// written again before the texture is released), and with <c>--check-view-liveness</c> its shape.
    /// </summary>
    private void Noted(nint handle, GpuTexture? texture, nint target, AddressViewDescription* view)
    {
        if (texture is not null)
        {
            _textures.Views.Written(handle, texture, target, view, unordered: false);
        }

        if (DebugLayer.ViewLiveness.Noting)
        {
            DebugLayer.ViewLiveness.Written(handle, target, DebugLayer.ViewLiveness.Shape(target, view, unordered: false), description: view);
        }
    }

    /// <summary>
    /// A buffer view at an address; false (with why in <paramref name="reason"/>) when it gets no desktop buffer. A null
    /// one whose record keeps the heap index is counted apart: only there does a program read the descriptor (a record
    /// with the view's console address is read through the address map).
    /// </summary>
    private bool BufferView(nint device, AddressViewDescription* view, nint handle, Binding.BufferRecord record, ref GpuResourceEvent reason)
    {
        _textures.Counters.Add(GpuResourceEvent.ViewBuffer);
        var outcome = _buffers.Create(device, view, handle, record);

        switch (outcome)
        {
            case BufferViewOutcome.Created:
                return true;
            case BufferViewOutcome.Clamped:
                _textures.Counters.Add(GpuResourceEvent.ViewBufferClamped);

                return true;
            case BufferViewOutcome.Raw:
                _textures.Counters.Add(GpuResourceEvent.ViewBufferRaw);

                return true;
        }

        reason = outcome == BufferViewOutcome.Uncovered ? GpuResourceEvent.ViewNullBufferUncovered : GpuResourceEvent.ViewNullBufferRange;

        if (record.Base == 0)
        {
            _textures.Counters.Add(GpuResourceEvent.ViewNullBufferKeptIndex);
        }

        var desc = (Binding.BufferViewDescription*)view;
        _warnings.Warn($"slot70-buffer-{outcome}-{desc->Format}-{desc->SrvFlags}-{record.Base == 0}", $"[d3d12_xs] ID3D12Device.X::Slot70: a buffer view at 0x{view->Address:x} (format {desc->Format}, first element {desc->SrvFirstElement}, {desc->SrvElements} elements, stride {desc->SrvStride}, flags {desc->SrvFlags}; record {Based(record)}): {outcome}, null descriptor");

        return false;
    }

    private static string Based(Binding.BufferRecord record)
    {
        if (record.Base == 0)
        {
            return "keeps the heap index: programs read this descriptor";
        }

        return "holds the console address: programs read through the address map";
    }

    /// <summary>--typeless-depth: a depth texture in its typeless family reads its depth plane where the typed one read nothing.</summary>
    private void NoteDepth(GpuTexture texture)
    {
        if (DepthFormats.TypelessDepth && texture.Console.Format != texture.DesktopFormat && DepthFormats.TypelessResource(texture.Console.Format) == texture.DesktopFormat)
        {
            _textures.Counters.Add(GpuResourceEvent.ViewDepthResolved);
        }
    }

    /// <summary>
    /// Counts a null view by why it is null, and names each kind once per pair of formats with the shapes involved: a
    /// texture view inside a GPU-written texture it does not match, one of a typed depth texture, or one with no texture.
    /// </summary>
    private void NoteNull(ResourceDescription* resource, AddressViewDescription* view, GpuTexture? texture, GpuResourceEvent reason)
    {
        var inside = _textures.Registry.Find(view->Address);
        string shape = "without a desc";

        if (resource != null)
        {
            shape = $"{resource->Width}x{resource->Height}x{resource->DepthOrArraySize}, {resource->MipLevels} mips, format {resource->Format}";
        }

        if (reason == GpuResourceEvent.ViewNullNoTexture && texture is not null)
        {
            reason = GpuResourceEvent.ViewNullDepth;
        }
        else if (reason == GpuResourceEvent.ViewNullNoTexture && inside is not null && inside.GpuWritten)
        {
            reason = GpuResourceEvent.ViewNullInsideWritten;
        }

        _textures.Counters.Add(reason);

        if (reason is GpuResourceEvent.ViewNullBufferUncovered or GpuResourceEvent.ViewNullBufferRange)
        {
            return;
        }

        string where = inside is null ? "no texture" : $"in a {Written(inside)} texture at 0x{inside.Address:x} of format {inside.DesktopFormat}, {inside.Console.Width}x{inside.Console.Height}x{inside.Console.DepthOrArraySize}, {inside.Console.MipLevels} mips";
        _warnings.Warn($"slot70-null-{reason}-{inside?.DesktopFormat}-{view->Format}", $"[d3d12_xs] ID3D12Device.X::Slot70: a view at 0x{view->Address:x} in format {view->Format} of a texture {shape}, {where}: null descriptor ({Explain(reason)}, counted as {reason})");
    }

    private static string Explain(GpuResourceEvent reason)
    {
        return reason switch
        {
            GpuResourceEvent.ViewNullBufferUncovered => "at an address no desktop buffer covers",
            GpuResourceEvent.ViewNullBufferRange => "off its buffer's element size, past its end or a raw view off 16 bytes",
            GpuResourceEvent.ViewNullInsideWritten => "inside a GPU-written texture whose extent it does not match",
            GpuResourceEvent.ViewNullDepth => "a typed depth texture takes no SRV",
            _ => "no texture there, and none could be made from its memory",
        };
    }

    /// <summary>Counts a view that reads in another format than the game asked for, and names each pair of formats once.</summary>
    private void NoteLoss(GpuTexture texture, int viewFormat, int created)
    {
        var loss = ViewFormats.Loss(texture.DesktopFormat, viewFormat, created);

        // A format with a render-target substitute is read in the substitute on purpose (TargetFormats); a shared-exponent
        // view of an R32_UINT texture is decoded by the program (--typed-image-views).
        bool decoded = Binding.ImageViewClasses.Read(Binding.BufferRecord.None, viewFormat, created).ImageClass == Binding.ImageViewClasses.SharedExponent;

        if (loss == ViewFormatLoss.None || TargetFormats.HasSubstitute(viewFormat) || decoded)
        {
            return;
        }

        var kind = loss == ViewFormatLoss.Cast ? GpuResourceEvent.ViewCastLost : GpuResourceEvent.ViewReinterpretLost;
        _textures.Counters.Add(kind);
        _warnings.Warn($"slot70-format-{texture.DesktopFormat}-{viewFormat}", $"[d3d12_xs] ID3D12Device.X::Slot70: a view in format {viewFormat} of a {Written(texture)} texture in format {texture.DesktopFormat} reads in {created} ({loss}: {Meaning(loss)}); counted as {kind}");
    }

    private static string Written(GpuTexture texture)
    {
        if (texture.GpuWritten)
        {
            return "GPU-written";
        }

        return "read-only";
    }

    private static string Meaning(ViewFormatLoss loss)
    {
        if (loss == ViewFormatLoss.Cast)
        {
            return "the sRGB decode or the integer reading is lost";
        }

        return "its bytes are not read as the view's format";
    }

    /// <summary>A buffer desc, or a texture desc without a format (raw or structured views): not a texture to make.</summary>
    private static bool IsBuffer(ResourceDescription* resource)
    {
        return resource != null && (resource->Dimension == ResourceDescription.BufferDimension || resource->Format == ViewFormats.Unknown);
    }

    /// <summary>A replaced GPU-written texture of the desc's extent at <paramref name="address"/>, when <paramref name="newest"/> is GPU-written and does not answer it.</summary>
    private GpuTexture? Replaced(GpuTexture? newest, ulong address, ResourceDescription* resource)
    {
        if (newest is null || !newest.GpuWritten)
        {
            return null;
        }

        return _textures.Registry.FindReplaced(address, *resource);
    }

    private GpuTexture? Resolve(nint device, ResourceDescription* resource, AddressViewDescription* view)
    {
        ulong address = view->Address;

        if (resource == null || address == 0)
        {
            return null;
        }

        if (IsBuffer(resource))
        {
            return null;
        }

        var texture = _textures.Registry.Find(address);

        if (texture is not null && (texture.Matches(address, *resource) || (texture.GpuWritten && texture.SameExtent(address, *resource))))
        {
            _textures.Counters.Add(GpuResourceEvent.ViewResolved);
            _textures.RefreshForView(texture);

            return texture;
        }

        // A view of a GPU-written texture a newer one replaced at this address reads the old one: the game still draws
        // into it through its depth or target views (w120c: format-53 shadow pages re-made at other sizes, 48 bound after).
        var replaced = Replaced(texture, address, resource);

        if (replaced is not null)
        {
            _textures.Counters.Add(GpuResourceEvent.ViewResolvedReplaced);

            return replaced;
        }

        // A view inside a texture the GPU writes is not memory to read: making a texture there would retire it.
        if (texture is not null && texture.GpuWritten)
        {
            return null;
        }

        return Made(device, texture, resource, view);
    }

    /// <summary>
    /// The read-only texture of the levels the view reads (<see cref="ResidentLevels"/>, which renumbers
    /// <paramref name="view"/>'s): <paramref name="registered"/> when it is that one, else one made from the memory.
    /// </summary>
    private GpuTexture? Made(nint device, GpuTexture? registered, ResourceDescription* resource, AddressViewDescription* view)
    {
        var levels = *resource;

        if (ResidentLevels.Reduce(ref levels, ref *view))
        {
            _textures.Counters.Add(GpuResourceEvent.ViewResident);

            if (registered is not null && registered.Matches(view->Address, levels))
            {
                _textures.Counters.Add(GpuResourceEvent.ViewResolved);
                _textures.RefreshForView(registered);

                return registered;
            }
        }

        var texture = _textures.CreateForView(device, view->Address, &levels, view->Format);

        if (texture is not null)
        {
            _textures.Counters.Add(GpuResourceEvent.ViewCreatedTexture);
        }

        return texture;
    }
}
