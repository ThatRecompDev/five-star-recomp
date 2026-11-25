// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Tests.Support;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// A machine whose GPU cannot back all of the game's page sections (issue #13: a 16 GB machine, whose GPU system-memory
/// budget is about 8 GB): heaps open only as far as the budget goes, and what does not fit falls back instead of failing.
/// </summary>
public sealed unsafe class D3D12XSectionHeapTests
{
    private const ulong Page = 0x10000;
    private const ulong Mebibyte = 1UL << 20;
    private const uint Reserve64K = 0x20402000;
    private const ulong CpuFlags = 0x21;
    private const uint CpuReadWrite = 0x4;
    private const uint CommitReserve = 0x3000;
    private const uint Marker = 0x5EC7104E;

    /// <summary>A section larger than one 1 GiB window, as the game's 6.1 GiB one is.</summary>
    private const ulong SectionPages = 20 * 1024;

    /// <summary>A page in the 11th window, past the three the budget opens.</summary>
    private const ulong WantedOffset = 0x28010000;
    private const uint IdMask = 0xFFFF;
    private const long FirstPresent = 100;
    /// <summary>The game's view heap: its desktop heap leaves remap pages for the map's views.</summary>
    private const int GameViews = 1245184;
    private const int CreateDescriptorHeapSlot = 14;

    [Fact]
    public void A_section_over_the_budget_opens_windows_where_buffers_are_placed_and_the_rest_falls_back()
    {
        using var log = new StringWriter();
        using var test = TestRuntime.Create(log: log);
        var virtualAlloc = (delegate* unmanaged<nint, nuint, uint, ulong, uint, nint>)test.Shim("xmem.dll", "XMemVirtualAlloc");
        var allocate = (delegate* unmanaged<ulong, ulong*, ulong*, int>)test.Shim("xmem.dll", "XMemAllocatePhysicalPages");
        var map = (delegate* unmanaged<nint, ulong, ulong*, nint>)test.Shim("xmem.dll", "XMemMapPhysicalPages");

        var device = new XboxDevice(test);
        var heaps = test.Runtime.D3D12X.SectionHeaps;
        heaps.Cap = 128 * Mebibyte;

        // One section of 1.25 GiB mapped whole, as the game maps its large allocation.
        ulong[] frames = new ulong[SectionPages];
        ulong count = SectionPages;
        nint range = virtualAlloc(0, (nuint)(SectionPages * Page), Reserve64K, CpuFlags, CpuReadWrite);

        fixed (ulong* first = frames)
        {
            Assert.Equal(1, allocate(0, &count, first));
            Assert.Equal(range, map(range, SectionPages, first));
        }

        // Two buffers fit the 128 MiB in lazy 64 MiB windows; the third finds no room and gets a buffer of its own.
        Assert.Equal(0, device.PlacedBuffer(range + 0x10000, Page, out _));
        Assert.Equal(0, device.PlacedBuffer(range + 0x10010000, Page, out nint second));
        Assert.Equal(0, device.PlacedBuffer(range + 0x20000000, Page, out _));
        Assert.Equal(128 * Mebibyte, heaps.Opened);

        Assert.Contains("backed in 64 MiB windows", log.ToString(), StringComparison.Ordinal);
        Assert.Contains("no heap at +0x20000000 for a placed buffer", log.ToString(), StringComparison.Ordinal);
        Assert.Contains("committed buffer of 0x10000 bytes (not shared with the game)", log.ToString(), StringComparison.Ordinal);

        // The buffer in the window at +256 MiB is the game's page: a GPU write lands at its CPU address.
        nint upload = device.Buffer(XboxDevice.UploadHeap, Page, XboxDevice.GenericRead);
        XboxDevice.WriteFirst(upload, Marker);
        device.Copy(second, upload, 0, Page);

        Assert.Equal(Marker, *(uint*)(range + 0x10010000));
    }

