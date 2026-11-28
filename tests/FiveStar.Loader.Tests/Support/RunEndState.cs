// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using Xunit;

namespace FiveStar.Loader.Tests.Support;

/// <summary>
/// Tests of the process-wide run-end and frame-dump state (<c>RunEnd</c>'s settings and request, <c>FrameStageDump</c>'s
/// present, filter and script requests) run alone, after the parallel ones, so one test's settings never reach another.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RunEndState
{
    public const string Name = "process-wide run-end state";
}
