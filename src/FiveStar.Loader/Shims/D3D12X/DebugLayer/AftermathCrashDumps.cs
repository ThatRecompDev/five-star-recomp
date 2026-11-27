// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--aftermath [dir]</c>: NVIDIA Nsight Aftermath GPU crash dumps (docs/debugging.md#aftermath). Before the desktop
/// device exists, GPU crash dumps are enabled; after, the device is initialized with only the features asked for
/// (<see cref="AftermathFeatures"/>: by default none, flags 0). At a GPU fault the driver hands over
/// <c>crash-*.nv-gpudmp</c> (with <c>--aftermath-shader-info</c> also the <c>shader-*.nvdbg</c> files of the shaders it
/// ran), which go to the folder with a table of our shader hashes, and
/// the log gets a summary (<see cref="AftermathDumpSummary"/>). The device-loss end waits for that
/// (<see cref="Report"/>, from the first sign of the removal on). With <c>--aftermath-markers</c> every draw, dispatch, ExecuteIndirect, copy and
/// barrier also sets an event marker naming its pipeline, and call stacks are captured: both cost per draw.
/// </summary>
internal sealed unsafe class AftermathCrashDumps
{
    /// <summary>GFSDK_Aftermath_GpuCrashDumpWatchedApiFlags_DX and GpuCrashDumpFeatureFlags_DeferDebugInfoCallbacks (GFSDK_Aftermath_GpuCrashDump.h).</summary>
    private const uint WatchDx = 0x1;
    private const uint DeferDebugInfoCallbacks = 0x1;

    /// <summary>GFSDK_Aftermath_FeatureFlags (GFSDK_Aftermath.h).</summary>
    private const uint EnableMarkers = 0x1;
    private const uint EnableResourceTracking = 0x2;
    private const uint GenerateShaderDebugInfo = 0x8;
    private const uint CallStackCapturing = 0x40000000;

    /// <summary>GFSDK_Aftermath_CrashDump_Status (GFSDK_Aftermath_GpuCrashDump.h): NotStarted 0, CollectingData 1, CollectingDataFailed 2, InvokingCallback 3, Finished 4.</summary>
    private const uint NotStarted = 0;
    private const uint CollectingDataFailed = 2;
    private const uint Finished = 4;

    /// <summary>GFSDK_Aftermath_GpuCrashDumpDescriptionKey_ApplicationName.</summary>
    private const uint ApplicationNameKey = 0x1;

    /// <summary>How long the device-loss end waits for the dump, and how often it asks.</summary>
    public static readonly TimeSpan DumpWait = TimeSpan.FromSeconds(15);

    /// <summary>GFSDK_Aftermath_CrashDump_Status names, by value.</summary>
    private static readonly string[] CrashDumpStatuses = ["NotStarted", "CollectingData", "CollectingDataFailed", "InvokingCallback", "Finished", "Unknown"];

    /// <summary>GFSDK_Aftermath_Device_Status_Active (GFSDK_Aftermath_Defines.h).</summary>
    private const uint DeviceActive = 0;

    /// <summary>ID3D12Device::GetDeviceRemovedReason (d3d12.h).</summary>
    private const int RemovedReasonSlot = 37;

    /// <summary>Bytes kept for GFSDK_Aftermath_PageFaultInformation, past its size (a u64, a resource descriptor, a u32).</summary>
    private const int PageFaultRoom = 1024;
    private const int PollMilliseconds = 50;

    /// <summary>A list whose context handle could not be made (<see cref="ListActivity.Aftermath"/>).</summary>
    private const nint NoContext = -1;

    /// <summary>GFSDK_Aftermath_ShaderDebugInfoIdentifier: uint64_t id[2].</summary>
    private const int IdentifierWords = 2;

    /// <summary>The longest marker text, in characters.</summary>
    private const int MaxMarkerChars = 240;

    private static readonly byte[] ApplicationName = Encoding.UTF8.GetBytes("Five Star Recomp\0");

