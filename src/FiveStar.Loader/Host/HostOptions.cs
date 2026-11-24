// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Loading;
using FiveStar.Loader.Shims.Network;

namespace FiveStar.Loader.Host;

/// <summary>The parsed command line of fivestar-loader.</summary>
internal sealed record HostOptions
{
    public List<string> Loads { get; } = [];

    public List<string> Calls { get; } = [];

    public List<string> ManualModules { get; } = [];

    public List<string> SearchDirectories { get; } = [];

    public List<string> DisabledShims { get; } = [];

    /// <summary>Patch files and folders of patch files, in order.</summary>
    public List<string> Patches { get; } = [];

    /// <summary>Mod folders, highest priority first.</summary>
    public List<string> ModFolders { get; } = [];

    public bool SystemCrt { get; set; }

    public UnresolvedMode Unresolved { get; set; } = UnresolvedMode.Stub;

    public bool CallEntryPoints { get; set; } = true;

    public bool ListExports { get; set; } = true;

    public bool Unload { get; set; }

    public string? WorkDirectory { get; set; }

    public string? LogFile { get; set; }

    public string? MainModule { get; set; }

    public bool PatchPeb { get; set; }

    public string? PackageRoot { get; set; }

    public string? SaveDirectory { get; set; }

    public string? GdkAbiTable { get; set; }

    public uint? DeviceType { get; set; }

    public string? InputConfig { get; set; }

    /// <summary>Timed key presses added to the desktop input (<c>--input-script</c>).</summary>
    public string? InputScript { get; set; }

    /// <summary>The learned screens of the input script (<c>--screens</c>); null: %LOCALAPPDATA%\FiveStar\screens.txt.</summary>
    public string? ScreensFile { get; set; }

    /// <summary>Extracted FSSM programs (`fivestar shader extract`) for pipeline creation.</summary>
    public string? ShaderDirectory { get; set; }

    /// <summary>Translated DXIL programs (`fivestar shader translate`) for pipeline creation.</summary>
    public string? TranslatedDirectory { get; set; }

    /// <summary>The pipeline disk cache folder (<c>--pipeline-cache</c>); null means the work folder's.</summary>
    public string? PipelineCache { get; set; }

    /// <summary><c>--no-pipeline-cache</c>: pipelines are neither loaded from nor stored in a disk cache.</summary>
    public bool NoPipelineCache { get; set; }

    /// <summary>The console pipeline cache (shaders.rpf or the blob) whose pipelines <c>--warm-pipelines</c> builds.</summary>
    public string? WarmSource { get; set; }

    /// <summary>Pipelines the warm-up builds at once; null for the default (4).</summary>
    public int? WarmJobs { get; set; }

    /// <summary>The warm-up builds every n-th pipeline only (a sample).</summary>
    public int WarmStep { get; set; } = 1;

    /// <summary>The warm-up builds only pipelines with a program whose name contains one of these.</summary>
    public List<string> WarmMatches { get; } = [];

    /// <summary>The warm-up builds only the pipelines earlier boots asked for (<c>pipelines-used.bin</c>).</summary>
    public bool WarmUsedOnly { get; set; }

    /// <summary>Every pipeline the cache lacks is built at creation, not on first use.</summary>
    public bool EagerPipelines { get; set; }

    /// <summary>The program cache folder of programs made on demand (<c>--program-cache</c>); null means the work folder's.</summary>
    public string? ProgramCache { get; set; }

    /// <summary><c>--no-on-demand</c>: only the ahead-of-time translation provides programs.</summary>
    public bool NoOnDemand { get; set; }

    /// <summary>dxc.exe, dxcompiler.dll or their folder (<c>--dxc</c>); null finds one.</summary>
    public string? Dxc { get; set; }

    /// <summary><c>--shader-backend dxc</c>: programs made on demand compile with dxc instead of the DXIL backend.</summary>
    public bool DxcBackend { get; set; }

    /// <summary>Threads that make programs on demand (<c>--shader-jobs</c>); null for the default.</summary>
    public int? ShaderJobs { get; set; }

    /// <summary>The PC FidelityFX API DLL the console FidelityFX calls are forwarded to; null leaves FidelityFX off.</summary>
    public string? FidelityFxLibrary { get; set; }

    /// <summary>Run the main executable's entry point after loading, with the call trace and fault reporter on.</summary>
    public bool RunMain { get; set; }

    /// <summary>How long --run lets the entry point run before reporting the main thread and exiting; null waits.</summary>
    public TimeSpan? RunTimeout { get; set; }

    /// <summary>
    /// <c>--cpu intel</c>: report the AMD-only instructions as missing and run the emulation self-test through the
    /// exception handler, as on an Intel CPU; the game's own instructions still run natively where the CPU has them.
    /// </summary>
    public bool AssumeIntelCpu { get; set; }

    /// <summary>With --run, log call counts and every thread's stack at this interval (<c>--sample-threads</c>).</summary>
    public TimeSpan? SampleThreads { get; set; }

    /// <summary>With --run, log where the private memory is at this interval (<c>--memory-map</c>).</summary>
    public TimeSpan? MemoryMap { get; set; }

    /// <summary>With --run, end the process below this many free physical bytes (<c>--min-free-memory</c>); 0 for none.</summary>
    public ulong MinimumFreeMemory { get; set; }

    /// <summary>With --run, end the process above this system commit (<c>--max-commit</c>); 0 for none.</summary>
    public ulong MaximumCommit { get; set; }

    /// <summary>With --sample-threads, also a profile burst of the running threads per sample (<c>--profile-threads</c>).</summary>
    public bool ProfileThreads { get; set; }

