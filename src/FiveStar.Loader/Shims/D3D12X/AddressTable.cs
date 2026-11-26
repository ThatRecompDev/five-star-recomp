// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// Translates game addresses to desktop ones by range: console GPU addresses (on the console, GPU address == CPU
/// address) to desktop GPU addresses, and descriptor handles in game memory to desktop descriptor handles. A new range
/// replaces the ones it overlaps. Lookups read a sorted array that is replaced on each addition, so they take no lock.
/// </summary>
internal sealed class AddressTable
{
    /// <summary>IUnknown::AddRef.</summary>
    private const int AddRefSlot = 1;

    private readonly object _gate = new();
    private AddressRange[] _ranges = [];
    private Action<AddressRange>? _added;
    private Action<AddressRange>? _replaced;

    public AddressTable(RetiredAddressBuffers retired)
    {
        Retired = retired;
    }

    /// <summary>The buffers of replaced ranges, released once the GPU is past them.</summary>
    public RetiredAddressBuffers Retired { get; }

    /// <summary>
    /// Calls <paramref name="added"/> with every range added from now on, in order, and <paramref name="replaced"/>
    /// with each range an addition replaces, before that addition is observed.
    /// </summary>
    public void Observe(Action<AddressRange> added, Action<AddressRange>? replaced = null)
    {
        lock (_gate)
        {
            _added = added;
            _replaced = replaced;
        }
    }

    /// <summary>
    /// Adds <paramref name="range"/>, which holds a reference on its desktop resource until a later range replaces it:
    /// the game releases buffers whose addresses views, copies and the shader address map still translate, and a view
    /// made on a released buffer crashed the driver (CreateShaderResourceView, nvwgf2umx.dll, at the story load). A
    /// replaced range leaves the map first (<see cref="Observe"/>), and its buffer retires (<see cref="RetiredAddressBuffers"/>).
    /// </summary>
    public void Add(AddressRange range)
    {
        lock (_gate)
        {
            var kept = new List<AddressRange>(_ranges.Length + 1);
            var replaced = new List<AddressRange>();

            foreach (var existing in _ranges)
            {
                if (existing.GameAddress + existing.Size <= range.GameAddress || range.GameAddress + range.Size <= existing.GameAddress)
                {
                    kept.Add(existing);
                }
                else
                {
                    replaced.Add(existing);
                }
            }

            AddRef(range.Resource);
            kept.Add(range);
            kept.Sort((a, b) => a.GameAddress.CompareTo(b.GameAddress));
            Volatile.Write(ref _ranges, kept.ToArray());

            foreach (var existing in replaced)
            {
                _replaced?.Invoke(existing);
            }

            _added?.Invoke(range);

            foreach (var existing in replaced)
            {
                Retired.Retire(existing.Resource);
            }
        }
    }

    /// <summary>Once per present: releases the replaced buffers the GPU is past (<see cref="RetiredAddressBuffers.Advance"/>).</summary>
    public void Advance(long present)
    {
        Retired.Advance(present);
    }

    private static unsafe void AddRef(nint resource)
    {
        if (resource != 0)
        {
            ((delegate* unmanaged<nint, uint>)ComObject.Slot(resource, AddRefSlot))(resource);
        }
    }

    /// <summary>The desktop address of <paramref name="gameAddress"/>; false outside every known range.</summary>
    [HotPath]
    public bool TryTranslate(ulong gameAddress, out ulong desktopAddress)
    {
        desktopAddress = 0;

        if (!TryFind(gameAddress, out var range))
        {
            return false;
        }

        desktopAddress = range.DesktopAddress + (gameAddress - range.GameAddress);

        return true;
    }

    /// <summary>The range holding <paramref name="gameAddress"/>; false outside every known range.</summary>
    [HotPath]
    public bool TryFind(ulong gameAddress, out AddressRange found)
    {
        var ranges = Volatile.Read(ref _ranges);
        int low = 0;
        int high = ranges.Length - 1;

        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            var range = ranges[middle];

            if (gameAddress < range.GameAddress)
            {
                high = middle - 1;
            }
            else if (gameAddress >= range.GameAddress + range.Size)
            {
                low = middle + 1;
            }
            else
            {
                found = range;

                return true;
            }
        }

        found = default;

        return false;
    }
}
