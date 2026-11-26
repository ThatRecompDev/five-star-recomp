// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;

namespace FiveStar.Graphics.Replay;

/// <summary>The JSON form of <see cref="DispatchCapture"/>: camelCase, indented, absent values left out.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(DispatchCapture))]
internal sealed partial class CaptureJsonContext : JsonSerializerContext;
