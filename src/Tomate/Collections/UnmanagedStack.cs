using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tomate;

/// <summary>
/// A stack containing unmanaged items with ref access to each of them
/// </summary>
/// <typeparam name="T"></typeparam>
/// <remarks>
/// This class is heavily based from the <see cref="Stack{T}"/> type of the .net framework.
/// Designed for single thread usage only.
/// <see cref="TryPeek"/> and <see cref="Peek"/> return <code>ref of {T}</code>, so don't use this reference after a <see cref="Push"/> or <see cref="Pop"/> operation.
/// </remarks>
[DebuggerTypeProxy(typeof(UnmanagedStack<>.DebugView))]
[DebuggerDisplay("Count = {Count}")]
public struct UnmanagedStack<T> : IDisposable where T : unmanaged
{
    private readonly IMemoryManager _memoryManager;
    private MemorySegment<T> _data;
    private int _size;

    private const int DefaultCapacity = 8;

    public UnmanagedStack(IMemoryManager memoryManager, int capacity)
    {
        if (capacity < 0)
        {
            ThrowHelper.NeedNonNegIndex(nameof(capacity));
        }
        _memoryManager = memoryManager;
        _data = default;
        if (capacity > 0)
        {
            _data = _memoryManager.Allocate<T>(capacity);
        }

        _size = 0;
    }
    public int Count => _size;

    public ref T this[int index]
    {
        get
        {
            Debug.Assert((uint)index < _size);
            return ref _data[index];
        }
    }
    public bool IsEmpty => _memoryManager == null;
    public bool IsDisposed => _size < 0;

    public void Clear()
    {
        _size = 0;
    }

    // Returns the top object on the stack without removing it.  If the stack
    // is empty, Peek throws an InvalidOperationException.
    public ref T Peek()
    {
        int size = _size - 1;

        if ((uint)size >= (uint)_data.Length)
        {
            ThrowForEmptyStack();
        }

        return ref _data[size];
    }

    public bool TryPeek(ref T result)
    {
        int size = _size - 1;

        if ((uint)size >= (uint)_data.Length)
        {
            result = default!;
            return false;
        }
        result = ref _data[size];
        return true;
    }

    // Pops an item from the top of the stack.  If the stack is empty, Pop
    // throws an InvalidOperationException.
    public ref T Pop()
    {
        int size = _size - 1;

        // if (_size == 0) is equivalent to if (size == -1), and this case
        // is covered with (uint)size, thus allowing bounds check elimination
        // https://github.com/dotnet/coreclr/pull/9773
        if ((uint)size >= (uint)_data.Length)
        {
            ThrowForEmptyStack();
        }

        _size = size;
        return ref _data[size];
    }

    public bool TryPop(out T result)
    {
        int size = _size - 1;

        if ((uint)size >= (uint)_data.Length)
        {
            result = default!;
            return false;
        }

        _size = size;
        result = _data[size];
        return true;
    }

    public ref T Push()
    {
        if (_size >= _data.Length)
        {
            Grow(_size + 1);
        }

        return ref _data[_size++];
    }

    // Pushes an item to the top of the stack.
    public void Push(ref T item)
    {
        int size = _size;

        if ((uint)size < (uint)_data.Length)
        {
            _data[size] = item;
            _size = size + 1;
        }
        else
        {
            PushWithResize(ref item);
        }
    }

    public void Push(T item)
    {
        int size = _size;

        if ((uint)size < (uint)_data.Length)
        {
            _data[size] = item;
            _size = size + 1;
        }
        else
        {
            PushWithResize(ref item);
        }
    }

    // Non-inline from Stack.Push to improve its code quality as uncommon path
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PushWithResize(ref T item)
    {
        Debug.Assert(_size == _data.Length);
        Grow(_size + 1);
        _data[_size] = item;
        _size++;
    }

    private unsafe void Grow(int capacity)
    {
        var newCapacity = _data.Length == 0 ? DefaultCapacity : 2 * _data.Length;

        // Check if the new capacity exceed the size of the block we can allocate
        if ((newCapacity * sizeof(T)) > _memoryManager.MaxAllocationLength)
        {
            newCapacity = _memoryManager.MaxAllocationLength / sizeof(T);

            if (newCapacity < capacity)
            {
                ThrowHelper.OutOfMemory($"The requested capacity {capacity} is greater than the maximum allowed capacity {newCapacity}. Use a Memory Manager with a greater PMB size");
            }
        }
        if (newCapacity < _size)
        {
            ThrowHelper.OutOfRange($"New Capacity {newCapacity} can't be less than actual Count {_size}");
        }

        var newItems = _memoryManager.Allocate<T>(newCapacity);
        _data.Slice(0, _size).ToSpan().CopyTo(newItems.ToSpan());

        _memoryManager.Free(_data);
        _data = newItems;
    }

    // Copies the Stack to an array, in the same order Pop would return the items.
    public T[] ToArray()
    {
        if (_size == 0)
        {
            return Array.Empty<T>();
        }

        T[] objArray = new T[_size];
        int i = 0;
        while (i < _size)
        {
            objArray[i] = _data[_size - i - 1];
            i++;
        }
        return objArray;
    }

    private void ThrowForEmptyStack()
    {
        Debug.Assert(_size == 0);
        ThrowHelper.EmptyStack();
    }

    internal sealed class DebugView
    {
        private UnmanagedStack<T> _stack;

        public DebugView(UnmanagedStack<T> stack)
        {
            _stack = stack;
        }

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public T[] Items
        {
            get
            {
                var src = _stack._data.Slice(0, _stack.Count).ToSpan();
                var dst = new T[src.Length];
                src.CopyTo(dst);
                Array.Reverse(dst);
                return dst;
            }
        }
    }

    public void Dispose()
    {
        if (IsEmpty)
        {
            return;
        }
        _memoryManager.Free(_data);
        _data = default;
        _size = -1;
    }
}