// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Compilation;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// <c>fivestar-loader --compile-host [dxil.dll]</c>: compiles the loader's requests with the DXIL backend
/// (<see cref="InProcessBackend"/>), each on a thread of its own at the asking thread's priority, and answers in the order they finish. It ends when
/// its input ends, which is when the loader ends.
/// </summary>
internal static class CompileHostServer
{
    private const int Success = 0;
    private const int ValidatorArgument = 1;

    public static int Run(string[] args)
    {
        string? validator = null;

        if (args.Length > ValidatorArgument)
        {
            validator = args[ValidatorArgument];
        }

        var backend = new InProcessBackend(validator);
        using var input = new BinaryReader(Console.OpenStandardInput());
        using var output = new BinaryWriter(Console.OpenStandardOutput());
        var gate = new Lock();

        while (true)
        {
            int id;
            ThreadPriority priority;
            CompileRequest request;

            try
            {
                (id, priority, request) = CompileHostProtocol.ReadRequest(input);
            }
            catch (EndOfStreamException)
            {
                return Success;
            }

            var thread = new Thread(() =>
            {
                var result = Compile(backend, request);

                lock (gate)
                {
                    CompileHostProtocol.WriteResult(output, id, result);
                }
            })
            {
                IsBackground = true,
                Name = "fivestar compile host",
                Priority = priority,
            };
            thread.Start();
        }
    }

    /// <summary>The backend's verdict; an error (a file that cannot be written, no validator) is reported as not lowered, so dxc takes the program.</summary>
    private static BackendResult Compile(InProcessBackend backend, CompileRequest request)
    {
        try
        {
            return backend.Run(request);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new BackendResult(BackendVerdict.NotLowered, $"error: {exception.GetType().Name} {exception.Message}");
        }
    }
}
