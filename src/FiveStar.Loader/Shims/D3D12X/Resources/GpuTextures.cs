// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims.D3D12X.States;
using FiveStar.Loader.Shims.Xg;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// Desktop textures for console memory (docs/gpu-resources.md): created for slot 133 or for a slot-70 view, registered
/// by address, and filled from the game's bytes through their XG layout at creation and whenever a refresh finds the
/// bytes changed. GPU-written textures (render target, depth-stencil, UAV) are never filled from memory.
/// </summary>
internal sealed unsafe class GpuTextures : IDisposable
{
    /// <summary>IUnknown::AddRef and ID3D12Device::CreateCommittedResource (d3d12.h).</summary>
    private const int AddRefSlot = 1;
    private const int DesktopCreateCommittedResourceSlot = 27;

    /// <summary>D3D12_HEAP_TYPE_DEFAULT and D3D12_HEAP_FLAG_NONE.</summary>
    private const int DefaultHeapType = 1;
    private const int NoHeapFlags = 0;

    /// <summary>D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET | ALLOW_DEPTH_STENCIL | ALLOW_UNORDERED_ACCESS: the GPU writes the texture.</summary>
    private const int GpuWrittenFlags = 0x7;

    /// <summary>D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE | NON_PIXEL_SHADER_RESOURCE, where a texture made for a view rests.</summary>
    private const int ShaderResourceState = 0xC0;

    /// <summary>Failed fills logged one by one; the counters count them all.</summary>
    private const long LoggedFailures = 8;

    private const double BytesPerGigabyte = 1UL << 30;

    private static readonly Guid ResourceIid = new("696442be-a72e-4059-bc79-5b5c98040fad");

    [ThreadStatic]
    private static byte* _kernelCopy;

    [ThreadStatic]
    private static nuint _kernelCopyBytes;

    private readonly object _watchGate = new();
    private readonly GameMemorySource _source;
    private readonly DesktopResourceDescriptions _descriptions;
    private readonly LoaderLog _log;
    private GpuTexture[] _watched = [];
    private long _unreadableLogged;
    private long _unmappedViewBytes;
    private long _checkedFrame = -1;

    public GpuTextures(GameMemorySource source, DesktopResourceDescriptions descriptions, LoaderLog log)
    {
        _source = source;
        _descriptions = descriptions;
        _log = log;
        Writes = new TextureRefreshWorker(this, source);
        Registry.Releasing = Refund;
    }

    public GpuTextureRegistry Registry { get; } = GpuTextureRegistry.OfQueues();

    public GpuResourceCounters Counters { get; } = new();

    public GpuTextureUploader Uploader { get; } = new();

    /// <summary>The shared heaps of placed read-only textures (<c>--placed-textures</c>).</summary>
    public PlacedTextureHeaps Placed { get; } = new();

    /// <summary>
    /// Slot 133: the desktop texture for <paramref name="address"/> in its home state <paramref name="state"/>
    /// (<see cref="ResourceStates.Home"/> of <paramref name="initialLayout"/>), registered and, unless GPU-written,
    /// filled from memory. Returns the HRESULT; <paramref name="desktop"/> is the caller's reference.
    /// </summary>
    public int CreatePlaced(nint device, ulong address, ResourceDescription* description, int initialLayout, void* clear, out nint desktop, out int state)
    {
        var desc = _descriptions.ToDesktop(device, description);
        state = ResourceStates.Home(desc.Dimension, desc.Flags, BarrierLayouts.ToState(initialLayout));
        byte* clearCopy = stackalloc byte[DepthViewDescriptions.ClearValueSize];
        bool readOnly = (description->Flags & GpuWrittenFlags) == 0;
        int hr = CreateTexture(device, &desc, state, DepthViewDescriptions.ClearValue(clear, clearCopy, desc.Format), readOnly, out desktop);
        TypelessTargets.NoteMade(desktop, description->Format, desc.Format);

        Counters.Add(GpuResourceEvent.PlacedTexture);

        if (HResult.Failed(hr))
        {
            return hr;
        }

        bool gpuWritten = (description->Flags & GpuWrittenFlags) != 0;
        var texture = Register(address, description, desktop, desc.Format, state, gpuWritten);
        Refresh(device, texture);

        return hr;
    }

