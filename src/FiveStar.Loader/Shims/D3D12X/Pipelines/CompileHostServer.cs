// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Graphics.Programs;
using FiveStar.Loader.Diagnostics;
using FiveStar.Shaders.Compilation;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// <c>fivestar-loader --compile-host [dxil.dll]</c>: serves the loader's requests, each on a thread of its own at the
/// asking thread's priority, and answers in the order they finish. A compile runs the DXIL backend
/// (<see cref="InProcessBackend"/>); a make also translates the program and picks the adapter's form first
/// (<see cref="ProgramProducer.Prepare"/>), so the translator's allocations stay out of the loader as well. It ends when
/// its input ends, which is when the loader ends.
/// </summary>
internal static class CompileHostServer
{
    private const int Success = 0;
    private const int ValidatorArgument = 1;
    private const string DxilExtension = ".dxil";

    public static int Run(string[] args)
    {
        string? validator = null;

        if (args.Length > ValidatorArgument)
        {
            validator = args[ValidatorArgument];
        }

        var backend = new InProcessBackend(validator);
        var warnings = new OnceLog(new LoaderLog(TextWriter.Null, null, false));
        using var input = new BinaryReader(Console.OpenStandardInput());
        using var output = new BinaryWriter(Console.OpenStandardOutput());
        var gate = new Lock();

        while (true)
        {
            int id;
            ThreadPriority priority;
            CompileRequest? compile;
            MakeRequest? make;

            try
            {
                (id, _, priority, compile, make) = CompileHostProtocol.ReadRequest(input);
            }
            catch (EndOfStreamException)
            {
                return Success;
            }

            var thread = new Thread(() =>
            {
                var answer = Serve(backend, warnings, compile, make);

                lock (gate)
                {
                    CompileHostProtocol.WriteAnswer(output, id, answer);
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

    /// <summary>The answer to one request; an error (a file that cannot be written, no validator) is answered as such, so the loader takes the work.</summary>
    private static HostAnswer Serve(InProcessBackend backend, OnceLog warnings, CompileRequest? compile, MakeRequest? make)
    {
        try
        {
            if (compile is not null)
            {
                var result = backend.Run(compile);

                return HostAnswer.Of(result.Verdict, result.Reason);
            }

            return Make(backend, warnings, make!);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return HostAnswer.Failed($"error: {exception.GetType().Name} {exception.Message}");
        }
    }

    /// <summary>Translates, picks the form and compiles it into <c>&lt;folder&gt;/&lt;name&gt;.dxil</c>; a form the backend cannot take comes back for dxc.</summary>
    private static HostAnswer Make(InProcessBackend backend, OnceLog warnings, MakeRequest make)
    {
        var picker = new ProgramVariantPicker(new ShaderManifest([]), make.Adapter, warnings);
        var compiler = new DxilBackendCompiler(DxcOptions.Defaults(string.Empty, retry: false), null, backend);
        var producer = new ProgramProducer(make.Programs, make.Folder, picker, compiler, SlowCompiles.Read([], compiler.Options.CompileTimeout)) { Header = make.Header };
        var form = producer.Prepare(make.Name);

        if (form.Failure is not null)
        {
            return HostAnswer.Refused(form.Failure);
        }

        var request = form.Request!;
        var result = backend.Run(request);

        if (result.Verdict == BackendVerdict.Compiled)
        {
            var target = Path.Combine(make.Folder, make.Name + DxilExtension);

            if (!string.Equals(request.DxilPath, target, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(request.DxilPath, target, overwrite: true);
            }

            return HostAnswer.Compiled;
        }

        return new HostAnswer((HostAnswerKind)result.Verdict, result.Reason, request.Profile, Path.GetFileName(request.HlslPath), request.Hlsl);
    }
}
