// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Loading;
using FiveStar.Loader.Native;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// A thread blocked on one of the game's events is reported with the object it waits on, how it was made and whether
/// anyone signaled it since: a lost wake-up names its object instead of only the waiting stack.
/// </summary>
public sealed unsafe class WaitObjectsTests
{
    private const string Prefix = "  [hang] ";
    private const uint DuplicateSameAccess = 2;

    [Fact]
    public void A_blocked_wait_names_its_event_and_its_last_signal()
    {
        nint never = Kernel32.CreateEventW(0, 1, 0, null);
        nint released = Kernel32.CreateEventW(0, 0, 0, null);

        try
        {
            WaitObjects.Created(never, "manual-reset event");
            WaitObjects.Created(released, "auto-reset event");
            using var blocked = new ManualResetEventSlim();
            var waiter = new Thread(() =>
            {
                WaitObjects.Waiting(never, uint.MaxValue);
                blocked.Set();
                _ = Kernel32.WaitForSingleObjectEx(never, uint.MaxValue, 0);
                WaitObjects.Done(never, 0);
            });
            waiter.Start();
            blocked.Wait();

            var report = WaitObjects.Report(new ImageTable(), Prefix, 0);
            Assert.Contains($"(no timeout) on handle 0x{never:x}: manual-reset event made by thread {Kernel32.GetCurrentThreadId()}", report, StringComparison.Ordinal);
            Assert.Contains("never signaled; recent: ", report, StringComparison.Ordinal);
            Assert.Contains("; waiting at no game frame found", report, StringComparison.Ordinal);

            // The signal is recorded with its thread, and the wait ends.
            WaitObjects.Signaled(never);
            Assert.NotEqual(0, Kernel32.SetEvent(never));
            waiter.Join();
            Assert.DoesNotContain($"0x{never:x}", WaitObjects.Report(new ImageTable(), Prefix, 0), StringComparison.Ordinal);
        }
        finally
        {
            WaitObjects.Closed(never);
            WaitObjects.Closed(released);
            _ = Kernel32.CloseHandle(never);
            _ = Kernel32.CloseHandle(released);
        }
    }

    [Fact]
    public void A_blocked_wait_lists_the_last_operations_on_its_object()
    {
        nint idle = Kernel32.CreateEventW(0, 0, 0, null);
        nint other = Kernel32.CreateEventW(0, 0, 0, null);
        uint self = Kernel32.GetCurrentThreadId();

        try
        {
            WaitObjects.Created(idle, "auto-reset event");
            WaitObjects.Created(other, "auto-reset event");

            // A signal, a wait on several objects that it ends, then a reset: the signal the next waiter expects is gone.
            WaitObjects.Signaled(idle);
            WaitObjects.WaitingOnSeveral([other, idle], all: false, uint.MaxValue);
            WaitObjects.DoneOnSeveral([other, idle], all: false, 1);
            WaitObjects.Reset(idle);

            uint waiterId = 0;
            using var blocked = new ManualResetEventSlim();
            var waiter = new Thread(() =>
            {
                waiterId = Kernel32.GetCurrentThreadId();
                WaitObjects.Waiting(idle, uint.MaxValue);
                blocked.Set();
                uint result = Kernel32.WaitForSingleObjectEx(idle, uint.MaxValue, 0);
                WaitObjects.Done(idle, result);
            });

            waiter.Start();
            blocked.Wait();

            string report = WaitObjects.Report(new ImageTable(), Prefix, 0);
            Assert.Matches($@"recent: -\d+\.\d s signaled by {self}, -\d+\.\d s wait on any of 2 by {self}, -\d+\.\d s woke by {self}, -\d+\.\d s reset by {self}, -\d+\.\d s wait by {waiterId}", report);

            Assert.NotEqual(0, Kernel32.SetEvent(idle));
            waiter.Join();
            WaitObjects.Waiting(idle, 0);
            Assert.Contains($"woke by {waiterId}", WaitObjects.Report(new ImageTable(), Prefix, 0), StringComparison.Ordinal);
            WaitObjects.Done(idle, 0x102);
        }
        finally
        {
            WaitObjects.Closed(idle);
            WaitObjects.Closed(other);
            _ = Kernel32.CloseHandle(idle);
            _ = Kernel32.CloseHandle(other);
        }
    }

