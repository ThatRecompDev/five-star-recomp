// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>A depth bound through a view of a texture replaced at its address is named in the census with its binds.</summary>
public sealed class ReplacedTargetsTests
{
    [Fact]
    public void A_depth_bound_after_its_texture_was_replaced_is_counted()
    {
        nint replaced = 0x5eed0;
        nint kept = 0x5eee0;
        ReplacedTargets.Note(replaced, "128x128 format 53, replaced by 512x512 format 53");
        ReplacedTargets.NoteDepthView(0x100, replaced, "128x128");
        ReplacedTargets.NoteDepthView(0x200, kept, "512x512");

        ReplacedTargets.BoundDepth(0x100);
        ReplacedTargets.BoundDepth(0x100);
        ReplacedTargets.BoundDepth(0x200);
        ReplacedTargets.BoundDepth(0);

        var text = new StringBuilder();
        ReplacedTargets.Append(text);

        Assert.Contains($"bound after being replaced: 0x{replaced:x} 128x128 format 53, replaced by 512x512 format 53, 2 binds", text.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain($"0x{kept:x}", text.ToString(), StringComparison.Ordinal);
    }
}
