// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Shims.Xg;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// Fills a desktop texture from console memory: each subresource is detiled with its XG layout
/// (<see cref="XgCopier.From"/>) into an upload buffer at the desktop's copyable footprint and copied into the texture
/// on a private direct queue between transitions out of and back into its resting state. The caller does not wait for
/// the GPU: an upload takes one of <see cref="Slots"/> slots (list, allocator, upload buffer, a reference to the
/// texture), waiting only when that slot's previous upload has not finished, and every game submission first has its
/// queue wait on the GPU for the uploads before it (<see cref="OrderBefore"/>). The slot keeps the texture alive until
/// its copy is done, so a texture retired and released right after its fill is not freed under the copy. Waiting on
/// the CPU for each upload held the game's threads for a fifth of a frame (texture views created per frame, CPU-mapped
/// textures refreshed per frame). A fill the desktop refuses (no memory for the upload buffer: it is system memory,
/// which heaps over the game's sections share, docs/d3d12x-shim.md) or whose slot does not come free fails without an
/// exception: the uploader is called from the game's threads, and an exception there ended the process (0xE0434352).
/// The failed slot is dropped, and the next fill on it makes a new one.
/// <para>
/// The lock covers taking a slot and submitting its copy; detiling into the slot's upload buffer and recording its list
/// run outside it, on the caller's thread, so fills from several threads (the refresh worker's settle, the frame's check
/// of CPU-mapped textures) detile at once and only the GPU copies queue in turn. A slot being filled is busy: a fill that
/// comes round to it waits for it.
/// </para>
/// </summary>
internal sealed unsafe class GpuTextureUploader : IDisposable
{
    /// <summary>D3D12_RESOURCE_STATE_COPY_DEST (d3d12.h).</summary>
    private const int CopyDestState = 0x400;

    /// <summary>ID3D12CommandQueue::Wait (d3d12.h).</summary>
    private const int QueueWaitSlot = 15;

    /// <summary>Subresources one upload handles: 15 mips times the array slices XG lays out (docs/xg.md).</summary>
    private const int MaxSubresources = XgLimits.MaxMips * 64;

    /// <summary>Uploads in flight at most.</summary>
    private const int Slots = 16;

    /// <summary>Waits between device checks while a slot's upload runs, up to about a minute.</summary>
    private const int WaitMilliseconds = 100;
    private const int MaxWaits = 600;

    /// <summary>An upload buffer past this (32 MiB) is not kept for fills that need less.</summary>
    private const ulong LargeStaging = 32UL << 20;

    /// <summary>This thread's footprints of the fill it is making (several threads fill at once).</summary>
    [ThreadStatic]
    private static Footprints? _layout;

    /// <summary>Why this thread's last fill failed.</summary>
    [ThreadStatic]
    private static string? _failure;

    private readonly object _gate = new();
    private long _stagingBytes;
    private readonly UploadSlot?[] _slots = new UploadSlot?[Slots];
    private readonly AutoResetEvent _completed = new(false);
    private ID3D12Device? _device;
    private ID3D12CommandQueue? _queue;
    private ID3D12Fence? _fence;
    private int _next;
    private ulong _submitted;
    private bool _disposed;

    /// <summary>Makes the upload buffer of a size; the desktop's committed upload buffer (tests make it fail).</summary>
    public Func<ID3D12Device, ulong, ID3D12Resource> CreateStaging { get; set; } = UploadBuffer;

    /// <summary>Why this thread's last fill failed, or null (a fill on this thread, so concurrent fills do not mix them up).</summary>
#pragma warning disable CA1822 // An instance member: callers ask the uploader they filled through.
    public string? Failure => _failure;
#pragma warning restore CA1822

