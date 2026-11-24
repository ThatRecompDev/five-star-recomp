// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using FiveStar.Shaders.Compilation;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// Runs the DXIL backend in a child process, the loader itself in <see cref="CompileHostProtocol.Mode"/> with server
/// GC, so the backend's allocations never pause the game's threads and a compiler crash does not end the game. Requests
/// from any thread go over one pipe and wait for their answers. A host that ends is started again at the next request;
/// the requests it held go to dxc. When no host starts, the backend runs in this process. The machine-wide memory
/// guards see the host like any other process.
/// </summary>
internal sealed class CompileHost : IBackendRunner
{
    private const int StartAttempts = 3;
    private const int EndsTolerated = 16;
    private const string ServerGcVariable = "DOTNET_gcServer";
    private const string Ended = "the compile host ended";
    private const string EndedTooOften = "the compile host ended too often";
    private const double BytesPerGigabyte = 1 << 30;

    private readonly string _executable;
    private readonly string? _validator;
    private readonly InProcessBackend _here;
    private readonly Lock _gate = new();
    private Connection? _connection;
    private int _starts;
    private int _failedStarts;
    private int _ends;
    private int _nextId;

    /// <param name="executable">The loader executable.</param>
    /// <param name="validator">dxil.dll, whose validator checks every container; null checks none.</param>
    public CompileHost(string executable, string? validator)
    {
        _executable = executable;
        _validator = validator;
        _here = new InProcessBackend(validator);
    }

    public bool Validates => _validator is not null;

    /// <summary>Hosts started, hosts that ended while running, and the private memory of the current one.</summary>
    public string Summary
    {
        get
        {
            var connection = _connection;
            var gigabytes = 0.0;

            if (connection is not null)
            {
                gigabytes = connection.PrivateBytes / BytesPerGigabyte;
            }

            if (Volatile.Read(ref _failedStarts) >= StartAttempts)
            {
                return "compile host: did not start, compiling in this process";
            }

            return $"compile host: {Volatile.Read(ref _starts)} started, {Volatile.Read(ref _ends)} ended, {gigabytes:F1} GB private";
        }
    }

    public BackendResult Run(CompileRequest request)
    {
        if (Volatile.Read(ref _ends) >= EndsTolerated)
        {
            return new BackendResult(BackendVerdict.NotLowered, EndedTooOften);
        }

        for (var attempt = 0; attempt < StartAttempts; attempt++)
        {
            var connection = Connect();

            if (connection is null)
            {
                return _here.Run(request);
            }

            var (result, sent) = connection.Send(Interlocked.Increment(ref _nextId), request);

            if (result is not null)
            {
                return result;
            }

            // A host that ended while compiling the request may have ended because of it: dxc takes it. One that had
            // ended before is replaced.
            if (sent)
            {
                return new BackendResult(BackendVerdict.NotLowered, Ended);
            }
        }

        return new BackendResult(BackendVerdict.NotLowered, Ended);
    }

    /// <summary>The current host's process id, or null; for tests.</summary>
    internal int? ProcessId => _connection?.ProcessId;

    /// <summary>Whether a host is running; for tests.</summary>
    internal bool Running => _connection is { Alive: true };

    /// <summary>The running host, started when there is none; null when none starts.</summary>
    private Connection? Connect()
    {
        lock (_gate)
        {
            if (_connection is { Alive: true })
            {
                return _connection;
            }

            // A host that ended before it answered anything did not start.
            if (_connection is { Answered: true })
            {
                _ends++;
            }
            else if (_connection is not null)
            {
                _failedStarts++;
            }

            _connection = null;

            if (_failedStarts >= StartAttempts)
            {
                return null;
            }

            _connection = Connection.Start(_executable, _validator);

            if (_connection is null)
            {
                _failedStarts++;
            }
            else
            {
                _starts++;
            }

            return _connection;
        }
    }

    /// <summary>One host process: a writer for requests and a thread that hands each answer to its waiting request.</summary>
    private sealed class Connection
    {
        private readonly Process _process;
        private readonly BinaryWriter _writer;
        private readonly Lock _writing = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<BackendResult?>> _pending = new();
        private volatile bool _alive = true;
        private volatile bool _answered;

        private Connection(Process process)
        {
            _process = process;
            _writer = new BinaryWriter(process.StandardInput.BaseStream);
            var reader = new Thread(Read) { IsBackground = true, Name = "fivestar compile host reader" };
            reader.Start();
        }

        public bool Alive => _alive;

        /// <summary>Whether the host answered a request.</summary>
        public bool Answered => _answered;

        public long PrivateBytes
        {
            get
            {
                try
                {
                    _process.Refresh();

                    return _process.PrivateMemorySize64;
                }
                catch (InvalidOperationException)
                {
                    return 0;
                }
            }
        }

        public static Connection? Start(string executable, string? validator)
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(CompileHostProtocol.Mode);

            if (validator is not null)
            {
                start.ArgumentList.Add(validator);
            }

            start.Environment[ServerGcVariable] = "1";

            try
            {
                var process = Process.Start(start);

                if (process is null)
                {
                    return null;
                }

                return new Connection(process);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        public int ProcessId => _process.Id;

        /// <summary>The answer to one request, null when the host ended first; whether the request reached the host.</summary>
        public (BackendResult? Result, bool Sent) Send(int id, CompileRequest request)
        {
            var answer = new TaskCompletionSource<BackendResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = answer;

            if (!_alive && _pending.TryRemove(id, out _))
            {
                return (null, false);
            }

            try
            {
                lock (_writing)
                {
                    CompileHostProtocol.WriteRequest(_writer, id, request);
                }
            }
            catch (IOException)
            {
                End();

                return (null, false);
            }

            return (answer.Task.GetAwaiter().GetResult(), true);
        }

        private void Read()
        {
            using var reader = new BinaryReader(_process.StandardOutput.BaseStream);

            try
            {
                while (true)
                {
                    var (id, result) = CompileHostProtocol.ReadResult(reader);
                    _answered = true;

                    if (_pending.TryRemove(id, out var answer))
                    {
                        answer.SetResult(result);
                    }
                }
            }
            catch (Exception exception) when (exception is EndOfStreamException or IOException)
            {
                End();
            }
        }

        /// <summary>Marks the host ended and answers every waiting request with null; the process is killed if it still runs.</summary>
        private void End()
        {
            _alive = false;

            foreach (var id in _pending.Keys)
            {
                if (_pending.TryRemove(id, out var answer))
                {
                    answer.TrySetResult(null);
                }
            }

            try
            {
                _process.Kill();
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }
    }
}
