// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using FiveStar.Loader.Loading;

namespace FiveStar.Loader.Diagnostics;

/// <summary>An event or semaphore of the game (<see cref="WaitObjects"/>): how it was made and when it was last signaled.</summary>
internal sealed class WaitObject
{
    /// <summary>Frames of the creating call shown: those in mapped images (the game and its manual modules).</summary>
    private const int ShownFrames = 6;

    /// <summary>Operations kept per object (signals, resets, waits and wake-ups), for a lost wake-up.</summary>
    private const int KeptOperations = 12;

    private readonly object _gate = new();
    private readonly nint[] _frames;
    private readonly WaitOperation[] _operations = new WaitOperation[KeptOperations];
    private uint _signaler;
    private double _signaledAt = -1;
    private long _signals;
    private long _nextOperation;

    public WaitObject(string kind, uint creator, double createdAt, nint[] frames)
    {
        Kind = kind;
        Creator = creator;
        CreatedAt = createdAt;
        _frames = frames;
    }

    public string Kind { get; }

    public uint Creator { get; }

    public double CreatedAt { get; }

    public void Signal(uint thread, double at, nint caller)
    {
        lock (_gate)
        {
            _signaler = thread;
            _signaledAt = at;
            _signals++;
            AddOperation(new WaitOperation("signaled", thread, at, caller));
        }
    }

    /// <summary>Keeps <paramref name="operation"/> among the object's last <see cref="KeptOperations"/>.</summary>
    public void Record(WaitOperation operation)
    {
        lock (_gate)
        {
            AddOperation(operation);
        }
    }

    private void AddOperation(WaitOperation operation)
    {
        _operations[_nextOperation % KeptOperations] = operation;
        _nextOperation++;
    }

    /// <summary>The kept operations, oldest first, as "-27.6 s signaled by 14500 at game+0x.., ..".</summary>
    private string History(ImageTable images, double now)
    {
        long first = Math.Max(0, _nextOperation - KeptOperations);
        var shown = new List<string>();

        for (long index = first; index < _nextOperation; index++)
        {
            var operation = _operations[index % KeptOperations];
            string at = operation.Caller == 0 ? string.Empty : " at " + Frames([operation.Caller], images);
            shown.Add(string.Create(CultureInfo.InvariantCulture, $"-{now - operation.At:F1} s {operation.Verb} by {operation.Thread}{at}"));
        }

        return string.Join(", ", shown);
    }

    /// <summary>The frames in mapped images, as "game+0x.. &lt;- ..", or "no game frame found".</summary>
    public static string Frames(nint[] frames, ImageTable images)
    {
        var shown = frames.Where(frame => images.FindByAddress(frame) is not null).Take(ShownFrames).Select(images.Describe).ToList();

        if (shown.Count == 0)
        {
            return "no game frame found";
        }

        return string.Join(" <- ", shown);
    }

    /// <summary>"auto-reset event made by thread T at S s from game+0x.. &lt;- ..; signaled N times, last ..".</summary>
    public string Describe(ImageTable images, double now)
    {
        string made = Frames(_frames, images);
        string signal;
        string history;

        lock (_gate)
        {
            signal = _signals == 0
                ? "never signaled"
                : string.Create(CultureInfo.InvariantCulture, $"signaled {_signals} times, last {now - _signaledAt:F1} s ago by thread {_signaler}");
            history = History(images, now);
        }

        return string.Create(CultureInfo.InvariantCulture, $"{Kind} made by thread {Creator} at {CreatedAt:F1} s from {made}; {signal}; recent: {history}");
    }
}
