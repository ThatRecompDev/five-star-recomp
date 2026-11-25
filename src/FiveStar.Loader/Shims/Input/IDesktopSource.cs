// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.Input;

/// <summary>Where keyboard and mouse input come from.</summary>
internal interface IDesktopSource
{
    /// <summary>Fills <paramref name="keys"/> with every virtual key held now, mouse buttons included.</summary>
    void ReadKeys(PressedKeys keys);

    /// <summary>
    /// Fills <paramref name="keys"/> with the keys the virtual gamepad maps to buttons: the held keys, but an input
    /// script's keys only through its pad keys (PadA and the rest), so a scripted key reaches the game once.
    /// </summary>
    void ReadPadKeys(PressedKeys keys)
    {
        ReadKeys(keys);
    }

    /// <summary>The cursor position in screen pixels; the shim turns its changes into mouse motion.</summary>
    CursorPosition ReadCursor();

    /// <summary>The scan code of a virtual key, 0 when it has none.</summary>
    uint ScanCode(int virtualKey);
}
