// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FiveStar.Loader.Diagnostics;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// The log's summary of an Aftermath GPU crash dump, read with the SDK's decoder (GFSDK_Aftermath_GpuCrashDumpDecoding.h):
/// the device status, the faulting GPU address and the resource there, the shaders running (named by
/// <see cref="AftermathShaderTable"/>) and the last event marker of each context. Only the leading fields of each
/// struct are read, and each buffer is larger than the struct, so a newer SDK's longer structs fit; Nsight Graphics
/// opens the dump itself for everything else.
/// </summary>
internal static unsafe class AftermathDumpSummary
{
    /// <summary>GFSDK_Aftermath_GpuCrashDump_ShaderInfo {u64 shaderHash; u64 shaderInstance; u32 isInternal; u32 shaderType}.</summary>
    private const int ShaderInfoSize = 24;
    private const int ShaderTypeOffset = 20;

    /// <summary>GFSDK_Aftermath_GpuCrashDump_EventMarkerInfo {u64 contextId; u32 status, type, ownership; void* markerData; u32 markerDataSize}.</summary>
    private const int MarkerInfoSize = 40;
    private const int MarkerStatusOffset = 8;
    private const int MarkerDataOffset = 24;
    private const int MarkerSizeOffset = 32;

    /// <summary>Bytes reserved per struct, past any of the SDK's sizes so far.</summary>
    private const int StructRoom = 1024;

    /// <summary>The longest marker text read and the most shaders and markers listed.</summary>
    private const int MaxMarkerBytes = 1024;
    private const int MaxListed = 32;

    /// <summary>GFSDK_Aftermath_Device_Status and GFSDK_Aftermath_Context_Status (GFSDK_Aftermath_Defines.h).</summary>
    private static readonly string[] DeviceStatuses = ["Active", "Timeout", "OutOfMemory", "PageFault", "Stopped", "Reset", "Unknown", "DmaFault"];
    private static readonly string[] ContextStatuses = ["NotStarted", "Executing", "Finished", "Invalid"];

    /// <summary>Logs what the decoder reads from <paramref name="dump"/>; logs why when it cannot.</summary>
    public static void Write(AftermathLibrary library, ReadOnlySpan<byte> dump, AftermathShaderTable shaders, LoaderLog log)
    {
        var create = (delegate* unmanaged[Cdecl]<uint, byte*, uint, nint*, uint>)library.Export("GFSDK_Aftermath_GpuCrashDump_CreateDecoder");
        var destroy = (delegate* unmanaged[Cdecl]<nint, uint>)library.Export("GFSDK_Aftermath_GpuCrashDump_DestroyDecoder");

        if (create == null || destroy == null)
        {
            log.Warn("[aftermath] no crash-dump decoder in this DLL; open the dump in Nsight Graphics");

            return;
        }

        nint decoder = 0;
        uint result;

        fixed (byte* bytes = dump)
        {
            result = create(library.Version, bytes, (uint)dump.Length, &decoder);
        }

        if (!AftermathLibrary.Succeeded(result))
        {
            log.Warn($"[aftermath] GpuCrashDump_CreateDecoder -> {AftermathLibrary.Describe(result)}");

            return;
        }

        try
        {
            var text = new StringBuilder("[aftermath] crash dump:");
            Device(library, decoder, text);
            PageFault(library, decoder, text);
            ActiveShaders(library, decoder, shaders, text);
            Markers(library, decoder, text);
            log.Error(text.ToString());
        }
        finally
        {
            _ = destroy(decoder);
        }
    }

    private static void Device(AftermathLibrary library, nint decoder, StringBuilder text)
    {
        var info = stackalloc byte[StructRoom];
        var get = (delegate* unmanaged[Cdecl]<nint, byte*, uint>)library.Export("GFSDK_Aftermath_GpuCrashDump_GetDeviceInfo");

        if (get == null || !AftermathLibrary.Succeeded(get(decoder, info)))
        {
            text.Append("\n    device status unknown");

            return;
        }

        text.Append(CultureInfo.InvariantCulture, $"\n    device status {Name(DeviceStatuses, *(uint*)info)}");
    }

    /// <summary>The faulting address (the first field of GFSDK_Aftermath_GpuCrashDump_PageFaultInfo) and the first resource there (GetPageFaultResourceInfo: u64 gpuVa, u64 size first).</summary>
    private static void PageFault(AftermathLibrary library, nint decoder, StringBuilder text)
    {
        var info = stackalloc byte[StructRoom];
        var get = (delegate* unmanaged[Cdecl]<nint, byte*, uint>)library.Export("GFSDK_Aftermath_GpuCrashDump_GetPageFaultInfo");
        uint result = AftermathLibrary.NotAvailable;

        if (get != null)
        {
            result = get(decoder, info);
        }

        if (!AftermathLibrary.Succeeded(result) || result == AftermathLibrary.NotAvailable)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n    no page fault ({AftermathLibrary.Describe(result)})");

            return;
        }

