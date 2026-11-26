// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;

namespace FiveStar.Graphics.Replay;

/// <summary>The kind of a <see cref="CapturedView"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CapturedViewKind>))]
public enum CapturedViewKind
{
    ShaderResource,
    UnorderedAccess,
}