    /// <summary>
    /// A read-only texture made for a slot-70 view of <paramref name="viewFormat"/> over readable memory; null when the
    /// memory is not readable or the desktop refuses the desc.
    /// </summary>
    public GpuTexture? CreateForView(nint device, ulong address, ResourceDescription* description, int viewFormat)
    {
        XgLayoutBuilder.Build(ToXg(description), out var layout);

        // A view over an XMem reservation gets its texture even before the pages are mapped: the game streams them in
        // later, and a null descriptor made now would stay null (w105/w108: hundreds of null views of 4x4 to 1024x1024
        // BC textures). Mapping the pages marks the texture written and the next submission fills it.
        long charge = 0;

        if (_source.Find(address, layout.Size) == null)
        {
            if (!_source.InReservation(address))
            {
                return null;
            }

            charge = UnmappedCharge(address, layout.Size);

            if (charge < 0)
            {
                return null;
            }
        }

        // Read-only: without the GPU-written flags, so a render-target format keeps its own bytes (no substitute).
        var readOnly = *description;
        readOnly.Flags &= ~GpuWrittenFlags;
        var desc = _descriptions.ToDesktop(device, &readOnly);
        desc.Format = ViewFormats.ForTexture(desc.Format, viewFormat);

        if (HResult.Failed(CreateTexture(device, &desc, ShaderResourceState, null, readOnly: true, out nint desktop)))
        {
            Interlocked.Add(ref _unmappedViewBytes, -charge);

            return null;
        }

        var texture = Register(address, description, desktop, desc.Format, ShaderResourceState, false);
        ComObject.Release(desktop);

        // Counted as over unmapped memory (the summary; --retire-unmapped-textures retires it if it stays so).
        if (charge > 0)
        {
            texture.UnmappedCharge = charge;
            Registry.Unmapped(address, layout.Size);
            Counters.Add(GpuResourceEvent.ViewCreatedUnmapped);
        }

        Refresh(device, texture);

        return texture;
    }

    /// <summary>
    /// A slot-70 view of the registered <paramref name="texture"/>: its memory may have new bytes the game wrote with the
    /// CPU (nothing tells us of those), so all its pages are checked, but by the refresh worker against the per-page
    /// hashes, before the next submission (which reads the view) and at most once a frame. A CPU-mapped texture is
    /// checked every frame anyway. Before this each of w108's ~16k resolved views read and hashed the whole texture on
    /// the game's thread.
    /// </summary>
    public void RefreshForView(GpuTexture texture)
    {
        if (texture.GpuWritten || texture.CopiedInto || texture.Watched || texture.Layout.PlaneCount != 1)
        {
            return;
        }

        long frame = Volatile.Read(ref _checkedFrame);

        if (texture.UploadedHash != 0 && Interlocked.Exchange(ref texture.ViewCheckedFrame, frame) == frame)
        {
            Counters.Add(GpuResourceEvent.ViewCheckSkipped);

            return;
        }

        texture.MarkPages(0, texture.Size);
        Writes.Add(texture);
        Counters.Add(GpuResourceEvent.ViewCheckQueued);
    }

    /// <summary>
    /// The bytes of textures made for views over reserved memory no page of which is mapped yet. Nothing releases such
    /// a texture until another takes its range, and the game makes views over memory it never maps: w110 made 226,732 of
    /// them and its private bytes passed the working-set guard (22 GB write-combined). Default 1 GiB; past it such a
    /// view is a null descriptor, as before f684f08. Textures over memory with a page mapped do not count.
    /// </summary>
    public static long UnmappedViewBudget { get; set; } = DefaultUnmappedViewBudget;

    /// <summary>The default <see cref="UnmappedViewBudget"/>: 1 GiB.</summary>
    public const long DefaultUnmappedViewBudget = 1L << 30;

