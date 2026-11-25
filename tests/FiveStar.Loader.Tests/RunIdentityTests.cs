// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;
using Xunit;

namespace FiveStar.Loader.Tests;

public sealed class RunIdentityTests
{
    [Fact]
    public void NamesTheBuildCommitAndTheCommandLine()
    {
        var line = RunIdentity.Line();

        Assert.Matches(@"^build \S+\+[0-9a-f]{40}; \.NET ", line);
        Assert.Contains("; command line: ", line, StringComparison.Ordinal);
    }
}
