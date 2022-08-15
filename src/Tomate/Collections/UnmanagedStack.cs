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
public unsafe struct UnmanagedStack<T> : IDisposable where T : unmanaged
{
    private readonly IMemoryManager _memoryManager;
    private MemoryBlock<T> _memoryBlock;
    private int _size;
    private int _capacity;
    private T* _buffer;

    private const int DefaultCapacity = 8;

    public UnmanagedStack(IMemoryManager memoryManager=null, int capacity=DefaultCapacity)
    {
        if (capacity < 0)
        {
            ThrowHelper.NeedNonNegIndex(nameof(capacity));
        }
        _memoryManager = memoryManager ?? DefaultMemoryManager.GlobalInstance;
        _memoryBlock = default;
        if (capacity > 0)
        {
            _memoryBlock = _memoryManager.Allocate<T>(capacity);
        }

        _buffer = _memoryBlock.MemorySegment.Address;
        _size = 0;
        _capacity = _memoryBlock.MemorySegment.Length;
    }
    public int Count => _size;

    public ref T this[int index]
    {
        get
        {
            Debug.Assert((uint)index < _size);
            return ref _buffer[index];
        }
    }
    public bool IsEmpty => _memoryManager == null;
    public bool IsDisposed => _size < 0;

    public void Clear()
    {
        _size = 0;
    }

    public ref T Push()
    {
        if (_size >= _capacity)
        {
            Grow(_size + 1);
        }

        return ref _buffer[_size++];
    }

    // Pushes an item to the top of the stack.
    public void Push(ref T item)
    {
        if ((uint)_size < (uint)_capacity)
        {
            _buffer[_size] = item;
            ++_size;
        }
        else
        {
            PushWithResize(ref item);
        }
    }

    public void Push(T item)
    {
        if ((uint)_size < (uint)_capacity)
        {
            _buffer[_size] = item;
            ++_size;
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
        Debug.Assert(_size == _capacity);
        Grow(_size + 1);
        _buffer[_size] = item;
        _size++;
    }

    // Returns the top object on the stack without removing it.  If the stack
    // is empty, Peek throws an InvalidOperationException.
    public ref T Pop()
    {
        --_size;

        if ((uint)_size >= (uint)_capacity)
        {
            ThrowForEmptyStack();
        }

        return ref _buffer[_size];
    }

    public bool TryPop(out T result)
    {
        --_size;

        if ((uint)_size >= (uint)_capacity)
        {
            result = default;
            return false;
        }

        result = _buffer[_size];
        return true;
    }

    public ref T Peek()
    {
        int size = _size - 1;

        if ((uint)size >= (uint)_capacity)
        {
            ThrowForEmptyStack();
        }

        return ref _buffer[size];
    }

    public bool TryPeek(ref T result)
    {
        int size = _size - 1;

        if ((uint)size >= (uint)_capacity)
        {
            result = default!;
            return false;
        }
        result = ref _buffer[size];
        return true;
    }

    // Pops an item from the top of the stack.  If the stack is empty, Pop
    // throws an InvalidOperationException.

    private void Grow(int capacity)
    {
        var newCapacity = _capacity == 0 ? DefaultCapacity : 2 * _capacity;

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
        _memoryBlock.MemorySegment.Slice(0, _size).ToSpan().CopyTo(newItems.MemorySegment.ToSpan());

        _memoryManager.Free(_memoryBlock);
        _memoryBlock = newItems;
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
            objArray[i] = _buffer[_size - i - 1];
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
                var src = _stack._memoryBlock.MemorySegment.Slice(0, _stack.Count).ToSpan();
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
        _memoryManager.Free(_memoryBlock);
        _memoryBlock = default;
        _size = -1;
    }
}