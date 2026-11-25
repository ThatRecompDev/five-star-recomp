// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Pipelines;

namespace FiveStar.Loader.Tests.Support;

/// <summary>A program library whose every read throws, as a corrupt program file would.</summary>
internal sealed class ThrowingProgramLibrary : IProgramLibrary
{
    public TranslatedProgram? Find(ReadOnlySpan<byte> prefix) => throw new InvalidDataException("unreadable program");

    public IReadOnlyList<string> Names(ReadOnlySpan<byte> prefix) => throw new InvalidDataException("unreadable program");

    public TranslatedProgram? Load(string name) => throw new InvalidDataException("unreadable program");

    public long HeldBytes => 0;
}