    private readonly AftermathLibrary _library;
    private readonly string _directory;
    private readonly AftermathFeatures _features;
    private readonly LoaderLog _log;
    private readonly ConcurrentDictionary<(nint Pipeline, MarkedKind Kind), nint> _markerTexts = new();
    private readonly ConcurrentDictionary<nint, nint> _contexts = new();
    private readonly delegate* unmanaged[Cdecl]<nint, nint*, uint> _createContext;
    private readonly delegate* unmanaged[Cdecl]<nint, void*, uint, uint> _setMarker;
    private readonly delegate* unmanaged[Cdecl]<uint, void*, uint, ulong*, uint> _debugInfoIdentifier;
    private readonly delegate* unmanaged[Cdecl]<uint*, uint> _deviceStatus;
    private readonly delegate* unmanaged[Cdecl]<byte*, uint> _pageFault;
    private readonly object _reportGate = new();
    private nint _device;
    private bool _reported;
    private int _unnamedShaderFiles;

    public AftermathCrashDumps(AftermathLibrary library, string directory, AftermathFeatures features, LoaderLog log)
    {
        _library = library;
        _directory = directory;
        _features = features;
        _log = log;

        _createContext = (delegate* unmanaged[Cdecl]<nint, nint*, uint>)library.Export("GFSDK_Aftermath_DX12_CreateContextHandle");
        _setMarker = (delegate* unmanaged[Cdecl]<nint, void*, uint, uint>)library.Export("GFSDK_Aftermath_SetEventMarker");
        _debugInfoIdentifier = (delegate* unmanaged[Cdecl]<uint, void*, uint, ulong*, uint>)library.Export("GFSDK_Aftermath_GetShaderDebugInfoIdentifier");
        _deviceStatus = (delegate* unmanaged[Cdecl]<uint*, uint>)library.Export("GFSDK_Aftermath_GetDeviceStatus");
        _pageFault = (delegate* unmanaged[Cdecl]<byte*, uint>)library.Export("GFSDK_Aftermath_GetPageFaultInformation");
        Shaders = new AftermathShaderTable(0, 0);
    }

    /// <summary>The crash dumps of this run; null without <c>--aftermath</c> or without the DLL.</summary>
    public static AftermathCrashDumps? Active { get; private set; }

    /// <summary>Our shaders by Aftermath hash, noted once the API version is known.</summary>
    public AftermathShaderTable Shaders { get; private set; }

    /// <summary>The API version the DLL took.</summary>
    public uint Version => _library.Version;

    /// <summary>Whether the device was initialized for Aftermath (markers need it).</summary>
    public bool Initialized { get; private set; }

    /// <summary>
    /// Finds and loads the DLL (<see cref="AftermathLibrary.Find"/>) and enables GPU crash dumps; must run before
    /// D3D12CreateDevice. Logs one line and returns null when the DLL is missing or refuses.
    /// </summary>
    public static AftermathCrashDumps? Start(string directory, string? dllPath, AftermathFeatures features, string loaderFolder, string? sdkRoot, LoaderLog log)
    {
        string? path = AftermathLibrary.Find(dllPath, loaderFolder, sdkRoot);

        if (path is null)
        {
            log.Warn($"[aftermath] {AftermathLibrary.FileName} not found ({dllPath ?? $"next to the loader or under %{AftermathLibrary.SdkVariable}%\\lib\\x64"}): install the Nsight Aftermath SDK from {AftermathLibrary.DownloadPage} and pass --aftermath-dll <path>; no GPU crash dumps");

            return null;
        }

        var library = AftermathLibrary.Load(path, out string? problem);

        if (library is null)
        {
            log.Warn($"[aftermath] {problem}; no GPU crash dumps");

            return null;
        }

        var dumps = new AftermathCrashDumps(library, directory, features, log);

        if (!dumps.Enable(path))
        {
            return null;
        }

        Active = dumps;

        return dumps;
    }

    /// <summary>Forgets the active crash dumps (tests).</summary>
    public static void Clear()
    {
        Active = null;
    }

