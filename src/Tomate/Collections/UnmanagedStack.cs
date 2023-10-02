using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Tomate;

/// <summary>
/// A stack containing unmanaged items with ref access to each of them
/// </summary>
/// <typeparam name="T"></typeparam>
/// <remarks>
/// This class is heavily based from the <see cref="Stack{T}"/> type of the .net framework.
/// Designed for single thread usage only.
/// <see cref="TryPeek"/> and <see cref="Peek"/> return <code>ref of {T}</code>, so don't use this reference after a <see cref="Push(ref T)"/> or <see cref="Pop"/> operation.
/// </remarks>
[PublicAPI]
[DebuggerTypeProxy(typeof(UnmanagedStack<>.DebugView))]
[DebuggerDisplay("Count = {Count}")]
public unsafe struct UnmanagedStack<T> : IDisposable where T : unmanaged
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public int _size;
        public int _capacity;
        private ulong _padding;
    }

    private Header* _header => (Header*)_memoryBlock.MemorySegment.Address;
    private T* _buffer => (T*)(_header + 1);

    private ref int _size => ref _header->_size;
    private ref int _capacity => ref _header->_capacity;

    //private readonly IMemoryManager _memoryManager;
    private MemoryBlock _memoryBlock;
    /*
    private int _size;
    private int _capacity;
    private T* _buffer;
    */

    private const int DefaultCapacity = 8;

    public UnmanagedStack() : this(null)
    {
        
    }

    public UnmanagedStack(IMemoryManager memoryManager=null, int capacity=DefaultCapacity)
    {
        if (capacity < 0)
        {
            ThrowHelper.NeedNonNegIndex(nameof(capacity));
        }
        memoryManager ??= DefaultMemoryManager.GlobalInstance;
        _memoryBlock = default;
        if (capacity > 0)
        {
            _memoryBlock = memoryManager.Allocate(sizeof(Header) + sizeof(T) * capacity);
        }
        else
        {
            _memoryBlock = memoryManager.Allocate(sizeof(Header));
        }

        //_buffer = _memoryBlock.MemorySegment.Address;
        _size = 0;
        _capacity = capacity;
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

    public MemorySegment<T> Content => new ((_buffer), _size);
    public bool IsDefault => _memoryBlock.IsDefault;
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

    // ReSharper disable once RedundantAssignment
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
        var memoryManager = _memoryBlock.MemoryManager;
        var maxAllocationLength = memoryManager.MaxAllocationLength;
        var headerSize = sizeof(Header);
        var itemSize = sizeof(T);
        if ((headerSize + newCapacity * itemSize) > maxAllocationLength)
        {
            newCapacity = (maxAllocationLength - headerSize) / itemSize;

            if (newCapacity < capacity)
            {
                ThrowHelper.OutOfMemory($"The requested capacity {capacity} is greater than the maximum allowed capacity {newCapacity}. Use a Memory Manager with a greater PMB size");
            }
        }
        
        _memoryBlock.Resize(sizeof(Header) + (itemSize * newCapacity));
        _capacity = newCapacity;
        /*
        var newItems = memoryManager.Allocate(headerSize + itemSize * newCapacity);
        _memoryBlock.MemorySegment.Slice(0, _size).ToSpan().CopyTo(newItems.MemorySegment.ToSpan());

        _memoryManager.Free(_memoryBlock);
        _memoryBlock = newItems;
        _capacity = _memoryBlock.MemorySegment.Length;
        _buffer = _memoryBlock.MemorySegment.Address;
    */
    }

    // Copies the Stack to an array, in the same order Pop would return the items.
    public T[] ToArray()
    {
        if (_size == 0)
        {
            return Array.Empty<T>();
        }

        var objArray = new T[_size];
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
                var data = new Span<T>(_stack._buffer, _stack._size);
                var items = new T[data.Length];
                var dest = new Span<T>(items);
                data.CopyTo(dest);
                
                /*
                var src = _stack._memoryBlock.MemorySegment.Slice(0, _stack.Count).ToSpan();
                var dst = new T[src.Length];
                src.CopyTo(dst);
                */
                Array.Reverse(items);
                return items;
            }
        }
    }

    public void Dispose()
    {
        if (IsDefault || IsDisposed)
        {
            return;
        }
        
        // _memoryManager.Free(_memoryBlock);
        _memoryBlock.Dispose();
        _memoryBlock = default;
        // _size = -1;
    }
}