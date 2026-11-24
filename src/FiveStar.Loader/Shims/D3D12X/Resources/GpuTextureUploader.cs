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

    private readonly object _gate = new();
    private readonly PlacedSubresourceFootPrint[] _footprints = new PlacedSubresourceFootPrint[MaxSubresources];
    private readonly uint[] _rows = new uint[MaxSubresources];
    private readonly ulong[] _rowSizes = new ulong[MaxSubresources];
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

    /// <summary>Why the last fill failed, or null.</summary>
    public string? Failure { get; private set; }

    /// <summary>Copies <paramref name="source"/> (the texture's console bytes) into <paramref name="texture"/>; false when it cannot.</summary>
    public bool Upload(nint device, GpuTexture texture, byte* source)
    {
        lock (_gate)
        {
            Failure = null;

            if (_disposed)
            {
                return false;
            }

            try
            {
                return Fill(device, texture, source);
            }
            catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or InvalidOperationException or TimeoutException)
            {
                Failure = exception.Message;

                return false;
            }
        }
    }

    /// <summary>
    /// Before the game's lists run on desktop queue <paramref name="queue"/>: the queue waits on the GPU for every upload
    /// submitted so far, so the lists read the new bytes. Nothing when none is still running.
    /// </summary>
    [HotPath]
    public void OrderBefore(nint queue)
    {
        // No lock: a fill holds it while it detiles or waits for a slot, and submissions must not queue behind that.
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

    private bool Fill(nint device, GpuTexture texture, byte* source)
    {
        Open(device);
        Marshal.AddRef(texture.Desktop);
        var resource = new ID3D12Resource(texture.Desktop);
        int count;
        ulong total;

        try
        {
            count = Subresources(texture, resource, out total);
        }
        catch
        {
            resource.Dispose();

            throw;
        }

        if (count == 0)
        {
            resource.Dispose();

            return false;
        }

        Submit(texture, source, resource, count, total);

        return true;
    }

    /// <summary>The subresources one fill copies, their footprints laid out; 0 when the texture cannot be filled.</summary>
    private int Subresources(GpuTexture texture, ID3D12Resource resource, out ulong total)
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

        _device!.GetCopyableFootprints(description, 0, (uint)count, 0, _footprints, _rows, _rowSizes, out total);

        if (!Fits(texture, count))
        {
            return 0;
        }

        return count;
    }

    /// <summary>Records and submits the copy into <paramref name="resource"/> on the next slot, which takes the reference.</summary>
    private void Submit(GpuTexture texture, byte* source, ID3D12Resource resource, int count, ulong total)
    {
        int index = _next;
        UploadSlot slot;

        try
        {
            slot = TakeSlot(index);
            EnsureStaging(slot, total);
            Detile(texture, source, slot.Data, count);
            Record(slot, resource, texture.State, count);
            slot.List.Close();
        }
        catch
        {
            // Nothing of this slot runs on the GPU (its last upload finished in TakeSlot, which left the slot as it was
            // when that wait failed): the texture is let go, and a slot that was opened is dropped for a new one.
            resource.Dispose();
            DropOpened(index);

            throw;
        }

        // The slot's fence value is set before the Signal: if the Signal fails, the next upload signals the same value
        // after this one on the same queue, so the slot is never reused before its copy ran.
        ulong value = _submitted + 1;
        slot.Target = resource;
        slot.Fence = value;
        _queue!.ExecuteCommandList(slot.List);
        _queue.Signal(_fence!, value).CheckError();
        Volatile.Write(ref _submitted, value);
    }

    /// <summary>After a failed fill: drops slot <paramref name="index"/> when TakeSlot handed it out (its list is open).</summary>
    private void DropOpened(int index)
    {
        if (_next == index)
        {
            return;
        }

        _slots[index]?.Dispose();
        _slots[index] = null;
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
        if (size <= slot.Size)
        {
            return;
        }

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
    }

    private static ID3D12Resource UploadBuffer(ID3D12Device device, ulong size)
    {
        return device.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None, Vortice.Direct3D12.ResourceDescription.Buffer(size), ResourceStates.GenericRead);
    }

    /// <summary>Whether every XG mip's rows fit the desktop footprint (element size and block rows agree).</summary>
    private bool Fits(GpuTexture texture, int count)
    {
        var layout = texture.Layout;
        ref readonly var plane = ref layout.Planes[0];

        for (int subresource = 0; subresource < count; subresource++)
        {
            ref readonly var mip = ref plane.Mips[subresource % plane.MipCount];
            ulong rowBytes = (ulong)mip.WidthElements * (ulong)plane.BytesPerElement;

            if (rowBytes > _rowSizes[subresource] || mip.HeightElements > _rows[subresource])
            {
                return false;
            }
        }

        return true;
    }

    private void Detile(GpuTexture texture, byte* source, byte* staging, int count)
    {
        var layout = texture.Layout;

        for (int subresource = 0; subresource < count; subresource++)
        {
            ref readonly var footprint = ref _footprints[subresource];
            uint rowPitch = footprint.Footprint.RowPitch;
            uint slicePitch = rowPitch * _rows[subresource];
            XgCopier.From(layout, staging + footprint.Offset, 0, (uint)subresource, source, rowPitch, slicePitch);
        }
    }

    private void Record(UploadSlot slot, ID3D12Resource resource, int state, int count)
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
            var source = new TextureCopyLocation(slot.Staging!, _footprints[subresource]);
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
}
