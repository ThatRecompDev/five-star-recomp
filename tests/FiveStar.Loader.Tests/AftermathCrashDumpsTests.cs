// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Host;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using FiveStar.Loader.Tests.Support;
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

        // Without the switches the device gets flags 0: no markers, resource tracking or shader debug info (boot w181).
        var light = new AftermathFeatures(false, plain.AftermathResources, plain.AftermathShaderInfo);
        var full = new CommandLineParser(["--aftermath-resources", "--aftermath-shader-info"]).Parse();
        Assert.Equal(0u, AftermathCrashDumps.DeviceFlags(light));
        Assert.Equal(0xAu, AftermathCrashDumps.DeviceFlags(new AftermathFeatures(full.AftermathMarkers, full.AftermathResources, full.AftermathShaderInfo)));
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

    /// <summary>The real DLL: every export the loader calls is there, the version probe lands on a version it takes, and the calls return result codes.</summary>
    [AftermathFact]
    public unsafe void TalksToTheSdkDll()
    {
        string[] exports =
        [
            "GFSDK_Aftermath_DX12_CreateContextHandle", "GFSDK_Aftermath_SetEventMarker", "GFSDK_Aftermath_GetShaderDebugInfoIdentifier",
            "GFSDK_Aftermath_GetShaderHash", "GFSDK_Aftermath_GpuCrashDump_CreateDecoder", "GFSDK_Aftermath_GpuCrashDump_DestroyDecoder",
            "GFSDK_Aftermath_GpuCrashDump_GetDeviceInfo", "GFSDK_Aftermath_GpuCrashDump_GetPageFaultInfo", "GFSDK_Aftermath_GpuCrashDump_GetPageFaultResourceInfo",
            "GFSDK_Aftermath_GpuCrashDump_GetActiveShadersInfoCount", "GFSDK_Aftermath_GpuCrashDump_GetActiveShadersInfo", "GFSDK_Aftermath_GetShaderHashForShaderInfo",
            "GFSDK_Aftermath_GpuCrashDump_GetEventMarkersInfoCount", "GFSDK_Aftermath_GpuCrashDump_GetEventMarkersInfo",
        ];
        var console = new StringWriter();
        var library = AftermathLibrary.Load(AftermathFactAttribute.Library!, out string? problem);

        Assert.NotNull(library);
        Assert.Null(problem);
        Assert.All(exports, name => Assert.NotEqual(0, library.Export(name)));

        try
        {
            var dumps = AftermathCrashDumps.Start(Path.GetTempPath(), AftermathFactAttribute.Library, default, string.Empty, null, new LoaderLog(console, null, false));
            Assert.Contains("[aftermath]", console.ToString(), StringComparison.Ordinal);

            // The SDK of 2026.3 is API version 0x21B; any 2.x DLL answers a version it takes, not FAIL_VersionMismatch.
            Assert.DoesNotContain("no API version", console.ToString(), StringComparison.Ordinal);
            Assert.InRange(library.CrashDumpStatus(), 0u, 5u);

            // A decoder over bytes that are no dump is refused with a result code.
            var create = (delegate* unmanaged[Cdecl]<uint, byte*, uint, nint*, uint>)library.Export("GFSDK_Aftermath_GpuCrashDump_CreateDecoder");
            var junk = stackalloc byte[64];
            nint decoder = 0;
            Assert.False(AftermathLibrary.Succeeded(create(dumps?.Version ?? 0x21B, junk, 64, &decoder)));
        }
        finally
        {
            AftermathCrashDumps.Clear();
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
            Assert.Null(AftermathCrashDumps.Start(Path.Combine(root, "dumps"), null, default, root, null, log));
            Assert.Contains(AftermathLibrary.DownloadPage, console.ToString(), StringComparison.Ordinal);

            // A file by that name that is no DLL loads nothing and ends nothing either.
            string broken = Path.Combine(root, AftermathLibrary.FileName);
            File.WriteAllText(broken, "not a DLL");
            Assert.Null(AftermathCrashDumps.Start(Path.Combine(root, "dumps"), broken, default, root, null, log));

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
