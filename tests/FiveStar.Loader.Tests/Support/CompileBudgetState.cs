// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using Xunit;

namespace FiveStar.Loader.Tests.Support;

/// <summary>
/// Tests of the compile budget's process-wide counts run alone, after the parallel ones: every pipeline another test
/// builds enters and leaves the same budget, and would move the counts they compare.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CompileBudgetState
{
    public const string Name = "process-wide compile budget";
}