    /// <summary>
    /// The bytes a texture of <paramref name="size"/> for a view at <paramref name="address"/> counts against
    /// <see cref="UnmappedViewBudget"/>: 0 when a page of its memory is mapped; when none is, its size (at least one
    /// 64 KiB placement) while the live ones stay within the budget, else -1 (no texture: a null view).
    /// </summary>
    private long UnmappedCharge(ulong address, ulong size)
    {
        if (_source.AnyMapped(address, size))
        {
            return 0;
        }

        long bytes = (long)Math.Max(size, GpuTexture.PageBytes);

        if (Interlocked.Add(ref _unmappedViewBytes, bytes) <= UnmappedViewBudget)
        {
            return bytes;
        }

        Interlocked.Add(ref _unmappedViewBytes, -bytes);
        Counters.Add(GpuResourceEvent.ViewUnmappedOverBudget);

        return -1;
    }

    /// <summary>
    /// <paramref name="texture"/> no longer counts against <see cref="UnmappedViewBudget"/>: a page under it was mapped,
    /// or it is released. The budget used to count every texture so made for good (w111b made 134k and released 139k).
    /// </summary>
    private void Refund(GpuTexture texture)
    {
        long charge = Interlocked.Exchange(ref texture.UnmappedCharge, 0);

        if (charge > 0)
        {
            Interlocked.Add(ref _unmappedViewBytes, -charge);
        }
    }

    /// <summary>The bytes of live textures made for views over memory with no page mapped (<see cref="UnmappedViewBudget"/>).</summary>
    public long UnmappedViewBytes => Interlocked.Read(ref _unmappedViewBytes);

    /// <summary>"views' textures over memory never mapped X GB of Y, upload buffers Z GB": the memory this class holds besides the registered textures.</summary>
    public string MemorySummary => FormattableString.Invariant($"views' textures over memory never mapped {UnmappedViewBytes / BytesPerGigabyte:F2} GB of {UnmappedViewBudget / BytesPerGigabyte:F2}, upload buffers {Uploader.StagingBytes / BytesPerGigabyte:F2} GB");

    /// <summary>Fills <paramref name="texture"/> from memory when it is not GPU-written, its memory is readable and its bytes changed.</summary>
    public void Refresh(nint device, GpuTexture texture)
    {
        if (texture.GpuWritten || texture.Layout.PlaneCount != 1)
        {
            return;
        }

        if (texture.CopiedInto)
        {
            Counters.Add(GpuResourceEvent.UploadKeptCopy);

            return;
        }

        Refresh(device, texture, throughKernel: texture.Watched);
    }

    /// <summary>
    /// As <see cref="Refresh(nint, GpuTexture)"/>, reading the bytes through the kernel when <paramref name="throughKernel"/>
    /// (memory another thread may unmap meanwhile).
    /// </summary>
    private void Refresh(nint device, GpuTexture texture, bool throughKernel)
    {
        // One refresh of a texture at a time: the refresh worker and a view's refresh would race on its page hashes.
        lock (texture.RefreshGate)
        {
            RefreshLocked(device, texture, throughKernel);
        }
    }

    private void RefreshLocked(nint device, GpuTexture texture, bool throughKernel)
    {
        byte* source = _source.Find(texture.Address, texture.Size);

        if (source == null)
        {
            Gathered(device, texture);

            return;
        }

        // A watched texture's memory can be unmapped by another thread while it is read every frame: a plain read
        // faulted (an AccessViolation the guard cannot catch, boot v3), so its bytes are copied through the kernel first.
        if (throughKernel)
        {
            source = ReadThroughKernel(source, texture.Size);

            if (source == null)
            {
                return;
            }
        }

        Fill(device, texture, source);
    }

    /// <summary>
    /// A texture in an XMem reservation whose pages are not all mapped at its CPU address: filled from the pages mapped
    /// anywhere (the CPU range or the GPU-only alias), zeros for the rest (<see cref="GameMemorySource.Gather"/>). w108's
    /// streamed material textures were never readable as a whole (UploadUnreadable 32,415 after 28,366 refreshes):
    /// a streamed texture has its pages mapped as its mips arrive, and the whole-range read refused it every time.
    /// </summary>
    private void Gathered(nint device, GpuTexture texture)
    {
        if (!_source.InReservation(texture.Address))
        {
            Counters.Add(GpuResourceEvent.UploadUnreadable);

            return;
        }

        byte* copy = KernelCopy(texture.Size);

        if (_source.Gather(texture.Address, texture.Size, copy) == 0)
        {
            Counters.Add(GpuResourceEvent.UploadUnreadable);
            NoteUnreadable(texture);

            return;
        }

        Counters.Add(GpuResourceEvent.UploadGathered);
        Fill(device, texture, copy);
    }

