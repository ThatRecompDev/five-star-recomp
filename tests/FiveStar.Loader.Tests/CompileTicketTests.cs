// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Pipelines;
using FiveStar.Loader.Tests.Support;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// Exit takes back exactly the compile its ticket stands for (issue #13): matching by estimate removed another compile's
/// entry of the same estimate, or none, and the one left behind kept a reservation that refused every heavy compile after.
/// The compiles enter urgent (only the floor and their own estimates are checked), so a busy machine still admits them.
/// </summary>
[Collection(CompileBudgetState.Name)]
public sealed class CompileTicketTests
{
    private const long Large = CompileBudget.TinyShaderBytes;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Two_compiles_of_the_same_estimate_leave_nothing_reserved_whichever_ends_first(bool firstEndsFirst)
    {
        var before = CompileBudget.Held();
        int running = CompileBudget.Running;
        var (first, second) = EnterTwo(Large, Large);

        Assert.Equal(first.Estimate, second.Estimate);
        Assert.Equal((before.RunningLarge + 2, before.Entries + 2, before.Expected + (2 * first.Estimate)), CompileBudget.Held());

        if (firstEndsFirst)
        {
            CompileBudget.Exit(first);
            Assert.Equal((before.RunningLarge + 1, before.Entries + 1, before.Expected + second.Estimate), CompileBudget.Held());
            CompileBudget.Exit(second);
        }
        else
        {
            CompileBudget.Exit(second);
            Assert.Equal((before.RunningLarge + 1, before.Entries + 1, before.Expected + first.Estimate), CompileBudget.Held());
            CompileBudget.Exit(first);
        }

        Assert.Equal(before, CompileBudget.Held());
        Assert.Equal(running, CompileBudget.Running);
    }

    [Fact]
    public void Compiles_of_other_sizes_tiny_ones_among_them_leave_nothing_reserved_and_a_second_exit_changes_nothing()
    {
        var before = CompileBudget.Held();
        int running = CompileBudget.Running;
        var (small, big) = EnterTwo(Large, 3 * Large);
        var tiny = Admit(Large - 1);

        Assert.False(tiny.Large);
        Assert.NotEqual(small.Estimate, big.Estimate);
        Assert.Equal(before.Entries + 2, CompileBudget.Held().Entries);

        // Each ends with its own ticket, not the bytes the caller holds then: the tiny one first, the big one twice.
        CompileBudget.Exit(tiny);
        CompileBudget.Exit(big);
        CompileBudget.Exit(big);
        CompileBudget.Exit(null);
        Assert.Equal((before.RunningLarge + 1, before.Entries + 1, before.Expected + small.Estimate), CompileBudget.Held());
        CompileBudget.Exit(small);

        Assert.Equal(before, CompileBudget.Held());
        Assert.Equal(running, CompileBudget.Running);
    }

    private static (CompileTicket First, CompileTicket Second) EnterTwo(long firstBytes, long secondBytes)
    {
        var first = Admit(firstBytes);

        try
        {
            return (first, Admit(secondBytes));
        }
        catch
        {
            CompileBudget.Exit(first);

            throw;
        }
    }

    private static CompileTicket Admit(long shaderBytes)
    {
        bool urgent = CompileBudget.Urgent;
        CompileBudget.Urgent = true;

        try
        {
            var ticket = CompileBudget.TryEnterNow(shaderBytes);

            return ticket ?? throw new Xunit.Sdk.XunitException($"the budget refused {shaderBytes} bytes: {CompileBudget.Refusals}");
        }
        finally
        {
            CompileBudget.Urgent = urgent;
        }
    }
}
