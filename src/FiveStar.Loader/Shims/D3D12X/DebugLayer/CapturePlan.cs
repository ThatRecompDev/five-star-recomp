// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Graphics.Replay;

namespace FiveStar.Loader.Shims.D3D12X.DebugLayer;

/// <summary>
/// What one captured dispatch reaches, as <see cref="CapturePlanner"/> found it while the game recorded it: the user
/// data, the window bytes of the tables it reads (copied then), the resources to copy on the GPU, the views that name
/// them, the game memory ranges and the group counts (direct, or an ExecuteIndirect's argument records).
/// </summary>
internal sealed class CapturePlan
{
    public CapturePlan(string pipeline, uint[] userData, bool userConstants, uint windowBytes)
    {
        Pipeline = pipeline;
        UserData = userData;
        UserConstants = userConstants;
        WindowBytes = windowBytes;
    }

    public string Pipeline { get; }

    /// <summary>The 128 compute user-data dwords.</summary>
    public uint[] UserData { get; }

    public bool UserConstants { get; }

    public uint WindowBytes { get; }

    /// <summary>Window bytes the program's tables cover, by window offset.</summary>
    public List<(uint Offset, byte[] Bytes)> Patches { get; } = [];

    public List<CaptureSource> Sources { get; } = [];

    /// <summary>The game memory ranges among <see cref="Sources"/>, with the replay heap index of their view pair.</summary>
    public List<(CaptureSource Source, uint Pair)> Mapped { get; } = [];

    public List<PlannedView> Views { get; } = [];

    /// <summary>Console address ranges the program can read, before they are merged into <see cref="Mapped"/>.</summary>
    public List<(ulong Start, ulong Bytes)> Memory { get; } = [];

    /// <summary>Desktop textures already among <see cref="Sources"/>, by resource.</summary>
    public Dictionary<nint, CaptureSource> Textures { get; } = [];

    public HashSet<uint> SeenIndices { get; } = [];

    public List<string> Notes { get; } = [];

    /// <summary>The group counts of a Dispatch; null for an ExecuteIndirect.</summary>
    public CapturedDispatch? Direct { get; set; }

    public IndirectArguments? Indirect { get; set; }

    /// <summary>Bytes of the readback buffer (<see cref="CaptureCopies.Layout"/>).</summary>
    public ulong ReadbackBytes { get; set; }

    /// <summary>Bytes of all sources so far, against the capture's cap.</summary>
    public ulong SourceBytes { get; set; }
}