    /// <summary>The first textures no page of which is mapped anywhere, logged with their CPU range: where their texels are not.</summary>
    private void NoteUnreadable(GpuTexture texture)
    {
        if (Interlocked.Increment(ref _unreadableLogged) <= LoggedFailures)
        {
            _log.Info($"  [d3d12_xs] texture at 0x{texture.Address:x} (0x{texture.Size:x} bytes, format {texture.DesktopFormat}): no page of it is mapped, at its address or its CPU range ({_source.Describe(texture.Address)}); left as it was");
        }
    }

    /// <summary>
    /// Uploads the whole texture from <paramref name="source"/> unless its bytes are the ones last uploaded; the hash is
    /// kept per page (<see cref="GpuTexture.PageHashes"/>), so a later write refreshes only what it changed
    /// (<see cref="TextureRefreshWorker"/>).
    /// </summary>
    private void Fill(nint device, GpuTexture texture, byte* source)
    {
        var pages = TextureRefreshWorker.HashPages(texture, source);
        ulong hash = TextureRefreshWorker.Combined(pages);

        if (hash == texture.UploadedHash)
        {
            Counters.Add(GpuResourceEvent.UploadSkipped);

            return;
        }

        if (Uploader.Upload(device, texture, source))
        {
            texture.PageHashes = pages;
            texture.UploadedHash = hash;
            Counters.Add(GpuResourceEvent.Upload);

            return;
        }

        ReportFailure(texture);
    }

    /// <summary>Counts and logs (the first eight of each) an upload <see cref="Uploader"/> did not make.</summary>
    internal void ReportFailure(GpuTexture texture)
    {
        var failure = Uploader.Failure;

        // No failure named: the console layout does not fit the desktop texture (Uploader.Subresources), which once left
        // textures unfilled without a word.
        if (failure is null)
        {
            if (Counters.Add(GpuResourceEvent.UploadRefused) <= LoggedFailures)
            {
                var console = texture.Console;
                _log.Warn($"[d3d12_xs] texture at 0x{texture.Address:x} ({console.Width}x{console.Height}x{console.DepthOrArraySize}, {console.MipLevels} mips, format {console.Format}, layout 0x{console.Layout:x}) not filled: its console layout does not fit the desktop texture");
            }

            return;
        }

        if (Counters.Add(GpuResourceEvent.UploadFailed) <= LoggedFailures)
        {
            _log.Warn($"[d3d12_xs] texture at 0x{texture.Address:x} ({texture.Size} bytes) not filled from memory: {failure}");
        }
    }

    /// <summary>
    /// Memory at console address <paramref name="address"/> changed behind the textures filled from it (a DirectStorage
    /// request, a page mapped under them): each is refreshed before the next submission (<see cref="RefreshWritten"/>).
    /// The game streams a texture's mips into memory after it made the texture and its views, so the fill those made
    /// found the memory empty or unmapped, and nothing refreshed it again (w105: the material textures had no texels).
    /// </summary>
    public void MarkWritten(ulong address, ulong bytes)
    {
        foreach (var texture in Registry.Overlapping(address, bytes))
        {
            // Memory under it is mapped or written now: it no longer stands for memory the game never mapped.
            Refund(texture);

            if (texture.GpuWritten || texture.CopiedInto)
            {
                continue;
            }

            ulong start = Math.Max(address, texture.Address) - texture.Address;
            ulong end = Math.Min(address + bytes, texture.Address + texture.Size) - texture.Address;
            texture.MarkPages(start, end);
            Writes.Add(texture);
        }
    }

