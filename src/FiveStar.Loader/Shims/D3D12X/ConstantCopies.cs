// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.Resources;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// Constant-buffer views the address map cannot serve: views outside the game's own heaps over its remappable XMem
/// pages, and views over memory no desktop buffer covers (in the game's heaps a view over XMem pages is a V# at the
/// game address, read live through the map, XboxDescriptorMethods.GameAddressView). Each such view reads a copy of its
/// own in one upload buffer, kept per descriptor, and every ExecuteCommandLists copies the game's bytes into all of them
/// first (<see cref="Refresh"/>): a submission sees the constants as the game left them when it submitted. A source
/// the CPU cannot read (an unmapped page) copies as zeros. The mechanism, copies of title memory refreshed at
/// submission, follows victory-gta6 (fxceless, GPL-3.0-or-later), a fork of VIctory.
/// </summary>
internal sealed unsafe class ConstantCopies
{
    /// <summary>Bytes of the upload buffer that holds every copy.</summary>
    public const ulong Capacity = 64 * Mebibyte;

    private const ulong Mebibyte = 1UL << 20;

    /// <summary>Copies come in sizes of 256 bytes (the desktop CBV granularity) times a power of two, up to 64 KiB.</summary>
    private const int SmallestShift = 8;
    private const int Classes = 9;

    /// <summary>D3D12_RESOURCE_STATE_GENERIC_READ, the state of upload buffers.</summary>
    private const int GenericRead = 0xAC3;

    private readonly object _gate = new();
    private readonly Dictionary<nint, Copy> _byHandle = [];
    private readonly Stack<ulong>[] _free = [.. Enumerable.Range(0, Classes).Select(_ => new Stack<ulong>())];
    private readonly XboxObjects _objects;
    private readonly AddressTable _addresses;
    private readonly GameMemorySource _memory;
    private readonly LoaderLog _log;
    private byte* _data;
    private bool _failed;
    private ulong _next;

    public ConstantCopies(XboxObjects objects, AddressTable addresses, GameMemorySource memory, LoaderLog log)
    {
        _objects = objects;
        _addresses = addresses;
        _memory = memory;
        _log = log;
    }

    /// <summary>Views that read a copy now.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byHandle.Count;
            }
        }
    }

    /// <summary>
    /// The address the view written at <paramref name="handle"/> reads instead of <paramref name="source"/>: a copy of
    /// its <paramref name="size"/> bytes, filled now and at every submission; 0 when the copies are full or the size is
    /// over 64 KiB.
    /// </summary>
    public ulong CopyFor(nint handle, ulong source, uint size)
    {
        int sizeClass = SizeClass(size);

        if (sizeClass < 0)
        {
            return 0;
        }

        lock (_gate)
        {
            if (_data == null && !Create())
            {
                return 0;
            }

            if (_byHandle.TryGetValue(handle, out var old))
            {
                _free[old.Class].Push(old.Offset);
                _byHandle.Remove(handle);
            }

            ulong offset;

            if (!_free[sizeClass].TryPop(out offset))
            {
                ulong bytes = 1UL << (SmallestShift + sizeClass);

                if (_next + bytes > Capacity)
                {
                    return 0;
                }

                offset = _next;
                _next += bytes;
            }

            var copy = new Copy(source, size, offset, sizeClass);
            _byHandle[handle] = copy;
            Fill(copy);

            return (ulong)(_data + offset);
        }
    }

    /// <summary>Copies the game's bytes into every view's copy; called before each ExecuteCommandLists.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            foreach (var copy in _byHandle.Values)
            {
                Fill(copy);
            }
        }
    }

    private void Fill(Copy copy)
    {
        byte* target = _data + copy.Offset;
        byte* source = _memory.Find(copy.Source, copy.Size);

        if (source == null)
        {
            NativeMemory.Clear(target, copy.Size);

            return;
        }

        Buffer.MemoryCopy(source, target, copy.Size, copy.Size);
    }

    /// <summary>The smallest class that holds <paramref name="size"/> bytes; -1 for none.</summary>
    private static int SizeClass(uint size)
    {
        for (int sizeClass = 0; sizeClass < Classes; sizeClass++)
        {
            if (size <= 1UL << (SmallestShift + sizeClass))
            {
                return sizeClass;
            }
        }

        return -1;
    }

    private bool Create()
    {
        if (_failed)
        {
            return false;
        }

        var properties = new HeapProperties { Type = HeapProperties.Upload };
        nint buffer = DesktopCalls.CreateBuffer(_objects.DesktopDevice, properties, Capacity, 0, GenericRead);
        byte* data = null;

        if (buffer != 0)
        {
            data = DesktopCalls.Map(buffer);
        }

        if (data == null)
        {
            _failed = true;
            _log.Warn("[d3d12_xs] constant copies: no upload buffer; views over game memory no buffer covers stay null");

            return false;
        }

        _addresses.Add(new AddressRange((ulong)data, Capacity, DesktopCalls.GpuAddress(buffer), buffer));
        _data = data;
        _log.Info($"[d3d12_xs] constant copies: {Capacity / Mebibyte} MiB at 0x{(ulong)data:x}, refreshed at every submission");

        return true;
    }

    private readonly record struct Copy(ulong Source, uint Size, ulong Offset, int Class);
}
