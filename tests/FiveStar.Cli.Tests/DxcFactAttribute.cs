// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Cli.Shader;
using Xunit;

namespace FiveStar.Cli.Tests;

/// <summary>A test that compiles HLSL with dxc and runs it on D3D12 WARP: skipped off Windows or without dxc (FIVESTAR_DXC).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DxcFactAttribute : FactAttribute
{
    public DxcFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "D3D12 WARP runs on Windows only";

            return;
        }

        var dxc = ShaderTools.Dxc(new CommandArguments([], new HashSet<string>()));

        if (!File.Exists(dxc))
        {
            Skip = "dxc not found (set FIVESTAR_DXC to dxc.exe)";
        }
    }
}
