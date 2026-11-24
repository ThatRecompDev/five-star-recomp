// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Formats.Platform;
using FiveStar.Loader.Calls;
using FiveStar.Loader.Cpu;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Loading;
using FiveStar.Loader.Mods;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using FiveStar.Loader.Shims.D3D12X.Present;
using FiveStar.Loader.Shims.Input;

namespace FiveStar.Loader.Host;

/// <summary>Runs one fivestar-loader invocation: load, report, call exports, unload.</summary>
internal sealed class LoaderHost
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int UsageError = 2;

    private readonly HostOptions _options;
    private readonly LoaderRuntime _runtime;
    private readonly LoaderLog _log;
    private readonly int _processId;

    public LoaderHost(HostOptions options, LoaderRuntime runtime, LoaderLog log, int processId)
    {
        _options = options;
        _runtime = runtime;
        _log = log;
        _processId = processId;
    }

    public int Run()
    {
        foreach (string spec in _options.DisabledShims)
        {
            if (!_runtime.Shims.Disable(spec))
            {
                _log.Error($"no such shim {spec}");

                return UsageError;
            }
        }

        string manual = string.Join(", ", _runtime.Loader.ManualModules.Order(StringComparer.Ordinal));
        string listed = manual.Length == 0 ? "(none)" : manual;
        _log.Info($"fivestar-loader pid {_processId}; manual modules: {listed}; unresolved={_options.Unresolved}");

        LoadAbiTable();

        if (_options.RunMain)
        {
            // Before loading: TLS callbacks and DllMain already run mapped code.
            _runtime.Trace.Enable(_options.TraceSystemCalls);
            _runtime.Trace.TracedModules.UnionWith(_options.TracedModules);
            _runtime.Faults.Install();
        }

        using var timers = Timers();
        CheckCpu();

        if (!ConfigureMods())
        {
            return UsageError;
        }

        var (targets, rc) = LoadTargets();
        LoadReport.Print(_runtime, targets, _options.ListExports);
        ReportMainImage();

        rc = CallExports(rc);
        rc = RunMain(rc);

        if (rc == ExecutableRunner.TimedOut)
        {
            EndTimedOutRun();
        }

        Finish();
        _log.Info($"done, exit code {rc}");

        return rc;
    }

    /// <summary>
    /// Logs which AMD-only instructions the host lacks. With --cpu intel, also checks their emulation through the
    /// exception handler (installed for it when --run did not).
    /// </summary>
    private void CheckCpu()
    {
        var features = CpuFeatures.None;

        if (!_options.AssumeIntelCpu)
        {
            features = CpuFeatures.Detect();
        }

        _runtime.Cpu.ReportHost(features, _options.AssumeIntelCpu);

        if (!_options.AssumeIntelCpu)
        {
            return;
        }

        _runtime.Faults.Install();
        var result = new AmdInstructionSelfTest(_runtime.CodeHeap, _runtime.Cpu).Run();

        foreach (string failure in result.Failures)
        {
            _log.Error($"cpu self-test: {failure}");
        }

        _log.Info($"cpu self-test: {result.Cases - result.Failures.Count} of {result.Cases} cases agree with the CPU and the reference");
    }

    /// <summary>The input script, its learned screens and the present signatures its screen blocks wait for.</summary>
    private void UseInputScript(string path, FrameCapture? capture)
    {
        string screensFile = _options.ScreensFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FiveStar", "screens.txt");
        var screens = new ScreenStore(screensFile);
        var sampler = new ScreenSampler(signature => _runtime.Input.Seen(signature), _log);
        _runtime.D3D12X.Presenter.Screens = sampler;
        var script = _runtime.Input.UseScript(path, System.Diagnostics.Stopwatch.StartNew(), () => capture?.RequestNext(), sampler.Request, screens);
        _log.Info($"  [input] {script.Presses.Count} scripted presses, {script.Marks.Count} captures and learns, {script.Screens.Count - 1} screens from {path} (followed while running); {screens.Count} learned screens in {screensFile}");
    }

    private void LoadAbiTable()
    {
        if (_options.GdkAbiTable is not null)
        {
            _runtime.Gdk.Runtime.LoadAbiTable(_options.GdkAbiTable);
        }
        else
        {
            LoadInstalledAbi();
        }

        if (_options.InputConfig is not null)
        {
            _runtime.Input.Configure(InputConfigFile.Load(_options.InputConfig));
            _log.Info($"  [input] mapping from {_options.InputConfig}");
        }

        FrameCapture? capture = null;

        if (_options.CaptureDirectory is not null || _options.D3D12TraceDraws)
        {
            capture = new FrameCapture(_options.CaptureDirectory, _options.CapturePresents, _log);
            _runtime.D3D12X.EnableCapture(capture, _options.D3D12TraceDraws);
        }

        if (_options.InputScript is not null)
        {
            UseInputScript(_options.InputScript, capture);
        }

        if (_options.D3D12Debug)
        {
            _runtime.D3D12X.Debugging.Enable(_options.D3D12GpuValidation);
        }

        if (_options.D3D12TraceQueues)
        {
            QueueTrace.Enable(_runtime.D3D12X.Objects, _log);
        }

        foreach (string skips in _options.D3D12Skips)
        {
            foreach (var skipped in SkippedSlot.ParseList(skips))
            {
                _runtime.D3D12X.Objects.Skip(skipped);
            }
        }

        _runtime.D3D12X.Presenter.ShowTestPattern = _options.PresentTest;
        _runtime.BvhBuilder.Enabled = _options.RayTracing;
        _runtime.XDsp.Convolve = _options.XDspConvolution;
        _runtime.Storage.BcPack.FailMissing = _options.FailMissingBcPack;
        _runtime.Storage.BcPack.LoadTables(_options.BcPackTables);

        if (_options.FidelityFxLibrary is not null)
        {
            _runtime.FidelityFx.Load(_options.FidelityFxLibrary);
        }
    }

    /// <summary>Without <c>--gdk-abi</c>, the thunk ABI of the installed PC GDK's xgameruntime.lib.</summary>
    private void LoadInstalledAbi()
    {
        string? library = GdkLibraryLocator.FindInstalled(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

        if (library is null)
        {
            _log.Warn("no --gdk-abi table and no installed PC GDK: every GDK thunk call returns REGDB_E_CLASSNOTREG");

            return;
        }

        try
        {
            _runtime.Gdk.Runtime.LoadAbiLibrary(GdkThunkLibrary.Read(File.ReadAllBytes(library)), library);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _log.Warn($"GDK library {library}: {e.Message}");
        }
    }

    /// <summary>Reads the patch files and adds the mod folders; false after a reported error.</summary>
    private bool ConfigureMods()
    {
        try
        {
            var patches = PatchFileReader.ReadPaths(_options.Patches);
            _runtime.Patcher.Add(patches);

            if (patches.Count > 0)
            {
                _log.Info($"patches: {patches.Count} from {string.Join(", ", _options.Patches)}");
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _log.Error($"patches: {e.Message}");

            return false;
        }

        foreach (string folder in _options.ModFolders)
        {
            if (!Directory.Exists(folder))
            {
                _log.Error($"mod folder {folder} does not exist");

                return false;
            }

            _runtime.Overlay.AddFolder(folder);
            _log.Info($"mod folder {_runtime.Overlay.Folders.Count}: {folder}");
        }

        return true;
    }

    private (List<LoadedImage> Targets, int Rc) LoadTargets()
    {
        var targets = new List<LoadedImage>();
        int rc = Success;

        foreach (string path in _options.Loads)
        {
            try
            {
                targets.Add(_runtime.Loader.Load(path, true));
            }
            catch (Exception e)
            {
                _log.Error($"load {path} failed: {e.GetType().Name}: {e.Message}");
                rc = Failure;
            }
        }

        return (targets, rc);
    }

    private void ReportMainImage()
    {
        var main = _runtime.Loader.MainImage;

        if (main is null)
        {
            return;
        }

        _log.Info($"== process identity: main image {main.Name} @ 0x{main.Base:x} ({main.Path})");

        if (_options.PatchPeb)
        {
            _runtime.Identity.PatchPeb(main.Base);
        }
    }

    private int CallExports(int rc)
    {
        var caller = new ExportCaller(_runtime, _log);
        int result = rc;

        foreach (string request in _options.Calls)
        {
            var outcomes = caller.Run(request);

            if (outcomes is null || outcomes.Any(o => o.Result is null))
            {
                result = Failure;
            }
        }

        return result;
    }

    private int RunMain(int rc)
    {
        var main = _runtime.Loader.MainImage;

        if (!_options.RunMain || rc != Success)
        {
            return rc;
        }

        if (main is null || main.Pe.IsDll)
        {
            _log.Error("--run needs an executable as the main image");

            return Failure;
        }

        var interval = _options.SampleThreads;

        if (interval.HasValue)
        {
            new ThreadSampler(_runtime.Images, _runtime.Trace, SampledCounters, _options.ProfileThreads, _log).Start(interval.Value);
        }

        StartMemoryWatch();

        GpuBreadcrumbs.Enable(_options.D3D12Breadcrumbs);
        Shims.D3D12X.Binding.CommandListBindingMethods.SkipComputeChains = _options.SkipComputeChains;
        Shims.D3D12X.Binding.CommandListBindingMethods.EmptyIndirectAfterSkip = _options.EmptyIndirectAfterSkip;
        Shims.D3D12X.RetireWaits.Enable(_options.RetireWaitsAllQueues, _log);
        Shims.D3D12X.ListSplits.Enabled = _options.SplitAtWaits;

        if (_options.WatchPoolPages)
        {
            Shims.D3D12X.DebugLayer.PoolPageWatch.Start(_log);
        }

        if (_options.WatchTransient)
        {
            TransientPoolWatch.Start(_log);
        }

        if (_options.WatchHang)
        {
            GameHangWatch.Start(_runtime.Images, SampledCounters, _runtime.D3D12X.Fences.DescribeCell, _log);
        }

        StartAudioCapture();
        _runtime.D3D12X.WaitForProgramIndex();

        return new ExecutableRunner(_runtime.Faults, _log).Run(main, _options.RunTimeout);
    }

    /// <summary>The memory map (<c>--memory-map</c>) and the memory limits (<c>--min-free-memory</c>, <c>--max-commit</c>).</summary>
    private void StartMemoryWatch()
    {
        var map = new MemoryMap(_runtime.Images, _runtime.XMem, _runtime.XMemPhysical, _log);

        if (_options.MemoryMap.HasValue)
        {
            map.Start(_options.MemoryMap.Value);
        }

        if (_options.MinimumFreeMemory != 0 || _options.MaximumCommit != 0)
        {
            new MemoryLimit(_options.MinimumFreeMemory, _options.MaximumCommit, map, _log).Start();
        }
    }

    /// <summary>With --run, per-monitor DPI awareness and the console's millisecond timers for the whole process, unless <c>--desktop-timers</c>.</summary>
    private ConsoleTimers? Timers()
    {
        if (!_options.RunMain)
        {
            return null;
        }

        DpiAwareness.Take(_log);

        if (_options.DesktopTimers)
        {
            return ConsoleTimers.Desktop(_log);
        }

        return ConsoleTimers.Take(_log);
    }

    /// <summary><c>--audio-capture</c>: starts the recorder, a child copy of the loader that records this process tree.</summary>
    private void StartAudioCapture()
    {
        string? path = _options.AudioCapture;
        string? loader = Environment.ProcessPath;

        if (path is null || loader is null)
        {
            return;
        }

        var start = new System.Diagnostics.ProcessStartInfo(loader) { UseShellExecute = false };
        start.ArgumentList.Add("--record-audio");
        start.ArgumentList.Add(_processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(path);
        using var recorder = System.Diagnostics.Process.Start(start);
        _log.Info($"  [audio-capture] recorder pid {recorder?.Id} writes {path}");
    }

    /// <summary>The loader counters each thread sample logs.</summary>
    private string SampledCounters()
    {
        string pipelines = _runtime.D3D12X.Pipelines?.Summary ?? "pipeline slots off";
        string memory = ProcessMemory.Summary(_runtime.XMem.LiveBytes, _runtime.XMemPhysical.LivePages * (long)XMemPhysicalMemory.PageSize);

        string audio = $"audio: {_runtime.Audio.Summary}; XDsp {_runtime.XDsp.Submitted} commands";

        return $"DirectStorage {_runtime.Storage.Summary()}; {pipelines}; {_runtime.D3D12X.Presenter.Presents} presents, {_runtime.D3D12X.FrameThrottle.Summary}; {_runtime.Input.Summary}; {audio}; {memory}; {MemorySnapshot.Summary()}";
    }

    /// <summary>
    /// After <c>--run-timeout</c> the game's threads still run and use the shims (the render thread presents on the
    /// swap chain), so disposing the runtime under them crashes. The process ends here instead, without cleanup.
    /// </summary>
    private void EndTimedOutRun()
    {
        _runtime.D3D12X.Presenter.LogSummary();
        _runtime.Cpu.LogSummary();
        SavePipelineCache();
        _log.Info($"done, exit code {ExecutableRunner.TimedOut} (the game's threads are still running: the process ends without cleanup)");
        Console.Out.Flush();
        _ = Kernel32.TerminateProcess(Kernel32.GetCurrentProcess(), (uint)ExecutableRunner.TimedOut);
    }

    /// <summary>Writes the pipelines built so far to the disk cache, as the process ends without cleanup.</summary>
    private void SavePipelineCache()
    {
        var cache = _runtime.D3D12X.Pipelines?.Cache;

        if (cache is null)
        {
            return;
        }

        cache.Save();
        _log.Info($"  [d3d12_xs] {cache.Summary}");
    }

    private void Finish()
    {
        _runtime.Cpu.LogSummary();
        var stubs = _runtime.Stubs;

        if (stubs.CallCount > 0)
        {
            _log.Warn($"{stubs.CallCount} call(s) hit unresolved-import stubs");
        }

        if (stubs.NotImplementedCallCount > 0)
        {
            _log.Info($"{stubs.NotImplementedCallCount} call(s) hit not-implemented GDK stubs");
        }

        _runtime.Identity.RestorePeb();

        if (_options.Unload)
        {
            _runtime.Loader.UnloadAll();
        }
    }
}