    /// <summary>After D3D12CreateDevice: GFSDK_Aftermath_DX12_Initialize on <paramref name="device"/>.</summary>
    public void AttachDevice(nint device)
    {
        _device = device;
        uint flags = DeviceFlags(_features);
        uint result = _library.InitializeDevice(flags, device);
        Initialized = AftermathLibrary.Succeeded(result);

        if (!Initialized)
        {
            _log.Warn($"[aftermath] DX12_Initialize -> {AftermathLibrary.Describe(result)}; crash dumps still come, without markers, resource names or shader debug info");

            return;
        }

        _log.Info($"  [aftermath] device initialized (flags 0x{flags:x}): markers {_features.Markers}, resource tracking {_features.Resources}, shader debug info {_features.ShaderInfo}");
    }

    /// <summary>The GFSDK_Aftermath_DX12_Initialize flags for <paramref name="features"/>; 0 (Minimum) for none.</summary>
    public static uint DeviceFlags(AftermathFeatures features)
    {
        uint flags = 0;

        if (features.Markers)
        {
            flags |= EnableMarkers | CallStackCapturing;
        }

        if (features.Resources)
        {
            flags |= EnableResourceTracking;
        }

        if (features.ShaderInfo)
        {
            flags |= GenerateShaderDebugInfo;
        }

        return flags;
    }

    /// <summary>
    /// At the first sign of a device removal (<paramref name="sign"/>), on the thread that saw it: logs the device and
    /// crash-dump status at once, the page fault the runtime knows of, then waits up to <see cref="DumpWait"/> for the
    /// driver's dump and our callbacks (GFSDK_Aftermath_GetCrashDumpStatus Finished or CollectingDataFailed) and logs
    /// the outcome. Once: a later caller (another sign, a fault in the process) waits for the first to finish.
    /// </summary>
    public void Report(string sign)
    {
        lock (_reportGate)
        {
            if (_reported)
            {
                return;
            }

            _reported = true;
            _log.Error($"[aftermath] at {sign}: device status {DeviceStatusText()}, crash dump status {StatusName(_library.CrashDumpStatus())}");
            PageFaultInformation();
            var started = DateTime.UtcNow;
            uint status = _library.CrashDumpStatus();

            while (status != Finished && status != CollectingDataFailed && DateTime.UtcNow - started < DumpWait)
            {
                Thread.Sleep(PollMilliseconds);
                status = _library.CrashDumpStatus();
            }

            string outcome = "the dump is written (above)";

            if (status == NotStarted)
            {
                outcome = "no GPU fault reported by the driver: it never started a dump";
            }
            else if (status != Finished)
            {
                outcome = "no dump";
            }

            _log.Error($"[aftermath] crash dump status {StatusName(status)} after {(DateTime.UtcNow - started).TotalSeconds:F1} s: {outcome}; device status {DeviceStatusText()}");
        }
    }

    /// <summary>
    /// A fault in the process (the game's int3 after it saw the loss, boot w182, ends it before the device-loss end):
    /// when the device is gone, holds the faulting thread for <see cref="Report"/>; otherwise returns at once.
    /// </summary>
    public void BeforeFaultEnds()
    {
        if (!DeviceLoss.Lost && DeviceStatus() == DeviceActive && !Removed())
        {
            return;
        }

        Report("a fault in the process after the device was lost");
    }

    /// <summary>Whether ID3D12Device::GetDeviceRemovedReason (desktop slot 37) reports the device removed.</summary>
    private bool Removed()
    {
        if (_device == 0)
        {
            return false;
        }

        return HResult.Failed(((delegate* unmanaged<nint, int>)ComObject.Slot(_device, RemovedReasonSlot))(_device));
    }

    /// <summary>GFSDK_Aftermath_GetDeviceStatus (GFSDK_Aftermath.h); <see cref="DeviceActive"/> when the DLL lacks it.</summary>
    private uint DeviceStatus()
    {
        uint status = DeviceActive;

        if (_deviceStatus != null)
        {
            _ = _deviceStatus(&status);
        }

        return status;
    }

    private string DeviceStatusText()
    {
        if (_deviceStatus == null)
        {
            return "unknown (no GFSDK_Aftermath_GetDeviceStatus)";
        }

        return AftermathDumpSummary.DeviceStatusName(DeviceStatus());
    }