    /// <summary>With --run, also trace calls into these mapped images (<c>--trace-module</c>).</summary>
    public List<string> TracedModules { get; } = [];

    /// <summary>With --run, also trace calls to System32 imports.</summary>
    public bool TraceSystemCalls { get; set; }

    /// <summary>The D3D12 debug layer, its messages and DRED in the loader log (<c>--d3d12-debug</c>).</summary>
    public bool D3D12Debug { get; set; }

    /// <summary>With the debug layer, also GPU-based validation (<c>--d3d12-gpu-validation</c>).</summary>
    public bool D3D12GpuValidation { get; set; }

    /// <summary>D3D12.X slots that do nothing, to bisect GPU faults (<c>--d3d12-skip list:51,queue:35</c>).</summary>
    public List<string> D3D12Skips { get; } = [];

    /// <summary>Queue submissions, signals and waits in the log (<c>--d3d12-trace-queues</c>).</summary>
    public bool D3D12TraceQueues { get; set; }

    /// <summary>A GPU marker after every draw and dispatch, for the stall report (<c>--d3d12-breadcrumbs</c>).</summary>
    public bool D3D12Breadcrumbs { get; set; }

    /// <summary>Check the game's transient-memory pool for corrupted nodes (<c>--watch-transient</c>).</summary>
    public bool WatchTransient { get; set; }

    /// <summary>Check the game's small-block pool pages for headers the CPU did not write (<c>--watch-pool-pages</c>).</summary>
    public bool WatchPoolPages { get; set; }

    /// <summary>Follow the game's hang watchdog, main state and loading-screen flags (<c>--watch-hang</c>).</summary>
    public bool WatchHang { get; set; }

    /// <summary>Skip the rest of a recording's compute after a skipped dispatch (<c>--skip-compute-chains</c>).</summary>
    public bool SkipComputeChains { get; set; }

    /// <summary>A recording's ExecuteIndirects after a skipped compute dispatch run nothing (<c>--empty-indirect-after-skip</c>).</summary>
    public bool EmptyIndirectAfterSkip { get; set; }

    /// <summary>A submission holding a retire write waits for every other queue (<c>--retire-waits-all-queues</c>).</summary>
    public bool RetireWaitsAllQueues { get; set; }

    /// <summary>A memory wait recorded after work splits its list there; <c>--no-split-at-waits</c> turns it off.</summary>
    public bool SplitAtWaits { get; set; } = true;

    /// <summary>Programs made on demand trace stores into the transient pool and the game heap (<c>--trace-pool-stores</c>).</summary>
    public bool TracePoolStores { get; set; }

    /// <summary>Presents read back when --capture-presents is not given: early, after the first seconds, later.</summary>
    private static readonly long[] DefaultCapturePresents = [60, 300, 900, 1500];

    /// <summary>What draws bind and what their targets hold, in the log (<c>--d3d12-trace-draws</c>).</summary>
    public bool D3D12TraceDraws { get; set; }

    /// <summary>Where captured presents go as PNG files (<c>--capture-dir</c>); null captures nothing to disk.</summary>
    public string? CaptureDirectory { get; set; }

    /// <summary>The present numbers whose plane is read back (<c>--capture-presents 60,600</c>).</summary>
    public List<long> CapturePresents { get; } = [.. DefaultCapturePresents];

    /// <summary>Present a test pattern instead of the game's frame (<c>--present-test</c>).</summary>
    public bool PresentTest { get; set; }

    /// <summary>XboxBvhBuilder.dll's CPU paths: console sizes and BLAS rehydration (<c>--raytracing</c>).</summary>
    public bool RayTracing { get; set; }

    /// <summary>Run the XDsp convolution effects on the CPU instead of returning silence (<c>--xdsp-convolution</c>).</summary>
    public bool XDspConvolution { get; set; }

    /// <summary>Keep the desktop's 15.6 ms timer ticks instead of 1 ms ones (<c>--desktop-timers</c>), to compare.</summary>
    public bool DesktopTimers { get; set; }

    /// <summary>Record what the game plays to this WAVE file (<c>--audio-capture</c>), from a child recorder process.</summary>
    public string? AudioCapture { get; set; }

    /// <summary>The recorder mode (<c>--record-audio &lt;pid&gt; &lt;file&gt;</c>): the process whose audio is recorded.</summary>
    public int? RecordAudioProcess { get; set; }

    /// <summary>Fail DirectStorage BCPack pages that have no block decoder instead of writing placeholder blocks (<c>--bcpack-missing fail</c>).</summary>
    public bool FailMissingBcPack { get; set; }

    /// <summary>The user's BCPack table file (<c>--bcpack-tables</c>), with which every BCPack codec decodes exactly.</summary>
    public string? BcPackTables { get; set; }

    /// <summary>What XNetworking reports (<c>--network</c>); outbound traffic is sealed in every mode.</summary>
    public NetworkMode NetworkMode { get; set; }

    /// <summary>The connectivity level reported instead of the mode's own (<c>--network-level</c>).</summary>
    public ConnectivityLevel? NetworkLevel { get; set; }

    public bool Verbose { get; set; }

    public bool ListSafeCalls { get; set; }

    /// <summary>File of <c>module!symbol</c> lines to classify through the shim table (the <c>--classify</c> mode).</summary>
    public string? ClassifyInput { get; set; }

    /// <summary>Where the classification goes; null writes it to standard output.</summary>
    public string? ClassifyOutput { get; set; }

    public bool Help { get; set; }
}
