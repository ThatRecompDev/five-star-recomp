// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Formats.Images;

/// <summary>An 8-bit RGBA image, rows top first, 4 bytes per pixel.</summary>
public sealed record RgbaImage(int Width, int Height, byte[] Pixels);
