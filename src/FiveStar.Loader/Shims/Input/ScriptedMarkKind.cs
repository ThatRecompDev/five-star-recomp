// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.Input;

/// <summary>What a timed non-key line of an input script does (<see cref="ScriptedMark"/>).</summary>
internal enum ScriptedMarkKind
{
    /// <summary><c>capture</c>: the next present is read back (<c>--capture-dir</c>).</summary>
    Capture,

    /// <summary><c>learn &lt;name&gt;</c>: the next present's signature is stored as a screen.</summary>
    Learn,

    /// <summary><c>dump</c>: the next present is the <c>--dump-frame</c> frame.</summary>
    Dump,

    /// <summary><c>exit</c>: the run ends once the captures and dumps already started are written.</summary>
    Exit,
}
