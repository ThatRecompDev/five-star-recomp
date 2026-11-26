// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Host;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary><c>--pix-capture</c>: the options, and the newest capturer under Microsoft PIX picked as PIX's own loader does.</summary>
public sealed class PixGpuCaptureTests
{
    [Fact]
    public void ParsesPresentsAndFolder()
    {
        var options = new CommandLineParser(["--pix-capture", "600,1500", "--pix-capture-dir", "caps"]).Parse();

        Assert.Equal([600L, 1500L], options.PixCapturePresents);
        Assert.Equal(Path.GetFullPath("caps"), options.PixCaptureDirectory);
    }

    [Fact]
    public void FindsTheNewestCapturer()
    {
        string root = Path.Combine(Path.GetTempPath(), "pix-" + Guid.NewGuid().ToString("N"));
        string pix = Path.Combine(root, "Microsoft PIX");

        try
        {
            Assert.Null(PixGpuCapture.FindCapturer(root));

            foreach (string version in new[] { "2408.09", "2509.25", "2601.01-empty" })
            {
                Directory.CreateDirectory(Path.Combine(pix, version));
            }

            File.WriteAllText(Path.Combine(pix, "2408.09", "WinPixGpuCapturer.dll"), string.Empty);
            File.WriteAllText(Path.Combine(pix, "2509.25", "WinPixGpuCapturer.dll"), string.Empty);

            Assert.Equal(Path.Combine(pix, "2509.25", "WinPixGpuCapturer.dll"), PixGpuCapture.FindCapturer(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