    /// <summary>A wait on several objects is reported as one of them, and its timeout is kept by every one.</summary>
    [Fact]
    public void A_wait_on_several_objects_names_them_all_and_its_timeout_stays_on_each()
    {
        nint first = Kernel32.CreateEventW(0, 0, 0, null);
        nint second = Kernel32.CreateEventW(0, 0, 0, null);
        uint self = Kernel32.GetCurrentThreadId();

        try
        {
            WaitObjects.Created(first, "auto-reset event");
            WaitObjects.Created(second, "auto-reset event");

            WaitObjects.WaitingOnSeveral([first, second], all: false, 10);
            Assert.Contains($"on one of 2 handles (0x{first:x}, 0x{second:x}), the first 0x{first:x}: ", WaitObjects.Report(new ImageTable(), Prefix, 0), StringComparison.Ordinal);
            WaitObjects.DoneOnSeveral([first, second], all: false, 0x102);

            foreach (var handle in new[] { first, second })
            {
                WaitObjects.Waiting(handle, 0);
                Assert.Matches($@"-\d+\.\d s timed out by {self}", WaitObjects.Report(new ImageTable(), Prefix, 0));
                WaitObjects.Done(handle, 0x102);
            }
        }
        finally
        {
            WaitObjects.Closed(first);
            WaitObjects.Closed(second);
            _ = Kernel32.CloseHandle(first);
            _ = Kernel32.CloseHandle(second);
        }
    }

    [Fact]
    public void A_wait_on_a_thread_handle_names_the_thread_it_joins()
    {
        nint thread = Kernel32.GetCurrentThread();
        nint handle = 0;
        Assert.NotEqual(0, Kernel32.DuplicateHandle(Kernel32.GetCurrentProcess(), thread, Kernel32.GetCurrentProcess(), &handle, 0, 0, DuplicateSameAccess));

        try
        {
            WaitObjects.Waiting(handle, uint.MaxValue);
            var report = WaitObjects.Report(new ImageTable(), Prefix, 0);
            WaitObjects.Done(handle, 0);

            Assert.Contains($"on handle 0x{handle:x}: the handle of thread {Kernel32.GetCurrentThreadId()} (waiting for it to end)", report, StringComparison.Ordinal);
        }
        finally
        {
            _ = Kernel32.CloseHandle(handle);
        }
    }

    /// <summary>A stack value inside an image's range is only read when the call bytes before it are committed.</summary>
    [Fact]
    public void A_value_after_an_uncommitted_page_is_not_read()
    {
        int page = Environment.SystemPageSize;
        nint region = Kernel32.VirtualAlloc(0, (nuint)(page * 2), Win32Constants.MemReserve, Win32Constants.PageReadWrite);
        Assert.NotEqual(0, region);

        try
        {
            Assert.NotEqual(0, Kernel32.VirtualAlloc(region + page, (nuint)page, Win32Constants.MemCommit, Win32Constants.PageReadWrite));

            Assert.False(ReturnAddresses.CallBytesReadable(region + 16));
            Assert.False(ReturnAddresses.CallBytesReadable(region + page + 2));
            Assert.True(ReturnAddresses.CallBytesReadable(region + page + 16));
        }
        finally
        {
            _ = Kernel32.VirtualFree(region, 0, Win32Constants.MemRelease);
        }
    }

    /// <summary>Return addresses are told from other stack values by the call that ends right before them.</summary>
    [Theory]
    [InlineData(new byte[] { 0x90, 0xE8, 1, 2, 3, 4 }, true)]
    [InlineData(new byte[] { 0xFF, 0x15, 1, 2, 3, 4 }, true)]
    [InlineData(new byte[] { 0xFF, 0x93, 1, 2, 3, 4 }, true)]
    [InlineData(new byte[] { 0x90, 0x90, 0x90, 0xFF, 0x50, 8 }, true)]
    [InlineData(new byte[] { 0x90, 0x90, 0x90, 0x41, 0xFF, 0xD2 }, true)]
    [InlineData(new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x57 }, false)]
    public void A_stack_value_counts_as_a_return_address_only_after_a_call(byte[] before, bool call)
    {
        fixed (byte* bytes = before)
        {
            Assert.Equal(call, ReturnAddresses.AfterCall(bytes + before.Length));
        }
    }
}