    /// <summary>
    /// GFSDK_Aftermath_GetPageFaultInformation (deprecated, but answers without a dump): its result and the faulting
    /// GPU VA, the first field of GFSDK_Aftermath_PageFaultInformation.
    /// </summary>
    private void PageFaultInformation()
    {
        if (_pageFault == null)
        {
            return;
        }

        var information = stackalloc byte[PageFaultRoom];
        new Span<byte>(information, PageFaultRoom).Clear();
        uint result = _pageFault(information);
        _log.Error($"[aftermath] GetPageFaultInformation -> {AftermathLibrary.Describe(result)}: faulting GPU VA 0x{*(ulong*)information:x} (0 for none)");
    }

    private static string StatusName(uint status)
    {
        if (status < CrashDumpStatuses.Length)
        {
            return CrashDumpStatuses[status];
        }

        return status.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>With <c>--aftermath-markers</c>: an event marker naming the operation about to be recorded on <paramref name="list"/>.</summary>
    [HotPath]
    public static void Mark(XboxObject* list, MarkedKind kind, nint pipeline)
    {
        var dumps = Active;

        if (dumps is null || !dumps._features.Markers || !dumps.Initialized)
        {
            return;
        }

        dumps.SetMarker(list, kind, pipeline);
    }

    [HotPath]
    private void SetMarker(XboxObject* list, MarkedKind kind, nint pipeline)
    {
        var activity = ListActivity.Of(list);

        // The wrapper's desktop list changes (a split at a wait, a replaced dead list): a handle is per desktop list, and
        // one used on a list closed as a segment is an invalid call that removes the device (boot w176, 0x887a0001).
        if (activity->AftermathList != list->Inner)
        {
            activity->Aftermath = Context(list->Inner);
            activity->AftermathList = list->Inner;
        }

        if (activity->Aftermath == NoContext || _setMarker == null)
        {
            return;
        }

        if (!_markerTexts.TryGetValue((pipeline, kind), out nint text))
        {
            text = MarkerText(pipeline, kind);
        }

        _ = _setMarker(activity->Aftermath, (void*)text, *(uint*)(text - sizeof(int)));
    }

    /// <summary>
    /// The context handle of a desktop command list (recording now): the one made for it before, else
    /// GFSDK_Aftermath_DX12_CreateContextHandle; <see cref="NoContext"/> when it has none.
    /// </summary>
    private nint Context(nint inner)
    {
        if (_contexts.TryGetValue(inner, out nint known))
        {
            return known;
        }

        nint handle = 0;

        if (_createContext == null || !AftermathLibrary.Succeeded(_createContext(inner, &handle)) || handle == 0)
        {
            handle = NoContext;
        }

        return _contexts.GetOrAdd(inner, handle);
    }

    /// <summary>"draw 0x... name" as NUL-terminated UTF-8 after its byte count (kept for the run: the SDK copies it per marker).</summary>
    private nint MarkerText(nint pipeline, MarkedKind kind)
    {
        string name = QueueProgress.PipelineName(pipeline);
        string text = string.Create(CultureInfo.InvariantCulture, $"{kind} 0x{pipeline:x} {name}");

        if (text.Length > MaxMarkerChars)
        {
            text = text[..MaxMarkerChars];
        }

        byte[] bytes = Encoding.UTF8.GetBytes(text + "\0");
        var block = (byte*)NativeMemory.Alloc((nuint)(sizeof(int) + bytes.Length));
        *(int*)block = bytes.Length;
        bytes.CopyTo(new Span<byte>(block + sizeof(int), bytes.Length));
        nint data = (nint)(block + sizeof(int));

        return _markerTexts.GetOrAdd((pipeline, kind), data);
    }

    /// <summary>GFSDK_Aftermath_EnableGpuCrashDumps with our callbacks; logs the outcome.</summary>
    private bool Enable(string path)
    {
        delegate* unmanaged[Cdecl]<void*, uint, void*, void> dump = &OnCrashDump;
        delegate* unmanaged[Cdecl]<void*, uint, void*, void> debugInfo = &OnShaderDebugInfo;
        delegate* unmanaged[Cdecl]<nint, void*, void> description = &OnDescription;
        uint deferral = 0;

        // Deferred shader debug info is kept in memory until a crash: only with --aftermath-shader-info.
        if (_features.ShaderInfo)
        {
            deferral = DeferDebugInfoCallbacks;
        }

        uint result = _library.EnableGpuCrashDumps(WatchDx, deferral, (nint)dump, (nint)debugInfo, (nint)description);

        if (!AftermathLibrary.Succeeded(result))
        {
            string version = "no API version from 0x200 to 0x3ff was taken";

            if (result != AftermathLibrary.VersionMismatch)
            {
                version = $"API version 0x{_library.Version:x}";
            }

            _log.Warn($"[aftermath] {path}: EnableGpuCrashDumps -> {AftermathLibrary.Describe(result)} ({version}); no GPU crash dumps");

            return false;
        }

        Shaders = new AftermathShaderTable(_library.Export("GFSDK_Aftermath_GetShaderHash"), _library.Version);
        string hashes = "not available (no GFSDK_Aftermath_GetShaderHash)";

        if (Shaders.Enabled)
        {
            hashes = "noted per pipeline";
        }

        _log.Info($"  [aftermath] {path} (API version 0x{_library.Version:x}): GPU crash dumps on, to {_directory}; shader hashes {hashes}");

        return true;
    }

    /// <summary>The dump: written, the shader table beside it, and the summary logged.</summary>
    private void Dumped(ReadOnlySpan<byte> dump)
    {
        Directory.CreateDirectory(_directory);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string path = Path.Combine(_directory, $"crash-{stamp}.nv-gpudmp");
        File.WriteAllBytes(path, dump.ToArray());
        _log.Error($"[aftermath] GPU crash dump written: {path} ({dump.Length} bytes); open it in Nsight Graphics (File > Open), shader debug info in {_directory}");

        string table = Path.Combine(_directory, $"shaders-{stamp}.tsv");
        Shaders.Write(table, QueueProgress.PipelineName);
        _log.Info($"  [aftermath] {Shaders.Count} shader hashes and their pipelines: {table}");
        AftermathDumpSummary.Write(_library, dump, Shaders, _log);
    }

    /// <summary>A shader's debug info, named by its GFSDK_Aftermath_ShaderDebugInfoIdentifier as Nsight Graphics looks it up.</summary>
    private void DebugInfo(ReadOnlySpan<byte> info)
    {
        var identifier = stackalloc ulong[IdentifierWords];
        string name = string.Create(CultureInfo.InvariantCulture, $"unidentified-{Interlocked.Increment(ref _unnamedShaderFiles)}");

        fixed (byte* bytes = info)
        {
            if (_debugInfoIdentifier != null && AftermathLibrary.Succeeded(_debugInfoIdentifier(_library.Version, bytes, (uint)info.Length, identifier)))
            {
                name = string.Create(CultureInfo.InvariantCulture, $"{identifier[0]:X16}-{identifier[1]:X16}");
            }
        }

        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Combine(_directory, $"shader-{name}.nvdbg"), info.ToArray());
    }

    /// <summary>PFN_GFSDK_Aftermath_GpuCrashDumpCb(const void* dump, uint32 size, void* userData).</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCrashDump(void* dump, uint size, void* userData)
    {
        try
        {
            Active?.Dumped(new ReadOnlySpan<byte>(dump, (int)size));
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "AftermathCrashDumps.OnCrashDump");
        }
    }

    /// <summary>PFN_GFSDK_Aftermath_ShaderDebugInfoCb(const void* info, uint32 size, void* userData).</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnShaderDebugInfo(void* info, uint size, void* userData)
    {
        try
        {
            Active?.DebugInfo(new ReadOnlySpan<byte>(info, (int)size));
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "AftermathCrashDumps.OnShaderDebugInfo");
        }
    }

    /// <summary>PFN_GFSDK_Aftermath_GpuCrashDumpDescriptionCb(PFN_GFSDK_Aftermath_AddGpuCrashDumpDescription addValue, void* userData).</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDescription(nint addValue, void* userData)
    {
        try
        {
            fixed (byte* name = ApplicationName)
            {
                ((delegate* unmanaged[Cdecl]<uint, byte*, void>)addValue)(ApplicationNameKey, name);
            }
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "AftermathCrashDumps.OnDescription");
        }
    }
}
