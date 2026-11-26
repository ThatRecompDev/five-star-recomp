// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using FiveStar.Loader.Diagnostics;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--aftermath [dir]</c>: NVIDIA Nsight Aftermath GPU crash dumps (docs/debugging.md#aftermath). Before the desktop
/// device exists, GPU crash dumps are enabled with deferred shader debug info; after, the device is initialized with
/// markers, resource tracking and shader debug info. At a GPU fault the driver hands over <c>crash-*.nv-gpudmp</c> and
/// the <c>shader-*.nvdbg</c> files of the shaders it ran, which go to the folder with a table of our shader hashes, and
/// the log gets a summary (<see cref="AftermathDumpSummary"/>). The device-loss end waits for that
/// (<see cref="WaitForDump"/>). With <c>--aftermath-markers</c> every draw, dispatch, ExecuteIndirect, copy and
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
    private const uint CollectingDataFailed = 2;
    private const uint Finished = 4;

    /// <summary>GFSDK_Aftermath_GpuCrashDumpDescriptionKey_ApplicationName.</summary>
    private const uint ApplicationNameKey = 0x1;

    /// <summary>How long the device-loss end waits for the dump, and how often it asks.</summary>
    public static readonly TimeSpan DumpWait = TimeSpan.FromSeconds(10);
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
    private readonly bool _markers;
    private readonly LoaderLog _log;
    private readonly ConcurrentDictionary<(nint Pipeline, MarkedKind Kind), nint> _markerTexts = new();
    private readonly delegate* unmanaged[Cdecl]<nint, nint*, uint> _createContext;
    private readonly delegate* unmanaged[Cdecl]<nint, void*, uint, uint> _setMarker;
    private readonly delegate* unmanaged[Cdecl]<uint, void*, uint, ulong*, uint> _debugInfoIdentifier;
    private int _unnamedShaderFiles;

    public AftermathCrashDumps(AftermathLibrary library, string directory, bool markers, LoaderLog log)
    {
        _library = library;
        _directory = directory;
        _markers = markers;
        _log = log;

        _createContext = (delegate* unmanaged[Cdecl]<nint, nint*, uint>)library.Export("GFSDK_Aftermath_DX12_CreateContextHandle");
        _setMarker = (delegate* unmanaged[Cdecl]<nint, void*, uint, uint>)library.Export("GFSDK_Aftermath_SetEventMarker");
        _debugInfoIdentifier = (delegate* unmanaged[Cdecl]<uint, void*, uint, ulong*, uint>)library.Export("GFSDK_Aftermath_GetShaderDebugInfoIdentifier");
        Shaders = new AftermathShaderTable(0, 0);
    }

    /// <summary>The crash dumps of this run; null without <c>--aftermath</c> or without the DLL.</summary>
    public static AftermathCrashDumps? Active { get; private set; }

    /// <summary>Our shaders by Aftermath hash, noted once the API version is known.</summary>
    public AftermathShaderTable Shaders { get; private set; }

    /// <summary>Whether the device was initialized for Aftermath (markers need it).</summary>
    public bool Initialized { get; private set; }

    /// <summary>
    /// Finds and loads the DLL (<see cref="AftermathLibrary.Find"/>) and enables GPU crash dumps; must run before
    /// D3D12CreateDevice. Logs one line and returns null when the DLL is missing or refuses.
    /// </summary>
    public static AftermathCrashDumps? Start(string directory, string? dllPath, bool markers, string loaderFolder, string? sdkRoot, LoaderLog log)
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

        var dumps = new AftermathCrashDumps(library, directory, markers, log);

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
        uint flags = EnableMarkers | EnableResourceTracking | GenerateShaderDebugInfo;

        if (_markers)
        {
            flags |= CallStackCapturing;
        }

        uint result = _library.InitializeDevice(flags, device);
        Initialized = AftermathLibrary.Succeeded(result);

        if (!Initialized)
        {
            _log.Warn($"[aftermath] DX12_Initialize -> {AftermathLibrary.Describe(result)}; crash dumps still come, without markers, resource names or shader debug info");

            return;
        }

        string markers = "off (--aftermath-markers)";

        if (_markers)
        {
            markers = "on, with call stacks";
        }

        _log.Info($"  [aftermath] device initialized (flags 0x{flags:x}); event markers {markers}");
    }

    /// <summary>
    /// After a device removal, before the process ends: waits up to <see cref="DumpWait"/> for the driver's dump and
    /// our callbacks to finish (GFSDK_Aftermath_GetCrashDumpStatus Finished or CollectingDataFailed).
    /// </summary>
    public void WaitForDump()
    {
        var started = DateTime.UtcNow;
        uint status = _library.CrashDumpStatus();

        while (status != Finished && status != CollectingDataFailed && DateTime.UtcNow - started < DumpWait)
        {
            Thread.Sleep(PollMilliseconds);
            status = _library.CrashDumpStatus();
        }

        _log.Info($"  [aftermath] crash dump status {status} after {(DateTime.UtcNow - started).TotalSeconds:F1} s (4 finished, 2 failed, 0 never started: no GPU fault the driver saw)");
    }

    /// <summary>With <c>--aftermath-markers</c>: an event marker naming the operation about to be recorded on <paramref name="list"/>.</summary>
    [HotPath]
    public static void Mark(XboxObject* list, MarkedKind kind, nint pipeline)
    {
        var dumps = Active;

        if (dumps is null || !dumps._markers || !dumps.Initialized)
        {
            return;
        }

        dumps.SetMarker(list, kind, pipeline);
    }

    [HotPath]
    private void SetMarker(XboxObject* list, MarkedKind kind, nint pipeline)
    {
        var activity = ListActivity.Of(list);

        if (activity->Aftermath == 0)
        {
            activity->Aftermath = OpenContext(list->Inner);
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

    /// <summary>GFSDK_Aftermath_DX12_CreateContextHandle for a desktop command list; <see cref="NoContext"/> when it has none.</summary>
    private nint OpenContext(nint inner)
    {
        nint handle = 0;

        if (_createContext == null || !AftermathLibrary.Succeeded(_createContext(inner, &handle)) || handle == 0)
        {
            return NoContext;
        }

        return handle;
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
        uint result = _library.EnableGpuCrashDumps(WatchDx, DeferDebugInfoCallbacks, (nint)dump, (nint)debugInfo, (nint)description);

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