    /// <summary>
    /// Before a submission: the textures <see cref="MarkWritten"/> named are current (the refresh worker did most of
    /// them meanwhile; this thread takes the rest and waits for the ones in progress).
    /// </summary>
    public void RefreshWritten(nint device)
    {
        Writes.Settle(device);
    }

    /// <summary>The written textures' refresh, off the submitting thread (<see cref="TextureRefreshWorker"/>).</summary>
    internal TextureRefreshWorker Writes { get; }

    /// <summary>
    /// Refills the whole of <paramref name="texture"/> from memory read through the kernel, as a first fill or when its
    /// page hashes are unknown (the refresh worker's fallback).
    /// </summary>
    internal void RefreshWhole(nint device, GpuTexture texture)
    {
        Refresh(device, texture, throughKernel: true);
    }

    /// <summary>This thread's copy buffer of at least <paramref name="size"/> bytes.</summary>
    internal static byte* CopyBuffer(ulong size)
    {
        return KernelCopy(size);
    }

    /// <summary>
    /// A GPU copy writes <paramref name="desktop"/>, the texture registered at <paramref name="address"/>: from now on
    /// its memory no longer refreshes it (victory-gta6 keeps a host-written texture the same way).
    /// </summary>
    [HotPath]
    public void MarkCopiedInto(ulong address, nint desktop)
    {
        var texture = Registry.Find(address);

        if (texture is not null && texture.Address == address && texture.Desktop == desktop)
        {
            texture.CopiedInto = true;
        }
    }

    /// <summary>
    /// The game mapped the texture at <paramref name="address"/> (resource Map): the CPU writes its bytes whenever it
    /// likes (video frames decoded into it, for one), so from now on it is checked once a frame.
    /// </summary>
    public void Watch(ulong address)
    {
        var texture = Registry.Find(address);

        if (texture is null || texture.GpuWritten || texture.Watched || texture.Address != address)
        {
            return;
        }

        lock (_watchGate)
        {
            if (texture.Watched)
            {
                return;
            }

            texture.Watched = true;
            Volatile.Write(ref _watched, [.. _watched, texture]);
        }

        _log.Detail($"  [d3d12_xs] texture at 0x{address:x} mapped by the CPU: checked for new bytes every frame ({_watched.Length} such)");
    }

    /// <summary>
    /// Once per <paramref name="frame"/> (the present count): releases retired textures past their grace period and
    /// refreshes the watched textures still registered.
    /// </summary>
    public void RefreshWatched(nint device, long frame)
    {
        if (Volatile.Read(ref _checkedFrame) == frame || Interlocked.Exchange(ref _checkedFrame, frame) == frame)
        {
            return;
        }

        Registry.Advance(frame);
        var textures = new List<GpuTexture>();
        int retired = 0;

        foreach (var texture in Volatile.Read(ref _watched))
        {
            if (Registry.Find(texture.Address) != texture)
            {
                retired++;
            }
            else if (texture.CopiedInto)
            {
                Counters.Add(GpuResourceEvent.UploadKeptCopy);
            }
            else if (!texture.GpuWritten && texture.Layout.PlaneCount == 1)
            {
                textures.Add(texture);
            }
        }

        if (retired != 0)
        {
            Forget();
        }

        // On the refresh worker's thread budget (TextureRefreshWorker.SettleThreads): each texture is read through the
        // kernel and hashed on its own thread, in that thread's copy buffer, and the uploads take the uploader's lock in
        // turn. One after another on the submitting thread (about a dozen Bink planes and the glyph cache) the check took
        // up to tens of ms of every w109 world frame before its first submission reached the GPU.
        Parallel.ForEach(textures, new ParallelOptions { MaxDegreeOfParallelism = TextureRefreshWorker.SettleThreads }, texture => Refresh(device, texture, throughKernel: true));
    }

    /// <summary>Drops the watched textures no longer registered (retired): they were walked every frame for good.</summary>
    private void Forget()
    {
        lock (_watchGate)
        {
            Volatile.Write(ref _watched, [.. _watched.Where(texture => Registry.Find(texture.Address) == texture)]);
        }
    }

    public void Dispose()
    {
        Writes.Dispose();
        Registry.Dispose();
        Uploader.Dispose();
    }

