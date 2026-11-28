// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Host;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>`--d3d12-describe-views` takes heap indices in decimal or hex and turns the draw trace on.</summary>
public sealed class DescribeViewsOptionTests
{
    [Fact]
    public void Indices_parse_in_decimal_and_hex()
    {
        var options = new CommandLineParser(["--d3d12-describe-views", "90644,0x1620a"]).Parse();

        Assert.Equal([90644u, 0x1620au], options.D3D12DescribeViews);
        Assert.True(options.D3D12TraceDraws);
    }

    [Fact]
    public void Text_that_is_no_index_is_refused()
    {
        Assert.Throws<CommandLineException>(() => new CommandLineParser(["--d3d12-describe-views", "texture"]).Parse());
    }
}
