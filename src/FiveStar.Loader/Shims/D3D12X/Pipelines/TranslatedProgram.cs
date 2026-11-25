// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Hardware;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>A program translated and compiled ahead of time: its name, hardware stage and DXIL (in native memory, <see cref="ProgramCode"/>).</summary>
internal sealed record TranslatedProgram(string Name, HardwareStage Stage, ProgramCode Code)
{
    /// <summary>A program over a native copy of <paramref name="dxil"/>.</summary>
    public static TranslatedProgram Of(string name, HardwareStage stage, ReadOnlySpan<byte> dxil)
    {
        return new TranslatedProgram(name, stage, ProgramCode.Copy(dxil));
    }

    /// <summary>The DXIL; valid while this program is reachable.</summary>
    public ReadOnlySpan<byte> Dxil => Code.Span;
}
