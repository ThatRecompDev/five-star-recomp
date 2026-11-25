// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>A record a program samples through (<see cref="SharedExponentSamplers"/>): the user SGPR holding its table and its byte offset there.</summary>
internal readonly record struct SampledRecord(string Program, int Sgpr, uint Offset);
