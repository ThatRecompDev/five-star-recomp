// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.Input;

/// <summary>
/// What an input script's lines ask of the present path: <paramref name="Capture"/> reads back the next present and
/// <paramref name="Dump"/> makes it the frame dump, each for the line whose run-end entry it is given;
/// <paramref name="Sample"/> asks for the next present's signature.
/// </summary>
internal sealed record ScriptOutputs(Action<string> Capture, Action<string> Dump, Action Sample);