    /// <summary>Copies <paramref name="source"/> (the texture's console bytes) into <paramref name="texture"/>; false when it cannot.</summary>
    public bool Upload(nint device, GpuTexture texture, byte* source, bool[]? only = null)
    {
        _failure = null;
        var layout = _layout ??= new Footprints();
        Fill? fill;

        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                fill = Prepare(device, texture, layout);
            }
        }
        catch (Exception exception) when (Expected(exception))
        {
            _failure = exception.Message;

            return false;
        }

        if (fill is null)
        {
            return false;
        }

        try
        {
            Detile(texture, source, fill.Slot.Data, fill.Count, only, layout);
            Record(fill.Slot, fill.Resource, texture.State, fill.Count, only, layout);
            fill.Slot.List.Close();
        }
        catch (Exception exception) when (Expected(exception))
        {
            _failure = exception.Message;
            Abandon(fill);

            return false;
        }

        return Finish(fill);
    }

    /// <summary>The exceptions a fill reports as its failure instead of letting them reach the game's thread.</summary>
    private static bool Expected(Exception exception)
    {
        return exception is SharpGen.Runtime.SharpGenException or InvalidOperationException or TimeoutException;
    }

    /// <summary>
    /// Before the game's lists run on desktop queue <paramref name="queue"/>: the queue waits on the GPU for every upload
    /// submitted so far, so the lists read the new bytes. Nothing when none is still running.
    /// </summary>
    [HotPath]
    public void OrderBefore(nint queue)
    {
        // No lock: a fill holds it while it waits for a slot, and submissions must not queue behind that.
        ulong submitted = Volatile.Read(ref _submitted);
        var fence = Volatile.Read(ref _fence);

        if (submitted == 0 || fence is null || fence.CompletedValue >= submitted)
        {
            return;
        }

        _ = ((delegate* unmanaged<nint, nint, ulong, int>)Native.ComObject.Slot(queue, QueueWaitSlot))(queue, fence.NativePointer, submitted);
    }

    /// <summary>Waits for the uploads still running (not past a device removal or the wait limit) and drops the slots.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // A fill detiling outside the lock still holds its slot: it sees the uploader disposed and lets it go.
            while (_slots.Any(slot => slot?.Busy == true))
            {
                Monitor.Wait(_gate);
            }

            bool finished = TryWaitFor(Volatile.Read(ref _submitted));

            for (int i = 0; i < Slots; i++)
            {
                // A slot whose copy may still run is left alone: freeing its buffers or texture under the GPU is worse than a leak at exit.
                if (finished)
                {
                    _slots[i]?.Dispose();
                }

                _slots[i] = null;
            }

            if (finished)
            {
                _queue?.Dispose();
                _device?.Dispose();
            }

            // The fence is not released: a game thread's submission may be reading it in OrderBefore (no lock there).
            Volatile.Write(ref _fence, null);
            _queue = null;
            _device = null;
        }
    }

    /// <summary>
    /// Under the lock: the texture's reference, its footprints in <paramref name="layout"/> and a busy slot with an upload
    /// buffer of their size; null when the texture cannot be filled (nothing held then).
    /// </summary>
    private Fill? Prepare(nint device, GpuTexture texture, Footprints layout)
    {
        Open(device);
        Marshal.AddRef(texture.Desktop);
        var resource = new ID3D12Resource(texture.Desktop);
        int count;
        ulong total;

        try
        {
            count = Subresources(texture, resource, layout, out total);
        }
        catch
        {
            resource.Dispose();

            throw;
        }

        if (count == 0)
        {
            resource.Dispose();

            return null;
        }

        int index = _next;
        UploadSlot? slot = null;

        try
        {
            slot = TakeSlot(index);
            EnsureStaging(slot, total);
        }
        catch
        {
            // Nothing of this slot runs on the GPU (a failed wait in TakeSlot leaves the slot as it was): the texture is
            // let go, and a slot that was handed out with its list open is dropped for a new one.
            resource.Dispose();

            if (slot is not null)
            {
                Drop(index, slot);
            }

            throw;
        }

        slot.Busy = true;

        return new Fill(slot, index, resource, count);
    }

    /// <summary>
    /// Under the lock: submits the filled slot's copy, unless the uploader was disposed meanwhile. The slot's fence value
    /// is set before the Signal: if the Signal fails, the next upload signals the same value after this one on the same
    /// queue, so the slot is never reused before its copy ran.
    /// </summary>
    private bool Finish(Fill fill)
    {
        lock (_gate)
        {
            var slot = fill.Slot;

            try
            {
                if (_disposed)
                {
                    fill.Resource.Dispose();

                    return false;
                }

                ulong value = _submitted + 1;
                slot.Target = fill.Resource;
                slot.Fence = value;
                _queue!.ExecuteCommandList(slot.List);
                _queue.Signal(_fence!, value).CheckError();
                Volatile.Write(ref _submitted, value);

                return true;
            }
            catch (Exception exception) when (Expected(exception))
            {
                _failure = exception.Message;

                return false;
            }
            finally
            {
                slot.Busy = false;
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>After a failed detile or record: the texture is let go and the slot, whose list is open, dropped for a new one.</summary>
    private void Abandon(Fill fill)
    {
        lock (_gate)
        {
            fill.Resource.Dispose();
            fill.Slot.Busy = false;
            Drop(fill.Index, fill.Slot);
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Drops <paramref name="slot"/> (its list open, nothing of it on the GPU); the next fill at <paramref name="index"/> makes a new one.</summary>
    private void Drop(int index, UploadSlot slot)
    {
        if (ReferenceEquals(_slots[index], slot))
        {
            _slots[index] = null;
        }

        slot.Dispose();
    }

    /// <summary>The subresources one fill copies, their footprints laid out; 0 when the texture cannot be filled.</summary>
    private int Subresources(GpuTexture texture, ID3D12Resource resource, Footprints layout, out ulong total)
    {
        var description = resource.Description;
        int count = description.MipLevels;
        total = 0;

        if (description.Dimension != ResourceDimension.Texture3D)
        {
            count *= description.DepthOrArraySize;
        }

        if (count > MaxSubresources || count > texture.Layout.Planes[0].MipCount * texture.Layout.Planes[0].Slices)
        {
            return 0;
        }

        _device!.GetCopyableFootprints(description, 0, (uint)count, 0, layout.Placed, layout.Rows, layout.RowSizes, out total);

        if (!Fits(texture, count, layout))
        {
            return 0;
        }

        return count;
    }

    private void Open(nint device)
    {
        if (_device is null)
        {
            Marshal.AddRef(device);
            _device = new ID3D12Device(device);
        }

        _queue ??= _device.CreateCommandQueue(new Vortice.Direct3D12.CommandQueueDescription(CommandListType.Direct));
        _fence ??= _device.CreateFence(0);
    }

    /// <summary>Slot <paramref name="index"/>, its previous upload finished and its texture let go, its list open; the next fill takes the next slot.</summary>
    private UploadSlot TakeSlot(int index)
    {
        // Another thread may still be filling this slot (detiling outside the lock): its copy is submitted first.
        while (_slots[index]?.Busy == true)
        {
            Monitor.Wait(_gate);
        }

        var slot = _slots[index];

        if (slot is null)
        {
            slot = new UploadSlot(_device!);
            _slots[index] = slot;
        }
        else
        {
            WaitFor(slot.Fence);
            slot.Finish();
            slot.Allocator.Reset();
            slot.List.Reset(slot.Allocator, null);
        }

        _next = (index + 1) % Slots;

        return slot;
    }

    private void WaitFor(ulong value)
    {
        if (value == 0 || _fence!.CompletedValue >= value)
        {
            return;
        }

        _fence.SetEventOnCompletion(value, _completed.SafeWaitHandle.DangerousGetHandle()).CheckError();

        for (int waited = 0; _fence.CompletedValue < value; waited++)
        {
            if (_device!.DeviceRemovedReason.Failure)
            {
                throw new InvalidOperationException($"device removed: {_device.DeviceRemovedReason}");
            }

            if (waited > MaxWaits)
            {
                throw new TimeoutException("the GPU did not finish a texture upload");
            }

            _completed.WaitOne(WaitMilliseconds);
        }
    }

    private bool TryWaitFor(ulong value)
    {
        if (_fence is null)
        {
            return true;
        }

        try
        {
            WaitFor(value);

            return true;
        }
        catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or InvalidOperationException or TimeoutException)
        {
            return false;
        }
    }

    private void EnsureStaging(UploadSlot slot, ulong size)
    {
        // A slot keeps its upload buffer, write-combined memory, for the next fill; one a large texture grew past
        // LargeStaging goes back once a fill that fits in less takes the slot (16 slots each kept their largest).
        bool shrink = slot.Size > LargeStaging && size <= LargeStaging;

        if (size <= slot.Size && !shrink)
        {
            return;
        }

        Interlocked.Add(ref _stagingBytes, -(long)slot.Size);

        // A failed creation or Map leaves no upload buffer, never the disposed one.
        slot.Staging?.Dispose();
        slot.Staging = null;
        slot.Data = null;
        slot.Size = 0;

        var staging = CreateStaging(_device!, size);
        slot.Staging = staging;
        void* data;
        staging.Map(0, null, &data).CheckError();
        slot.Data = (byte*)data;
        slot.Size = size;

        Interlocked.Add(ref _stagingBytes, (long)size);
    }

    /// <summary>The bytes of the upload buffers the slots keep (write-combined system memory).</summary>
    public long StagingBytes => Interlocked.Read(ref _stagingBytes);

    private static ID3D12Resource UploadBuffer(ID3D12Device device, ulong size)
    {
        return device.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None, Vortice.Direct3D12.ResourceDescription.Buffer(size), ResourceStates.GenericRead);
    }

    /// <summary>Whether every XG mip's rows fit the desktop footprint (element size and block rows agree).</summary>
    private static bool Fits(GpuTexture texture, int count, Footprints layout)
    {
        var tiling = texture.Layout;
        ref readonly var plane = ref tiling.Planes[0];

        for (int subresource = 0; subresource < count; subresource++)
        {
            ref readonly var mip = ref plane.Mips[subresource % plane.MipCount];
            ulong rowBytes = (ulong)mip.WidthElements * (ulong)plane.BytesPerElement;

            if (rowBytes > layout.RowSizes[subresource] || mip.HeightElements > layout.Rows[subresource])
            {
                return false;
            }
        }

        return true;
    }

    private static void Detile(GpuTexture texture, byte* source, byte* staging, int count, bool[]? only, Footprints layout)
    {
        var tiling = texture.Layout;

        for (int subresource = 0; subresource < count; subresource++)
        {
            if (!Takes(only, subresource))
            {
                continue;
            }

            ref readonly var footprint = ref layout.Placed[subresource];
            uint rowPitch = footprint.Footprint.RowPitch;
            uint slicePitch = rowPitch * layout.Rows[subresource];
            XgCopier.From(tiling, staging + footprint.Offset, 0, (uint)subresource, source, rowPitch, slicePitch);
        }
    }

    /// <summary>Whether a fill limited to <paramref name="only"/> (every subresource when null) copies <paramref name="subresource"/>.</summary>
    private static bool Takes(bool[]? only, int subresource)
    {
        return only is null || (subresource < only.Length && only[subresource]);
    }

    private static void Record(UploadSlot slot, ID3D12Resource resource, int state, int count, bool[]? only, Footprints layout)
    {
        var list = slot.List;
        var resting = (ResourceStates)state;
        bool transition = state != CopyDestState;

        if (transition)
        {
            list.ResourceBarrierTransition(resource, resting, ResourceStates.CopyDest);
        }

        for (int subresource = 0; subresource < count; subresource++)
        {
            if (!Takes(only, subresource))
            {
                continue;
            }

            var source = new TextureCopyLocation(slot.Staging!, layout.Placed[subresource]);
            list.CopyTextureRegion(new TextureCopyLocation(resource, (uint)subresource), 0, 0, 0, source, null);
        }

        if (transition)
        {
            list.ResourceBarrierTransition(resource, ResourceStates.CopyDest, resting);
        }
    }

    /// <summary>One upload's list, allocator and upload buffer, the texture its copy writes, and the fence value it signals.</summary>
    private sealed class UploadSlot : IDisposable
    {
        public UploadSlot(ID3D12Device device)
        {
            Allocator = device.CreateCommandAllocator(CommandListType.Direct);
            List = device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, Allocator, null);
        }

        public ID3D12CommandAllocator Allocator { get; }

        public ID3D12GraphicsCommandList List { get; }

        public ID3D12Resource? Staging { get; set; }

        public byte* Data { get; set; }

        public ulong Size { get; set; }

        public ulong Fence { get; set; }

        /// <summary>Whether a fill is detiling into the slot or recording it outside the lock; no other fill takes it then.</summary>
        public bool Busy { get; set; }

        /// <summary>The texture the last upload wrote, referenced until its copy finished.</summary>
        public ID3D12Resource? Target { get; set; }

        /// <summary>After the slot's upload finished: lets its texture go.</summary>
        public void Finish()
        {
            Target?.Dispose();
            Target = null;
        }

        public void Dispose()
        {
            Finish();
            List.Dispose();
            Allocator.Dispose();
            Staging?.Dispose();
        }
    }

    /// <summary>One fill's copyable footprints, rows and row sizes per subresource (a thread's, reused).</summary>
    private sealed class Footprints
    {
        public PlacedSubresourceFootPrint[] Placed { get; } = new PlacedSubresourceFootPrint[MaxSubresources];

        public uint[] Rows { get; } = new uint[MaxSubresources];

        public ulong[] RowSizes { get; } = new ulong[MaxSubresources];
    }

    /// <summary>A fill between its slot taken and its copy submitted: the slot and its index, the texture's reference, its subresources.</summary>
    private sealed record Fill(UploadSlot Slot, int Index, ID3D12Resource Resource, int Count);
}
