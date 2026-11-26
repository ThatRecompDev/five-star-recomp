// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// GFSDK_Aftermath_Lib.x64.dll of the NVIDIA Nsight Aftermath SDK, which the user installs (it is never shipped), loaded
/// at run time: the exports the loader calls, with the C ABI of GFSDK_Aftermath.h, GFSDK_Aftermath_GpuCrashDump.h and
/// GFSDK_Aftermath_GpuCrashDumpDecoding.h. Every call takes the API version the DLL was built with
/// (GFSDK_Aftermath_Version_API), which <see cref="EnableGpuCrashDumps"/> finds by asking.
/// </summary>
internal sealed unsafe class AftermathLibrary
{
    public const string FileName = "GFSDK_Aftermath_Lib.x64.dll";

    /// <summary>The variable the SDK's installer documents for its root; the DLL is in <c>lib\x64</c> under it.</summary>
    public const string SdkVariable = "NSIGHT_AFTERMATH_SDK";

    public const string DownloadPage = "https://developer.nvidia.com/nsight-aftermath";

    /// <summary>GFSDK_Aftermath_Result (GFSDK_Aftermath_Defines.h): failures are 0xBAD000xx.</summary>
    public const uint Success = 0x1;
    public const uint NotAvailable = 0x2;
    public const uint VersionMismatch = 0xBAD00001;
    public const uint DebugLayerNotCompatible = 0xBAD0000A;
    private const uint FailureMask = 0xFFF00000;
    private const uint FailureBase = 0xBAD00000;

    /// <summary>The API versions asked, newest first: 2.x releases of the SDK (GFSDK_Aftermath_Version_API).</summary>
    private const uint NewestVersion = 0x3FF;
    private const uint OldestVersion = 0x200;

    private readonly nint _module;
    private readonly delegate* unmanaged[Cdecl]<uint, uint, uint, nint, nint, nint, nint, void*, uint> _enable;
    private readonly delegate* unmanaged[Cdecl]<uint*, uint> _status;
    private readonly delegate* unmanaged[Cdecl]<uint, uint, nint, uint> _initialize;

    private AftermathLibrary(nint module, nint enable, nint status, nint initialize)
    {
        _module = module;
        _enable = (delegate* unmanaged[Cdecl]<uint, uint, uint, nint, nint, nint, nint, void*, uint>)enable;
        _status = (delegate* unmanaged[Cdecl]<uint*, uint>)status;
        _initialize = (delegate* unmanaged[Cdecl]<uint, uint, nint, uint>)initialize;
    }

    /// <summary>The API version the DLL accepted; 0 before <see cref="EnableGpuCrashDumps"/> found it.</summary>
    public uint Version { get; private set; }

    /// <summary>Whether an Aftermath result is a success (GFSDK_Aftermath_SUCCEED).</summary>
    public static bool Succeeded(uint result)
    {
        return (result & FailureMask) != FailureBase;
    }

    /// <summary>A result as hex, with its header name for the ones the loader acts on.</summary>
    public static string Describe(uint result)
    {
        string hex = string.Create(CultureInfo.InvariantCulture, $"0x{result:x8}");

        return result switch
        {
            Success => $"{hex} (Success)",
            NotAvailable => $"{hex} (NotAvailable)",
            VersionMismatch => $"{hex} (FAIL_VersionMismatch)",
            DebugLayerNotCompatible => $"{hex} (FAIL_D3DDebugLayerNotCompatible: the D3D12 debug layer is on)",
            _ => hex,
        };
    }

    /// <summary>
    /// Where the DLL is: <paramref name="explicitPath"/> (<c>--aftermath-dll</c>, a file or its folder), else next to
    /// the loader, else under <paramref name="sdkRoot"/>\lib\x64; null when none of them has it.
    /// </summary>
    public static string? Find(string? explicitPath, string loaderFolder, string? sdkRoot)
    {
        if (explicitPath is not null)
        {
            string file = explicitPath;

            if (Directory.Exists(explicitPath))
            {
                file = Path.Combine(explicitPath, FileName);
            }

            if (File.Exists(file))
            {
                return Path.GetFullPath(file);
            }

            return null;
        }

        string beside = Path.Combine(loaderFolder, FileName);

        if (File.Exists(beside))
        {
            return beside;
        }

        if (sdkRoot is null)
        {
            return null;
        }

        string installed = Path.Combine(sdkRoot, "lib", "x64", FileName);

        if (File.Exists(installed))
        {
            return installed;
        }

        return null;
    }

    /// <summary>Loads the DLL at <paramref name="path"/>; null with the reason in <paramref name="problem"/> when it is not one.</summary>
    public static AftermathLibrary? Load(string path, out string? problem)
    {
        problem = null;

        if (!NativeLibrary.TryLoad(path, out nint module))
        {
            problem = $"{path} did not load (not an x64 DLL, or its dependencies are missing)";

            return null;
        }

        if (!NativeLibrary.TryGetExport(module, "GFSDK_Aftermath_EnableGpuCrashDumps", out nint enable)
            || !NativeLibrary.TryGetExport(module, "GFSDK_Aftermath_GetCrashDumpStatus", out nint status)
            || !NativeLibrary.TryGetExport(module, "GFSDK_Aftermath_DX12_Initialize", out nint initialize))
        {
            problem = $"{path} lacks GFSDK_Aftermath_EnableGpuCrashDumps, _GetCrashDumpStatus or _DX12_Initialize";
            NativeLibrary.Free(module);

            return null;
        }

        return new AftermathLibrary(module, enable, status, initialize);
    }

    /// <summary>An optional export; 0 when this DLL has none by that name.</summary>
    public nint Export(string name)
    {
        NativeLibrary.TryGetExport(_module, name, out nint address);

        return address;
    }

    /// <summary>
    /// GFSDK_Aftermath_EnableGpuCrashDumps(version, watchedApis, flags, dumpCb, shaderDebugInfoCb, descriptionCb,
    /// resolveMarkerCb, userData), asked with each version from the newest down until the DLL stops answering
    /// FAIL_VersionMismatch; the version it took is <see cref="Version"/>.
    /// </summary>
    public uint EnableGpuCrashDumps(uint watchedApis, uint flags, nint dump, nint shaderDebugInfo, nint description)
    {
        uint result = VersionMismatch;

        for (uint version = NewestVersion; version >= OldestVersion && result == VersionMismatch; version--)
        {
            result = _enable(version, watchedApis, flags, dump, shaderDebugInfo, description, 0, null);

            if (result != VersionMismatch)
            {
                Version = version;
            }
        }

        return result;
    }

    /// <summary>GFSDK_Aftermath_DX12_Initialize(version, featureFlags, ID3D12Device*).</summary>
    public uint InitializeDevice(uint flags, nint device)
    {
        return _initialize(Version, flags, device);
    }

    /// <summary>GFSDK_Aftermath_GetCrashDumpStatus: a GFSDK_Aftermath_CrashDump_Status.</summary>
    public uint CrashDumpStatus()
    {
        uint status = 0;
        _ = _status(&status);

        return status;
    }
}
