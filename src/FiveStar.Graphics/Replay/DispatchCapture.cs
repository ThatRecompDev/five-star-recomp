// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Graphics.Replay;

/// <summary>
/// One compute dispatch the loader captured (<c>--capture-dispatch</c>), as <see cref="FileName"/> in its folder: the
/// program, the user data its root arguments filled, the group counts (an ExecuteIndirect's resolved records), the
/// resources it can reach with the bytes they held before it ran and after, and the descriptors of the bindless heap
/// that name them. Everything a fresh device needs to run it again (<see cref="DispatchReplay"/>).
/// </summary>
/// <param name="Pipeline">The pipeline's name: the translated programs it was built from.</param>
/// <param name="Program">The program and its files in the capture folder.</param>
/// <param name="UserConstants">Whether the user data is bound as root constants (else a root CBV over 128 dwords).</param>
/// <param name="UserData">The 128 user-data dwords: dword n is user SGPR n.</param>
/// <param name="Dispatches">The group counts, one entry per dispatch the call ran.</param>
/// <param name="HeapSize">Descriptors of the shader-visible view heap the views need.</param>
/// <param name="FallbackTable">The heap index root parameter 1 (the mesh fallback table) points at.</param>
/// <param name="Resources">The buffers and textures.</param>
/// <param name="Views">The descriptors written into the heap.</param>
/// <param name="Notes">What the capture could not keep, for the reader.</param>
public sealed record DispatchCapture(
    string Pipeline,
    CapturedProgram Program,
    bool UserConstants,
    IReadOnlyList<uint> UserData,
    IReadOnlyList<CapturedDispatch> Dispatches,
    uint HeapSize,
    uint FallbackTable,
    IReadOnlyList<CapturedResource> Resources,
    IReadOnlyList<CapturedView> Views,
    IReadOnlyList<string> Notes)
{
    /// <summary>The manifest's name in a capture folder.</summary>
    public const string FileName = "capture.json";

    /// <summary>The layout this reader understands.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Sampler descriptors of the shader-visible sampler heap (each a linear clamp sampler in the replay).</summary>
    public uint SamplerHeapSize { get; init; } = MaxSamplers;

    public int Version { get; init; } = CurrentVersion;

    /// <summary>D3D12_MAX_SHADER_VISIBLE_SAMPLER_HEAP_SIZE (d3d12.h).</summary>
    private const uint MaxSamplers = 2048;
}
