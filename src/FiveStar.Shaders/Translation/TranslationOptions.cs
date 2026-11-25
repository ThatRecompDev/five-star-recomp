// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Shaders.Translation;

/// <summary>
/// Translator choices. <paramref name="StructuredControlFlow"/> emits branches as if/else and loops instead of the
/// block dispatch loop, where the program's control-flow graph allows it. <paramref name="PinWaveSize"/> gives a compute
/// program that moves data between lanes by lane number <c>[WaveSize(n)]</c> with its native wave size, so it runs only
/// where the GPU offers that size instead of computing wrong results. <paramref name="EmulateWaveSize"/> makes the wave
/// operations of pixel, vertex and mesh programs keep console lane numbers on any hardware wave size (rdna_prelude.hlsli,
/// "program waves"); without it they use the hardware wave as it is. <see cref="Loops"/> bounds translated loops
/// per stage (<see cref="Core.LoopCaps"/>); null keeps the translator's own caps.
/// </summary>
public sealed record TranslationOptions(bool StructuredControlFlow, bool PinWaveSize, bool EmulateWaveSize)
{
    public Core.LoopCaps? Loops { get; init; }
}
