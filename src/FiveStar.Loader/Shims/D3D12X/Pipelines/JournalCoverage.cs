// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// At device creation, how many pipelines earlier boots used (the journal, <see cref="PipelineJournal"/>) are not in the
/// disk cache: the game builds those on the driver when it first asks for them, and a boot after one that was killed
/// before its cache was written could sit on a black screen for minutes doing so. One line says so at the start, then
/// the progress every <see cref="Interval"/> until all are cached or nothing changes for <see cref="IdleChecks"/>
/// checks. A journaled pipeline's cache key needs its programs; one whose programs are not made yet is checked again at
/// each interval.
/// </summary>
internal sealed class JournalCoverage
{
    /// <summary>The time between progress lines.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>Checks in a row with no change before the lines stop.</summary>
    private const int IdleChecks = 10;

    private readonly PipelineFactory _factory;
    private readonly PipelineLibraryCache _cache;
    private readonly LoaderLog _log;
    private readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private List<WarmTarget> _unchecked = [];

    public JournalCoverage(PipelineFactory factory, PipelineLibraryCache cache, LoaderLog log)
    {
        _factory = factory;
        _cache = cache;
        _log = log;
    }

    /// <summary>Journaled pipelines found missing from the cache so far.</summary>
    public int Missing => _missing.Count;

    /// <summary>Journaled pipelines whose programs were not made yet at the last check.</summary>
    public int Unchecked => _unchecked.Count;

    /// <summary>Of <see cref="Missing"/>, those the cache holds now.</summary>
    public int Built => _missing.Count(_cache.Contains);

    /// <summary>Checks the journal in <paramref name="folder"/> against the cache on a background thread and logs while pipelines are missing.</summary>
    public static void Start(PipelineFactory factory, PipelineLibraryCache? cache, string? folder, LoaderLog log)
    {
        if (cache is null || folder is null)
        {
            return;
        }

        var coverage = new JournalCoverage(factory, cache, log);
        var thread = new Thread(() => coverage.Run(folder)) { IsBackground = true, Name = "fivestar journal coverage", Priority = ThreadPriority.Lowest };
        thread.Start();
    }

    /// <summary>
    /// Takes <paramref name="targets"/> to check: each whose programs are made is looked up in the cache (missing ones
    /// are kept), the others wait for the next <see cref="Check"/>. Returns how many were found missing now.
    /// </summary>
    public int Check(List<WarmTarget> targets)
    {
        var waiting = new List<WarmTarget>();
        int found = 0;

        foreach (var target in targets)
        {
            var recipe = Prepare(target);

            if (recipe is null)
            {
                waiting.Add(target);

                continue;
            }

            string key = recipe.Stream.Key;
            recipe.Stream.Dispose();

            if (!_cache.Contains(key) && _missing.Add(key))
            {
                found++;
            }
        }

        _unchecked = waiting;

        return found;
    }

    /// <summary>"B of N built and cached, U not checked yet" for the progress line.</summary>
    public string Progress()
    {
        return $"{Built} of {Missing} built and cached, {Missing - Built} left; {Unchecked} more not checked yet (their programs are not made yet)";
    }

    private void Run(string folder)
    {
        List<WarmTarget> targets;

        try
        {
            targets = PipelineJournal.Read(folder);
        }
        catch (IOException exception)
        {
            _log.Warn($"[d3d12_xs] journal coverage off: {exception.Message}");

            return;
        }

        Check(targets);
        Follow();
    }

    /// <summary>The line that says pipelines are missing, once some are.</summary>
    private bool Announce(bool announced)
    {
        if (announced || Missing == 0)
        {
            return announced;
        }

        _log.Warn($"[d3d12_xs] {Missing} journaled pipelines are not in the cache: building them, the screen may stay black for a while ({Unchecked} more not checked yet: their programs are not made yet)");

        return true;
    }

    /// <summary>
    /// The first line once pipelines are found missing, then the progress every <see cref="Interval"/>, until nothing is
    /// left to build or check, or nothing changes for <see cref="IdleChecks"/> checks. Silent when nothing is missing.
    /// </summary>
    private void Follow()
    {
        var last = (Built: -1, Missing: -1, Unchecked: -1);
        bool announced = Announce(false);
        int idle = 0;

        while (idle < IdleChecks && (Missing > Built || Unchecked > 0))
        {
            Thread.Sleep(Interval);
            int found = Check(_unchecked);
            var now = (Built, Missing, Unchecked);
            idle = now == last ? idle + 1 : 0;
            last = now;

            if (announced)
            {
                _log.Info($"  [d3d12_xs] journaled pipelines missing from the cache: {Progress()}{Found(found)}");
            }

            announced = Announce(announced);
        }

        if (announced)
        {
            _log.Info($"  [d3d12_xs] journaled pipelines missing from the cache, last: {Progress()}");
        }
    }

    private static string Found(int found)
    {
        if (found == 0)
        {
            return string.Empty;
        }

        return $" ({found} found missing in this check)";
    }

    /// <summary>The recipe of a target; null while its programs are not made (they are not made here) or it has none.</summary>
    private PipelineRecipe? Prepare(WarmTarget target)
    {
        try
        {
            if (target.Compute is not null && _factory.Ready(target.Compute))
            {
                return _factory.PrepareCompute(target.Compute);
            }

            if (target.Graphics is not null && _factory.Ready(target.Graphics))
            {
                return _factory.PrepareGraphics(target.Graphics);
            }

            return null;
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }
}
