// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using Xunit;

namespace FiveStar.Cli.Tests;

/// <summary>A command whose option lacks its value is refused instead of running without the option.</summary>
public sealed class CommandArgumentsTests
{
    private const int UsageError = 2;

    [Theory]
    [InlineData("--limit-mb")]
    [InlineData("--limit-mb", "--list")]
    public void An_option_without_its_value_stops_the_command_before_it_writes(params string[] options)
    {
        var output = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string[] arguments = ["archive", "carve", "missing.rpf", output, .. options];

        var exit = Program.Main(arguments);

        Assert.Equal(UsageError, exit);
        Assert.False(Directory.Exists(output));
    }
}
