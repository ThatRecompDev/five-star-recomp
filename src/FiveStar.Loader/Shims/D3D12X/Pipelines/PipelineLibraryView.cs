// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.MemoryMappedFiles;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// A segment file mapped for reading (<see cref="PipelineLibraryFile.Open"/>): its pipeline names and library blob. A
/// library made from the blob reads it in every load, so <see cref="Dispose"/> unmaps it only once no load holds it
/// (<see cref="Enter"/>): the cache's disposal at the end of a boot unmapped it under a build thread's load.
/// </summary>
internal sealed class PipelineLibraryView : IDisposable
{
    private readonly object _gate = new();
    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private int _users;
    private bool _disposed;
    private bool _unmapped;

    public PipelineLibraryView(MemoryMappedFile map, MemoryMappedViewAccessor view, IReadOnlyList<string> keys, nint blob, nuint size)
    {
        _map = map;
        _view = view;
        Keys = keys;
        Blob = blob;
        Size = size;
    }

    public IReadOnlyList<string> Keys { get; }

    /// <summary>The serialized ID3D12PipelineLibrary; valid until disposal and the last <see cref="Exit"/>.</summary>
    public nint Blob { get; }

    public nuint Size { get; }

    /// <summary>Whether the blob is still mapped.</summary>
    public bool Mapped
    {
        get
        {
            lock (_gate)
            {
                return !_unmapped;
            }
        }
    }

    /// <summary>A load starts reading the blob; false once the view is disposed.</summary>
    public bool Enter()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _users++;

            return true;
        }
    }

    /// <summary>That load is done; the last one after <see cref="Dispose"/> unmaps the blob.</summary>
    public void Exit()
    {
        lock (_gate)
        {
            if (--_users > 0 || !_disposed)
            {
                return;
            }
        }

        Unmap();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_users > 0)
            {
                return;
            }
        }

        Unmap();
    }

    private void Unmap()
    {
        lock (_gate)
        {
            if (_unmapped)
            {
                return;
            }

            _unmapped = true;
        }

        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _map.Dispose();
    }
}
