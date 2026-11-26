// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using Xunit;

namespace FiveStar.Loader.Tests.Support;

/// <summary>A test on the Nsight Aftermath SDK's DLL: skipped unless FIVESTAR_AFTERMATH_DLL names GFSDK_Aftermath_Lib.x64.dll.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AftermathFactAttribute : FactAttribute
{
    public const string Variable = "FIVESTAR_AFTERMATH_DLL";

    public AftermathFactAttribute()
    {
        if (Library is null)
        {
            Skip = $"{Variable} does not name GFSDK_Aftermath_Lib.x64.dll";
        }
    }

    /// <summary>The DLL's path, or null when the variable does not name a file.</summary>
    public static string? Library
    {
        get
        {
            string? path = Environment.GetEnvironmentVariable(Variable);

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            return path;
        }
    }
}
