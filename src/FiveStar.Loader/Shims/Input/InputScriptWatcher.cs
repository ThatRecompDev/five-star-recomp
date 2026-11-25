// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using FiveStar.Loader.Diagnostics;

namespace FiveStar.Loader.Shims.Input;

/// <summary>
/// Follows the <c>--input-script</c> file while the game runs. Every <see cref="Interval"/> it reloads the file when
/// its write time changed (a file that does not parse keeps the old script, warned once per change; begun blocks stay
/// begun), starts the <c>capture</c> and <c>learn</c> lines whose time has come, and while a <c>screen</c> or
/// <c>change</c> block waits asks for the next present's signature. A block begins when the <see cref="BlockMatcher"/>
/// sees its screen or its change, when its timeout passes (warned), or when the teach key (<see cref="TeachKey"/>) is
/// pressed while a screen block waits: the screen showing then is learned under the block's name. Marks already due when
/// a script is loaded are skipped.
/// </summary>
internal sealed class InputScriptWatcher : IDisposable
{
    /// <summary>F8 on the real keyboard.</summary>
    public const int TeachKey = 0x77;

    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    /// <summary>While a block waits, the nearest signature seen is logged this often.</summary>
    private const double ReportSeconds = 10;

    private readonly string _path;
    private readonly ScriptedDesktopInput _input;
    private readonly Stopwatch _clock;
    private readonly Action _capture;
    private readonly Action _sample;
    private readonly ScreenStore _screens;
    private readonly LoaderLog _log;
    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private readonly PressedKeys _realKeys = new();
    private readonly List<string> _learning = [];
    private InputScript _script;
    private bool[] _marksDone;
    private readonly BlockMatcher _matcher = new();
    private DateTime _written;
    private int _unlearnedBlock;
    private double _nextReport;
    private bool _teachHeld;
    private bool _teaching;

    /// <param name="path">The script file.</param>
    /// <param name="script">The script as loaded at start.</param>
    /// <param name="input">The scripted desktop source the presses go to.</param>
    /// <param name="clock">The script's clock.</param>
    /// <param name="capture">Requests a readback of the next present.</param>
    /// <param name="sample">Requests the next present's signature, delivered to <see cref="Seen"/>.</param>
    /// <param name="screens">The learned screens.</param>
    /// <param name="log">Reloads, marks and screens are logged.</param>
    public InputScriptWatcher(string path, InputScript script, ScriptedDesktopInput input, Stopwatch clock, Action capture, Action sample, ScreenStore screens, LoaderLog log)
    {
        _path = path;
        _script = script;
        _input = input;
        _clock = clock;
        _capture = capture;

        _sample = sample;
        _screens = screens;
        _log = log;

        _marksDone = new bool[script.Marks.Count];
        _written = File.GetLastWriteTimeUtc(path);
        _timer = new Timer(_ => Tick(), null, Interval, Interval);
    }

    public void Dispose()
    {
        _timer.Dispose();
    }

    /// <summary>A present's signature, from the presenting thread.</summary>
    public void Seen(ScreenSignature signature)
    {
        lock (_gate)
        {
            double seconds = _clock.Elapsed.TotalSeconds;

            foreach (string name in _learning)
            {
                _screens.Learn(name, signature);
                _log.Info($"  [input] screen {name} learned at {seconds:F1} s");
            }

            _learning.Clear();
            Match(signature, seconds);
        }
    }

    private void Tick()
    {
        if (!_gate.TryEnter())
        {
            return;
        }

        try
        {
            double seconds = _clock.Elapsed.TotalSeconds;
            Reload(seconds);
            StartMarks(seconds);
            Watch(seconds);
        }
        finally
        {
            _gate.Exit();
        }
    }

