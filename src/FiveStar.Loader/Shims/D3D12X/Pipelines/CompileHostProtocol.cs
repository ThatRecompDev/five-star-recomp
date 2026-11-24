// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Compilation;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// The messages between the loader and its compile host (<see cref="CompileHost"/>, <see cref="CompileHostServer"/>)
/// over the host's standard input and output. A request is its id, the priority of the thread that asks (the host
/// compiles at the same, so a game thread's inline compile keeps its priority), profile, source path
/// and source; a result its id, verdict and reason. Strings are length-prefixed UTF-8 (<see cref="BinaryWriter.Write(string)"/>).
/// </summary>
internal static class CompileHostProtocol
{
    /// <summary>The loader's first argument that makes it a compile host; the second, when present, names dxil.dll.</summary>
    public const string Mode = "--compile-host";

    public static void WriteRequest(BinaryWriter writer, int id, ThreadPriority priority, CompileRequest request)
    {
        writer.Write(id);
        writer.Write((int)priority);
        writer.Write(request.Profile);
        writer.Write(request.HlslPath);
        writer.Write(request.Hlsl);
        writer.Flush();
    }

    public static (int Id, ThreadPriority Priority, CompileRequest Request) ReadRequest(BinaryReader reader)
    {
        var id = reader.ReadInt32();
        var priority = (ThreadPriority)reader.ReadInt32();
        var profile = reader.ReadString();
        var path = reader.ReadString();
        var hlsl = reader.ReadString();

        return (id, priority, new CompileRequest(path, hlsl, profile, DisableOptimizations: false));
    }

    public static void WriteResult(BinaryWriter writer, int id, BackendResult result)
    {
        writer.Write(id);
        writer.Write((int)result.Verdict);
        writer.Write(result.Reason);
        writer.Flush();
    }

    public static (int Id, BackendResult Result) ReadResult(BinaryReader reader)
    {
        var id = reader.ReadInt32();
        var verdict = (BackendVerdict)reader.ReadInt32();
        var reason = reader.ReadString();

        return (id, new BackendResult(verdict, reason));
    }
}
