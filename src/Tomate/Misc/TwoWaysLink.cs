using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Tomate;

public struct TwoWaysLinkedList<T> where T : unmanaged
{
    public delegate ref Link Accessor(T id);

    public struct Link
    {
        public T Previous;
        public T Next;
    }

    public int Count => _count;
    public bool IsEmpty => _count == 0;

    public T FirstId => _first;

    public T LastId
    {
        get
        {
            if (IsEmpty)
            {
                return default;
            }

            return _accessor(_first).Previous;
        }
    }

    private readonly Func<T> _allocator;
    private readonly Accessor _accessor;

    private T _first;
    private int _count;
    private readonly EqualityComparer<T> _comparer;

    public TwoWaysLinkedList(Func<T> allocator, Accessor accessor)
    {
        _allocator = allocator;
        _accessor = accessor;
        _first = default;
        _count = 0;
        _comparer = EqualityComparer<T>.Default;
    }

    public T InsertNewFirst()
    {
        return InsertFirst(Allocate());
    }

    public T InsertFirst(T nodeId)
    {
        CheckId(nodeId);
        ref var node = ref _accessor(nodeId);

        if (_count == 0)
        {
            _first = nodeId;
            node.Previous = _first;
            node.Next = default;

            ++_count;
            return nodeId;
        }

        ref var curFirst = ref _accessor(_first);

        node.Previous = curFirst.Previous;
        node.Next = _first;

        curFirst.Previous = nodeId;

        _first = nodeId;

        ++_count;
        return _first;
    }

    public T InsertNewLast()
    {
        if (IsEmpty)
        {
            return InsertNewFirst();
        }

        var curId = Allocate();
        return InsertLast(curId);
    }

    public T InsertLast(T curId)
    {
        CheckId(curId);
        ref var cur = ref _accessor(curId);

        if (IsDefault(_first))
        {
            return InsertFirst(curId);
        }

        ref var first = ref _accessor(_first);
        var lastId = first.Previous;
        ref var last = ref _accessor(lastId);

        first.Previous = curId;
        last.Next = curId;

        cur.Previous = lastId;
        cur.Next = default;

        ++_count;
        return curId;
    }

    public T InsertNew(T leftId)
    {
        // Insert first case
        if (IsDefault(leftId))
        {
            return InsertNewFirst();
        }

        var nodeId = Allocate();
        return Insert(leftId, nodeId);
    }

    public T Insert(T leftId, T nodeId)
    {
        CheckId(nodeId);

        if (IsDefault(leftId))
        {
            return InsertFirst(nodeId);
        }

        ref var node = ref _accessor(nodeId);
        ref var left = ref _accessor(leftId);

        node.Previous = leftId;
        node.Next = left.Next;

        if (IsDefault(left.Next) == false)
        {
            ref var next = ref _accessor(left.Next);
            next.Previous = nodeId;
        }
        else
        {
            _accessor(_first).Previous = nodeId;
        }

        left.Next = nodeId;

        ++_count;
        return nodeId;
    }

    public void Remove(T id)
    {
        if (_comparer.Equals(id, _first))
        {
            ref var first = ref _accessor(id);
            var nextId = first.Next;

            if (IsDefault(nextId) == false)
            {
                ref var next = ref _accessor(nextId);
                next.Previous = first.Previous;
            }

            _first = nextId;

            first.Previous = first.Next = default;
            --_count;
        }
        else
        {
            ref var cur = ref _accessor(id);
            var prevId = cur.Previous;
            var nextId = cur.Next;

            ref var prev = ref _accessor(prevId);
            prev.Next = nextId;

            if (IsDefault(nextId) == false)
            {
                ref var next = ref _accessor(nextId);
                next.Previous = prevId;
            }
            else
            {
                _accessor(_first).Previous = prevId;
            }

            cur.Previous = cur.Next = default;
            --_count;
        }
    }

    public int Walk(Func<T, bool> action)
    {
        var processed = 0;
        if (IsEmpty)
        {
            return processed;
        }

        var curId = _first;
        while (IsDefault(curId) == false)
        {
            ++processed;
            if (action(curId) == false)
            {
                return processed;
            }

            curId = Next(curId);
        }

        return processed;
    }

    public T Previous(T id)
    {
        if (_comparer.Equals(id, _first))
        {
            return default;
        }

        return _accessor(id).Previous;
    }

    public T Next(T id)
    {
        ref var n = ref _accessor(id);
        return n.Next;
    }

    public bool CheckIntegrity()
    {
        var hashForward = new HashSet<T>(Count);
        
        // Forward link integrity test
        var cur = _first;
        var first = cur;
        var last = cur;
        while (IsDefault(cur) == false)
        {
            last = cur;
            if (hashForward.Add(cur) == false)
            {
                return false;
            }
            ref var header = ref _accessor(cur);
            cur = header.Next;
        }

        // Check last item is the first's previous
        ref var firstHeader = ref _accessor(first);
        if (_comparer.Equals(firstHeader.Previous, last) == false)
        {
            return false;
        }

        // Backward link integrity test
        var hashBackward = new HashSet<T>(Count);
        cur = firstHeader.Previous;
        last = cur;
        first = cur;
        while (IsDefault(cur) == false)
        {
            first = cur;
            if (hashBackward.Add(cur) == false)
            {
                return false;
            }

            if (_comparer.Equals(_first, cur))
            {
                cur = default;
            }
            else
            {
                ref var header = ref _accessor(cur);
                cur = header.Previous;
            }
        }

        // Check first
        if (_comparer.Equals(_first, first) == false)
        {
            return false;
        }

        if (hashForward.Count != hashBackward.Count)
        {
            return false;
        }

        foreach (var e in hashForward)
        {
            if (hashBackward.Contains(e) == false)
            {
                return false;
            }
        }

        return true;
    }

    private bool IsDefault(T leftId)
    {
        return _comparer.Equals(leftId, default(T));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private T Allocate()
    {
        Debug.Assert(_allocator != null, "You must specify an Allocator lambda at construction time to use this API");
        var res = _allocator();
        CheckId(res);
        return res;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckId(T id)
    {
        Debug.Assert(IsDefault(id) == false, "The allocator can't return an Id that is equal to the default value of <T>. Default is used to mark the absence of a link.");
    }
}
