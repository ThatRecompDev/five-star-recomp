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

    [Fact]
    public void The_command_line_hides_the_users_profile_folder()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string text = RunIdentity.Redact($"--save-dir {profile}\\saves --log x.log");

        Assert.DoesNotContain(profile, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%\\saves", text, StringComparison.Ordinal);
    }
}
