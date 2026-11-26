// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Host;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary><c>--aftermath</c>: the options, where the SDK's DLL is looked for, and a run without it.</summary>
public sealed class AftermathCrashDumpsTests
{
    [Fact]
    public void ParsesTheOptionalFolderAndTheDll()
    {
        var plain = new CommandLineParser(["--aftermath", "--aftermath-markers"]).Parse();
        var folder = new CommandLineParser(["--aftermath", "dumps", "--aftermath-dll", "sdk"]).Parse();
        var implied = new CommandLineParser(["--aftermath-dll", "sdk"]).Parse();

        Assert.Equal(Path.GetFullPath("aftermath"), plain.AftermathDirectory);
        Assert.True(plain.AftermathMarkers);

        Assert.Equal(Path.GetFullPath("dumps"), folder.AftermathDirectory);
        Assert.Equal(Path.GetFullPath("sdk"), folder.AftermathLibrary);
        Assert.False(folder.AftermathMarkers);
        Assert.Equal(Path.GetFullPath("aftermath"), implied.AftermathDirectory);
        Assert.Null(new CommandLineParser([]).Parse().AftermathDirectory);
    }

    [Fact]
    public void FindsTheDllBesideTheLoaderThenInTheSdk()
    {
        string root = Path.Combine(Path.GetTempPath(), "aftermath-" + Guid.NewGuid().ToString("N"));
        string loader = Path.Combine(root, "loader");
        string sdkLib = Path.Combine(root, "sdk", "lib", "x64");

        try
        {
            Directory.CreateDirectory(loader);
            Directory.CreateDirectory(sdkLib);
            Assert.Null(AftermathLibrary.Find(null, loader, Path.Combine(root, "sdk")));

            File.WriteAllText(Path.Combine(sdkLib, AftermathLibrary.FileName), string.Empty);
            Assert.Equal(Path.Combine(sdkLib, AftermathLibrary.FileName), AftermathLibrary.Find(null, loader, Path.Combine(root, "sdk")));
            Assert.Equal(Path.Combine(sdkLib, AftermathLibrary.FileName), AftermathLibrary.Find(sdkLib, loader, null));

            File.WriteAllText(Path.Combine(loader, AftermathLibrary.FileName), string.Empty);
            Assert.Equal(Path.Combine(loader, AftermathLibrary.FileName), AftermathLibrary.Find(null, loader, Path.Combine(root, "sdk")));
            Assert.Null(AftermathLibrary.Find(Path.Combine(root, "missing.dll"), loader, null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RunsOnWithOneLineWhenTheDllIsMissingOrBroken()
    {
        string root = Path.Combine(Path.GetTempPath(), "aftermath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var console = new StringWriter();
        var log = new LoaderLog(console, null, false);

        try
        {
            Assert.Null(AftermathCrashDumps.Start(Path.Combine(root, "dumps"), null, false, root, null, log));
            Assert.Contains(AftermathLibrary.DownloadPage, console.ToString(), StringComparison.Ordinal);

            // A file by that name that is no DLL loads nothing and ends nothing either.
            string broken = Path.Combine(root, AftermathLibrary.FileName);
            File.WriteAllText(broken, "not a DLL");
            Assert.Null(AftermathCrashDumps.Start(Path.Combine(root, "dumps"), broken, false, root, null, log));

            Assert.Null(AftermathCrashDumps.Active);
            Assert.Equal(2, console.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.False(Directory.Exists(Path.Combine(root, "dumps")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
