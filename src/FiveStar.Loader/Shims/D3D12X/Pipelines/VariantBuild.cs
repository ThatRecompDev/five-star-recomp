// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>A rectangle-list variant being built (<see cref="RectanglePipelines"/>): its recipe, and set once it is done.</summary>
internal sealed class VariantBuild(RectangleRecipe recipe)
{
    public RectangleRecipe Recipe { get; } = recipe;

    public ManualResetEventSlim Done { get; } = new();
}
