// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// <c>--pix-capture</c>: WinPixGpuCapturer.dll, the newest under <c>%ProgramFiles%\Microsoft PIX\*\</c> (as
/// PIXLoadLatestWinPixGpuCapturerLibrary picks it), loaded before the desktop device exists; capture N begins after
/// present N-1 (after device creation for N = 1) and ends after present N, so <c>present-N.wpix</c> holds the frame
/// <c>present-N.png</c> reads back. Begin and end are the capturer's PIXBeginCapture2 and PIXEndCapture exports (pix3.h).
/// </summary>
internal sealed unsafe class PixGpuCapture
{
    private const string Capturer = "WinPixGpuCapturer.dll";

    /// <summary>The PIX event runtimes that export the programmatic capture calls, in the capturer's folder.</summary>
    private static readonly string[] EventRuntimes = ["WinPixEventRuntime.dll", "WinPixEventRuntime_OneCore.dll"];

    /// <summary>PIX_CAPTURE_GPU (pix3.h: 1 &lt;&lt; 1; 1 &lt;&lt; 3 is a CPU capture kind, refused with E_NOTIMPL in w168).</summary>
    private const uint CaptureGpu = 1 << 1;

    /// <summary>PIXCaptureParameters is a union; its GPU member is one PCWSTR FileName at offset 0. Zeroed beyond.</summary>
    private const int ParameterBytes = 64;

    private readonly HashSet<long> _presents;
    private readonly string _directory;
    private readonly LoaderLog _log;
    private delegate* unmanaged[Stdcall]<uint, void*, int> _begin;
    private delegate* unmanaged[Stdcall]<int, int> _end;
    private long _open;
    private nint _fileName;
    private void* _parameters;

    public PixGpuCapture(IEnumerable<long> presents, string directory, LoaderLog log)
    {
        _presents = [.. presents];
        _directory = directory;
        _log = log;
    }

    /// <summary>Whether the capturer is loaded and has both exports.</summary>
    public bool Loaded => _begin != null && _end != null;

    /// <summary>The newest WinPixGpuCapturer.dll under <paramref name="programFiles"/>\Microsoft PIX; null when none.</summary>
    public static string? FindCapturer(string programFiles)
    {
        string root = Path.Combine(programFiles, "Microsoft PIX");

        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory.GetDirectories(root)
            .Select(folder => Path.Combine(folder, Capturer))
            .Where(File.Exists)
            .OrderBy(path => Path.GetFileName(Path.GetDirectoryName(path)), StringComparer.OrdinalIgnoreCase)
            .LastOrDefault();
    }

    /// <summary>Loads the capturer; must run before D3D12CreateDevice. Logs one line when PIX is not installed.</summary>
    public void Load()
    {
        string? path = FindCapturer(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

        if (path is null)
        {
            _log.Warn($"[pix] {Capturer} not found under %ProgramFiles%\\Microsoft PIX: install PIX (winget install Microsoft.PIX); no PIX captures");

            return;
        }

        // The capturer only has to be loaded; PIXBeginCapture2 and PIXEndCapture are the event runtime's (issue #65:
        // PIX 2611.19 has them in WinPixEventRuntime_OneCore.dll of the same folder, not in the capturer).
        if (!NativeLibrary.TryLoad(path, out nint capturer) || !TryCaptureExports(capturer, Path.GetDirectoryName(path)!, out nint begin, out nint end))
        {
            _log.Warn($"[pix] {path} loaded without PIXBeginCapture2 and PIXEndCapture in it or in the event runtime beside it; no PIX captures");

            return;
        }

        _begin = (delegate* unmanaged[Stdcall]<uint, void*, int>)begin;
        _end = (delegate* unmanaged[Stdcall]<int, int>)end;
        Pipelines.PipelineLibraryCache.ReadOnly = true;
        _log.Info($"  [pix] {path} loaded; GPU captures of presents {string.Join(',', _presents.Order())} to {_directory}; the pipeline cache is only read meanwhile");
    }

    /// <summary>PIXBeginCapture2 and PIXEndCapture from the capturer, else from the event runtime in <paramref name="folder"/>.</summary>
    private static bool TryCaptureExports(nint capturer, string folder, out nint begin, out nint end)
    {
        end = 0;

        if (NativeLibrary.TryGetExport(capturer, "PIXBeginCapture2", out begin) && NativeLibrary.TryGetExport(capturer, "PIXEndCapture", out end))
        {
            return true;
        }

        foreach (string runtime in EventRuntimes)
        {
            string file = Path.Combine(folder, runtime);

            if (File.Exists(file) && NativeLibrary.TryLoad(file, out nint module)
                && NativeLibrary.TryGetExport(module, "PIXBeginCapture2", out begin) && NativeLibrary.TryGetExport(module, "PIXEndCapture", out end))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>After present <paramref name="presents"/> (0: the device was just created): ends and begins captures.</summary>
    public void AfterPresent(long presents)
    {
        if (!Loaded)
        {
            return;
        }

        if (_open != 0 && presents == _open)
        {
            End();
        }

        if (_presents.Contains(presents + 1))
        {
            Begin(presents + 1);
        }
    }

    private void Begin(long present)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, $"present-{present}.wpix");
        _fileName = Marshal.StringToHGlobalUni(path);
        _parameters = NativeMemory.AllocZeroed(ParameterBytes);
        *(nint*)_parameters = _fileName;
        int hr = _begin(CaptureGpu, _parameters);

        if (HResult.Failed(hr))
        {
            _log.Warn($"[pix] PIXBeginCapture2({path}) failed: {HResult.Format(hr)}");
            Release();

            return;
        }

        _open = present;
        _log.Info($"  [pix] capturing present {present} to {path}");
    }

    private void End()
    {
        int hr = _end(0);
        string path = Marshal.PtrToStringUni(_fileName) ?? string.Empty;
        _log.Info($"  [pix] PIXEndCapture -> {HResult.Format(hr)}; capture of present {_open} in {path}");
        _open = 0;
        Release();
    }

    private void Release()
    {
        Marshal.FreeHGlobal(_fileName);
        NativeMemory.Free(_parameters);
        _fileName = 0;
        _parameters = null;
    }
}