    private GpuTexture Register(ulong address, ResourceDescription* description, nint desktop, int format, int state, bool gpuWritten)
    {
        XgLayoutBuilder.Build(ToXg(description), out var layout);
        var texture = new GpuTexture(address, *description, desktop, format, state, gpuWritten, layout);
        ((delegate* unmanaged<nint, uint>)ComObject.Slot(desktop, AddRefSlot))(desktop);
        Registry.Register(texture);

        if (_log.DetailEnabled)
        {
            string writer = gpuWritten ? "GPU-written" : "filled from memory";
            _log.Detail($"  [d3d12_xs] texture at 0x{address:x}: {description->Width}x{description->Height}x{description->DepthOrArraySize}, {description->MipLevels} mips, format {format}, layout 0x{description->Layout:x}, 0x{layout.Size:x} bytes, {writer}");
        }

        return texture;
    }

    /// <summary>
    /// The bytes at <paramref name="source"/> read through the kernel (ReadProcessMemory) into this thread's copy buffer,
    /// valid until the thread's next call; null when a page went away. The buffer is native and kept: a managed array of
    /// a watched texture's size (a 3840x2160 video plane, the 2048x2048 glyph cache) went to the large object heap every
    /// frame, and each such allocation counts toward a full collection.
    /// </summary>
    private static byte* ReadThroughKernel(byte* source, ulong size)
    {
        byte* copy = KernelCopy(size);
        nuint done = 0;

        if (Kernel32.ReadProcessMemory(Kernel32.GetCurrentProcess(), (nint)source, copy, (nuint)size, &done) == 0 || done != (nuint)size)
        {
            return null;
        }

        return copy;
    }

    /// <summary>This thread's copy buffer, grown to <paramref name="size"/> bytes.</summary>
    private static byte* KernelCopy(ulong size)
    {
        if ((nuint)size > _kernelCopyBytes)
        {
            _kernelCopy = (byte*)NativeMemory.Realloc(_kernelCopy, (nuint)size);
            _kernelCopyBytes = (nuint)size;
        }

        return _kernelCopy;
    }

    /// <summary>
    /// A read-only single-sample texture placed in a shared heap with <c>--placed-textures</c> (<see cref="PlacedTextureHeaps"/>),
    /// else (or when placing fails) a committed one.
    /// </summary>
    private int CreateTexture(nint device, ResourceDescription* description, int state, void* clear, bool readOnly, out nint resource)
    {
        if (PlacedTextureHeaps.Enabled && readOnly && description->SampleCount <= 1)
        {
            int hr = Placed.Create(device, description, state, clear, out resource);

            if (!HResult.Failed(hr))
            {
                return hr;
            }

            Counters.Add(GpuResourceEvent.PlacedFailed);
        }

        return CreateCommitted(device, description, state, clear, out resource);
    }

    private static int CreateCommitted(nint device, ResourceDescription* description, int state, void* clear, out nint resource)
    {
        int* properties = stackalloc int[] { DefaultHeapType, 0, 0, 0, 0 };
        Guid iid = ResourceIid;
        nint created = 0;
        var create = (delegate* unmanaged<nint, int*, int, ResourceDescription*, int, void*, Guid*, nint*, int>)ComObject.Slot(device, DesktopCreateCommittedResourceSlot);
        int hr = create(device, properties, NoHeapFlags, description, state, clear, &iid, &created);
        resource = created;

        return hr;
    }

    /// <summary>The XG desc of a console resource desc: the same D3D12_RESOURCE_DESC fields.</summary>
    private static XgResourceDesc ToXg(ResourceDescription* description)
    {
        return new XgResourceDesc
        {
            Dimension = (uint)description->Dimension,
            Alignment = description->Alignment,
            Width = description->Width,
            Height = description->Height,
            DepthOrArraySize = description->DepthOrArraySize,
            MipLevels = description->MipLevels,
            Format = (uint)description->Format,
            SampleCount = description->SampleCount,
            SampleQuality = description->SampleQuality,
            Layout = (uint)description->Layout,
            Flags = (uint)description->Flags,
        };
    }
}
