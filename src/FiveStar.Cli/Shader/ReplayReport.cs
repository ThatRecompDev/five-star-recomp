// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Graphics.Replay;

namespace FiveStar.Cli.Shader;

/// <summary>A replay's runs, how each output differs from the captured one, and the debug layer's errors.</summary>
internal sealed record ReplayReport(ReplayOutcome Outcome, IReadOnlyList<OutputDifference> Differences, IReadOnlyList<string> DebugErrors);
