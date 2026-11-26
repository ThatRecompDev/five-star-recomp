// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Cli.Archive;

/// <summary>The command line cannot be used as typed; the message says why.</summary>
internal sealed class CommandLineException : ArgumentException
{
    public CommandLineException(string message)
        : base(message)
    {
    }
}