        text.Append(CultureInfo.InvariantCulture, $"\n    page fault at GPU VA 0x{*(ulong*)info:x}");
        var resource = stackalloc byte[StructRoom];
        var getResource = (delegate* unmanaged[Cdecl]<nint, uint, byte*, uint>)library.Export("GFSDK_Aftermath_GpuCrashDump_GetPageFaultResourceInfo");

        if (getResource != null && AftermathLibrary.Succeeded(getResource(decoder, 1, resource)))
        {
            text.Append(CultureInfo.InvariantCulture, $"; resource there: GPU VA 0x{*(ulong*)resource:x}, 0x{*(ulong*)(resource + sizeof(ulong)):x} bytes (details in Nsight Graphics)");
        }
    }

    private static void ActiveShaders(AftermathLibrary library, nint decoder, AftermathShaderTable shaders, StringBuilder text)
    {
        var count = (delegate* unmanaged[Cdecl]<nint, uint*, uint>)library.Export("GFSDK_Aftermath_GpuCrashDump_GetActiveShadersInfoCount");
        var get = (delegate* unmanaged[Cdecl]<nint, uint, byte*, uint>)library.Export("GFSDK_Aftermath_GpuCrashDump_GetActiveShadersInfo");
        var binary = (delegate* unmanaged[Cdecl]<nint, byte*, ulong*, uint>)library.Export("GFSDK_Aftermath_GetShaderHashForShaderInfo");
        uint shaderCount = 0;

        if (count == null || get == null || !AftermathLibrary.Succeeded(count(decoder, &shaderCount)) || shaderCount == 0)
        {
            text.Append("\n    no active shaders recorded");

            return;
        }

        var infos = (byte*)NativeMemory.AllocZeroed((nuint)(shaderCount * StructRoom));

        try
        {
            if (!AftermathLibrary.Succeeded(get(decoder, shaderCount, infos)))
            {
                return;
            }

            for (uint i = 0; i < Math.Min(shaderCount, (uint)MaxListed); i++)
            {
                byte* info = infos + (i * ShaderInfoSize);
                ulong hash = 0;

                if (binary != null)
                {
                    _ = binary(decoder, info, &hash);
                }

                string name = shaders.Describe(hash, QueueProgress.PipelineName) ?? "not a shader of ours (or no hash)";
                text.Append(CultureInfo.InvariantCulture, $"\n    active shader {i}: type {*(uint*)(info + ShaderTypeOffset)}, hash 0x{*(ulong*)info:x}, binary hash 0x{hash:x16}: {name}");
            }
        }
        finally
        {
            NativeMemory.Free(infos);
        }
    }

    private static void Markers(AftermathLibrary library, nint decoder, StringBuilder text)
    {
        var count = (delegate* unmanaged[Cdecl]<nint, uint*, uint>)library.Export("GFSDK_Aftermath_GpuCrashDump_GetEventMarkersInfoCount");
        var get = (delegate* unmanaged[Cdecl]<nint, uint, byte*, uint>)library.Export("GFSDK_Aftermath_GpuCrashDump_GetEventMarkersInfo");
        uint markerCount = 0;

        if (count == null || get == null || !AftermathLibrary.Succeeded(count(decoder, &markerCount)) || markerCount == 0)
        {
            text.Append("\n    no event markers (--aftermath-markers sets them)");

            return;
        }

        var infos = (byte*)NativeMemory.AllocZeroed((nuint)(markerCount * StructRoom));

        try
        {
            if (!AftermathLibrary.Succeeded(get(decoder, markerCount, infos)))
            {
                return;
            }

            for (uint i = 0; i < Math.Min(markerCount, (uint)MaxListed); i++)
            {
                byte* info = infos + (i * MarkerInfoSize);
                string status = Name(ContextStatuses, *(uint*)(info + MarkerStatusOffset));
                text.Append(CultureInfo.InvariantCulture, $"\n    context 0x{*(ulong*)info:x} {status}: last marker {MarkerText(*(nint*)(info + MarkerDataOffset), *(uint*)(info + MarkerSizeOffset))}");
            }
        }
        finally
        {
            NativeMemory.Free(infos);
        }
    }

    /// <summary>A marker's text (ours are NUL-terminated UTF-8), read only where the process can read it.</summary>
    private static string MarkerText(nint data, uint size)
    {
        if (data == 0 || size == 0 || size > MaxMarkerBytes || !GpuBreadcrumbs.Readable((ulong)data, size))
        {
            return "(none)";
        }

        return Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)data, (int)size)).TrimEnd('\0');
    }

    private static string Name(string[] names, uint value)
    {
        if (value < names.Length)
        {
            return names[value];
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }
}
