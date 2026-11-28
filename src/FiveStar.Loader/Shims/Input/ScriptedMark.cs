// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.Input;

/// <summary>
/// A <c>capture</c>, <c>dump</c>, <c>exit</c> or <c>learn &lt;name&gt;</c> line of an input script, <paramref name="Start"/>
/// seconds after its block began; <paramref name="Learn"/> is the screen name of a learn line, null for the others.
/// </summary>
internal readonly record struct ScriptedMark(int Block, double Start, ScriptedMarkKind Kind, string? Learn)
{
    /// <summary>Whether the line asks for output a run waits for (<c>capture</c>, <c>dump</c>).</summary>
    public bool IsWork => Kind is ScriptedMarkKind.Capture or ScriptedMarkKind.Dump;

    /// <summary>The run-end entry of a capture or dump line: its kind, block and time ("input-script dump 1:3").</summary>
    public string EntryName => $"input-script {Kind.ToString().ToLowerInvariant()} {Block}:{Start.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
