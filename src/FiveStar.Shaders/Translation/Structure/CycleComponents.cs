// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Shaders.Translation.Structure;

/// <summary>
/// The strongly connected components of a region of a graph that hold a cycle (Tarjan's algorithm, iterative: a
/// program has tens of thousands of blocks). Edges leaving the region, to the end marker or back to the region's
/// header are left out, so a loop's own back edges do not join its body into one component again.
/// </summary>
internal sealed class CycleComponents
{
    private readonly List<List<int>> _successors;
    private readonly HashSet<int> _region;
    private readonly int? _header;
    private readonly int _end;
    private readonly Dictionary<int, int> _index = [];
    private readonly Dictionary<int, int> _low = [];
    private readonly HashSet<int> _onStack = [];
    private readonly Stack<int> _stack = new();
    private readonly Stack<(int Node, int Next)> _work = new();
    private readonly List<HashSet<int>> _components = [];
    private int _counter;

    public CycleComponents(List<List<int>> successors, HashSet<int> region, int? header, int end)
    {
        _successors = successors;
        _region = region;
        _header = header;
        _end = end;
    }

    public List<HashSet<int>> Find()
    {
        foreach (var root in _region.Order().Where(root => !_index.ContainsKey(root)))
        {
            Open(root);

            while (_work.Count > 0)
            {
                var (node, next) = _work.Pop();

                if (next < _successors[node].Count)
                {
                    _work.Push((node, next + 1));
                    Follow(node, _successors[node][next]);

                    continue;
                }

                Close(node);
            }
        }

        return _components;
    }

    private void Open(int node)
    {
        _index[node] = _counter;
        _low[node] = _counter;
        _counter++;
        _stack.Push(node);
        _onStack.Add(node);
        _work.Push((node, 0));
    }

    private void Follow(int node, int target)
    {
        if (target == _end || target == _header || !_region.Contains(target))
        {
            return;
        }

        if (!_index.TryGetValue(target, out int seen))
        {
            Open(target);

            return;
        }

        if (_onStack.Contains(target))
        {
            _low[node] = Math.Min(_low[node], seen);
        }
    }

    /// <summary>All of the node's edges are followed: its low link goes to its parent, and a root pops its component.</summary>
    private void Close(int node)
    {
        if (_work.Count > 0)
        {
            int parent = _work.Peek().Node;
            _low[parent] = Math.Min(_low[parent], _low[node]);
        }

        if (_low[node] != _index[node])
        {
            return;
        }

        var component = new HashSet<int>();
        int member;

        do
        {
            member = _stack.Pop();
            _onStack.Remove(member);
            component.Add(member);
        }
        while (member != node);

        if (component.Count > 1 || (node != _header && _successors[node].Contains(node)))
        {
            _components.Add(component);
        }
    }
}
