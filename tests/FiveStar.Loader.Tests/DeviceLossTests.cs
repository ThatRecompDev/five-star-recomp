// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// A removed device ends the process with its own exit code a few seconds after the first sign, once, instead of the
/// game piling work and memory against it (w80: commit near 106 GB before the boot guard).
/// </summary>
public sealed class DeviceLossTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(DeviceLoss.GraceSeconds * 4);

    [Fact]
    public void The_first_sign_of_a_removal_drops_work_and_ends_the_process_once_with_its_exit_code()
    {
        var original = DeviceLoss.End;
        var ended = new List<int>();
        using var done = new ManualResetEventSlim();

        try
        {
            DeviceLoss.Clear();
            DeviceLoss.Armed = true;
            DeviceLoss.End = code =>
            {
                lock (ended)
                {
                    ended.Add(code);
                }

                done.Set();
            };

            Assert.True(DeviceLoss.IsRemoval(unchecked((int)0x887A0005)));
            Assert.False(DeviceLoss.IsRemoval(unchecked((int)0x887A0001)));

            DeviceLoss.Noticed("a test");
            DeviceLoss.Noticed("a second sign");
            Assert.True(DeviceLoss.Lost);

            Assert.True(done.Wait(Limit));
            Thread.Sleep(100);
            Assert.Equal([DeviceLoss.ExitCode], ended);
        }
        finally
        {
            DeviceLoss.End = original;
            DeviceLoss.Armed = false;
            DeviceLoss.Clear();
        }
    }
}