    /// <summary>
    /// --evict-windows on a budget scaled down from --section-heap-budget 3: three 64 MiB windows of room for the
    /// address map. A program reading a page whose window has no heap sets that window's flag; at the next present the
    /// window wanted least recently is released for it (its pages wait again at once), its heap goes 8 presents later,
    /// and the asked-for window opens with its pages mapped. A window a placed buffer lives on is never released.
    /// </summary>
    [Fact]
    public void Windows_programs_ask_for_replace_the_least_recently_wanted_ones()
    {
        WindowFeedback.Enabled = true;

        try
        {
            using var test = TestRuntime.Create();
            var device = new XboxDevice(test);
            var d3d12 = test.Runtime.D3D12X;
            d3d12.SectionHeaps.Cap = SectionHeaps.PlacedReserve + (3 * SectionHeaps.LazyWindowBytes);
            ShaderVisibleViewHeap(device);
            nint range = MapSection(test);

            var heaps = d3d12.SectionHeaps;
            var views = d3d12.MemoryViews;
            uint* flags = d3d12.Bindings.Window.WindowFlags;
            nint desktop = d3d12.Objects.DesktopDevice;

            // The address map opened the first three windows; a page at +640 MiB waits, tagged with its window's id.
            Assert.Equal(3 * SectionHeaps.LazyWindowBytes, heaps.Opened);
            var (view, field) = views.Entry((ulong)range + WantedOffset);
            Assert.Equal(DescriptorWindow.UnmappedSlot, view);
            Assert.Equal(WindowFeedback.WaitTag, field & WindowFeedback.WaitTag);
            var wanted = heaps.ById((int)(field & IdMask))!;
            var section = wanted.Chunk.Section;

            // A buffer of the game's pins the first window.
            Assert.Equal(0, device.PlacedBuffer(range + (nint)Page, Page, out _));

            // A program reads the page (gpu_page stores the flag): the window at +64 MiB is released for it.
            flags[wanted.Id] = 1;
            d3d12.Residency!.Poll(desktop, flags, FirstPresent);
            var released = heaps.Find(section, SectionHeaps.LazyWindowBytes)!;
            Assert.True(released.Retiring);
            Assert.False(heaps.Find(section, 0)!.Retiring);
            Assert.Equal(DescriptorWindow.UnmappedSlot, views.Entry((ulong)range + SectionHeaps.LazyWindowBytes).View);

            // Its heap goes once the GPU is past it, and the window asked for opens in its place.
            flags[wanted.Id] = 1;
            d3d12.Residency.Poll(desktop, flags, FirstPresent + WindowResidency.RetirePresents);
            Assert.Equal(0, released.Heap);
            Assert.NotEqual(0, wanted.Heap);
            Assert.NotEqual(DescriptorWindow.UnmappedSlot, views.Entry((ulong)range + WantedOffset).View);
            Assert.Equal(3 * SectionHeaps.LazyWindowBytes, heaps.Opened);
        }
        finally
        {
            WindowFeedback.Enabled = false;
        }
    }

    /// <summary>
    /// --evict-windows on a device with OpenExistingHeapFromAddress1: memory the game committed itself is covered by the
    /// 64 MiB window around the first address the GPU is given, not the whole allocation (the game's 2.2 GiB heap), and
    /// a buffer there reads the game's bytes.
    /// </summary>
    [Fact]
    public void Memory_the_game_committed_is_opened_by_the_window()
    {
        WindowFeedback.Enabled = true;

        try
        {
            using var log = new StringWriter();
            using var test = TestRuntime.Create(log: log);
            var device = new XboxDevice(test);

            var virtualAlloc = (delegate* unmanaged<nint, nuint, uint, ulong, uint, nint>)test.Shim("xmem.dll", "XMemVirtualAlloc");
            nint memory = virtualAlloc(0, (nuint)(3 * SectionHeaps.LazyWindowBytes), CommitReserve, 0, CpuReadWrite);
            nint address = memory + (nint)(2 * SectionHeaps.LazyWindowBytes) + (nint)Page;
            *(uint*)address = Marker;

            Assert.Equal(0, device.PlacedBuffer(address, Page, out nint placed));
            nint readback = device.Buffer(XboxDevice.ReadbackHeap, Page, XboxDevice.CopyDest);
            device.Copy(readback, placed, 0, Page);

            Assert.True(test.Runtime.D3D12X.PlacedResources.Addresses.TryFind((ulong)address, out var range));

            if (!log.ToString().Contains("no ID3D12Device13", StringComparison.Ordinal))
            {
                Assert.Equal((ulong)memory + (2 * SectionHeaps.LazyWindowBytes), range.GameAddress);
                Assert.Equal(SectionHeaps.LazyWindowBytes, range.Size);
            }

            Assert.Equal(Marker, XboxDevice.ReadFirst(readback));
        }
        finally
        {
            WindowFeedback.Enabled = false;
        }
    }

    /// <summary>One section of 1.25 GiB mapped whole, as the game maps its large allocation.</summary>
    private static nint MapSection(TestRuntime test)
    {
        var virtualAlloc = (delegate* unmanaged<nint, nuint, uint, ulong, uint, nint>)test.Shim("xmem.dll", "XMemVirtualAlloc");
        var allocate = (delegate* unmanaged<ulong, ulong*, ulong*, int>)test.Shim("xmem.dll", "XMemAllocatePhysicalPages");
        var map = (delegate* unmanaged<nint, ulong, ulong*, nint>)test.Shim("xmem.dll", "XMemMapPhysicalPages");
        ulong[] frames = new ulong[SectionPages];
        ulong count = SectionPages;
        nint range = virtualAlloc(0, (nuint)(SectionPages * Page), Reserve64K, CpuFlags, CpuReadWrite);

        fixed (ulong* first = frames)
        {
            Assert.Equal(1, allocate(0, &count, first));
            Assert.Equal(range, map(range, SectionPages, first));
        }

        return range;
    }

    /// <summary>Device slot 14: the game's shader-visible view heap, which attaches the descriptor window and the address map.</summary>
    private static void ShaderVisibleViewHeap(XboxDevice device)
    {
        int* description = stackalloc int[] { 0, GameViews, 1, 0 };
        Guid iid = new("8efb471d-616c-4f49-90f7-127bb763fa51");
        nint heap = 0;
        Assert.Equal(0, ((delegate* unmanaged<nint, int*, Guid*, nint*, int>)XboxDevice.Vtable(device.Device)[CreateDescriptorHeapSlot])(device.Device, description, &iid, &heap));
    }
}
