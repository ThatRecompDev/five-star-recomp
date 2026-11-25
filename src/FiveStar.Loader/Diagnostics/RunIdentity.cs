// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// What a run was made of, for the first line of its log: the loader's build (version + the commit it was built from,
/// so a run of a branch with picked pull requests names that branch's head) and the command line with every flag, so
/// a log says which options (skipped pipelines, guards, backends) shaped what it shows.
/// </summary>
internal static class RunIdentity
{
    public static string Line()
    {
        var assembly = typeof(RunIdentity).Assembly;
        string build = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        string arguments = string.Join(' ', Environment.GetCommandLineArgs().Skip(1));

        return $"build {build}; .NET {Environment.Version}; command line: {arguments}";
    }
}
