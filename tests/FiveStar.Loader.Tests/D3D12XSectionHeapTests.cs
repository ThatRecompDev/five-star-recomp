// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

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
    private const uint Marker = 0x5EC7104E;

    /// <summary>A section larger than one 1 GiB window, as the game's 6.1 GiB one is.</summary>
    private const ulong SectionPages = 20 * 1024;

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
}