    private void Reload(double seconds)
    {
        DateTime written;
        InputScript script;

        try
        {
            written = File.GetLastWriteTimeUtc(_path);

            if (written == _written)
            {
                return;
            }

            _written = written;
            script = InputScript.Load(_path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            _log.Warn($"[input] script not reloaded: {exception.Message}");

            return;
        }

        script.Continue(_script);
        _script = script;
        _input.Replace(script);
        _marksDone = new bool[script.Marks.Count];

        for (int i = 0; i < _marksDone.Length; i++)
        {
            _marksDone[i] = script.TryTime(script.Marks[i].Block, script.Marks[i].Start, out double at) && at <= seconds;
        }

        _log.Info($"  [input] script reloaded at {seconds:F1} s: {script.Presses.Count} presses, {script.Marks.Count} captures and learns, {script.Screens.Count - 1} screens");
    }

    private void StartMarks(double seconds)
    {
        var marks = _script.Marks;

        for (int i = 0; i < marks.Count; i++)
        {
            if (_marksDone[i] || !_script.TryTime(marks[i].Block, marks[i].Start, out double at) || at > seconds)
            {
                continue;
            }

            _marksDone[i] = true;

            if (marks[i].Learn is null)
            {
                _capture();
                _log.Info($"  [input] script: capture at {seconds:F1} s");
            }
            else
            {
                _learning.Add(marks[i].Learn!);
                _sample();
            }
        }
    }

    /// <summary>
    /// Asks for a signature while a block waits for a learned screen or a change, or the teach key was pressed; begins a
    /// block that ran past its timeout.
    /// </summary>
    private void Watch(double seconds)
    {
        int block = _script.WaitingBlock;
        _input.ReadRealKeys(_realKeys);
        bool teach = _realKeys.IsDown(TeachKey);

        if (teach && !_teachHeld && block >= 0)
        {
            _teaching = true;
        }

        _teachHeld = teach;

        if (block < 0)
        {
            return;
        }

        var waiting = _script.Blocks[block];

        if (BlockMatcher.TimedOut(waiting, _script.WaitingSince(block), seconds))
        {
            _log.Warn($"[input] {Kind(waiting)} {waiting.Name} did not show within {waiting.Timeout:F1} s: block {block} begins anyway");
            Begin(block, waiting.Name, seconds, "timed out");

            return;
        }

        if (_teaching || waiting.Change || _screens.TryGet(waiting.Name, out _, out _))
        {
            _sample();
            Report(waiting, seconds);
        }
        else if (_unlearnedBlock != block)
        {
            _unlearnedBlock = block;
            _log.Info($"  [input] script waits for screen {waiting.Name}, which is not learned yet: press F8 when it shows");
        }
    }

    private void Match(ScreenSignature signature, double seconds)
    {
        int block = _script.WaitingBlock;

        if (block < 0)
        {
            return;
        }

        var waiting = _script.Blocks[block];

        if (_teaching && !waiting.Change)
        {
            _teaching = false;
            _screens.Learn(waiting.Name, signature);
            Begin(block, waiting.Name, seconds, "taught");

            return;
        }

        ScreenSignature? learned = null;
        int limit = 0;

        if (!waiting.Change && _screens.TryGet(waiting.Name, out var stored, out limit))
        {
            learned = stored;
        }

        string? how = _matcher.Seen(waiting, block, signature, learned, limit, !_script.Pressed(block - 1, seconds));

        if (how is not null)
        {
            Begin(block, waiting.Name, seconds, how);
        }
    }

    private void Begin(int block, string name, double seconds, string how)
    {
        _script.Begin(block, seconds);
        _matcher.Began();
        _teaching = false;
        _log.Info($"  [input] {Kind(_script.Blocks[block])} {name} at {seconds:F1} s ({how}): block {block} begins");
    }

    private static string Kind(ScriptBlock block)
    {
        if (block.Change)
        {
            return "change";
        }

        return "screen";
    }

    private void Report(ScriptBlock waiting, double seconds)
    {
        if (seconds < _nextReport)
        {
            return;
        }

        if (_nextReport > 0 && waiting.Change)
        {
            _log.Info($"  [input] waiting for change {waiting.Name} at {seconds:F1} s: at most {_matcher.Largest} cells changed");
        }
        else if (_nextReport > 0 && _matcher.Nearest != int.MaxValue)
        {
            _log.Info($"  [input] waiting for screen {waiting.Name} at {seconds:F1} s: nearest frame {_matcher.Nearest} cells changed");
        }

        _nextReport = seconds + ReportSeconds;
        _matcher.ResetNearest();
    }
}
