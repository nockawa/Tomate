using System.Diagnostics;
using JetBrains.Annotations;

namespace Tomate;

/// <summary>
/// A queue containing unmanaged items with ref access to each of them
/// </summary>
/// <typeparam name="T"></typeparam>
/// <remarks>
/// This class is heavily based from the <see cref="Queue{T}"/> type of the .net framework.
/// Designed for single thread usage only.
/// <see cref="TryPeek"/> and <see cref="Peek"/> return <code>ref of {T}</code>, so don't use this reference after a <see cref="Enqueue()"/> or <see cref="Dequeue"/> operation.
/// </remarks>
[PublicAPI]
[DebuggerTypeProxy(typeof(UnmanagedQueue<>.DebugView))]
[DebuggerDisplay("Count = {Count}")]
public unsafe struct UnmanagedQueue<T> : IDisposable where T : unmanaged
{
    private readonly IMemoryManager _memoryManager;
    private MemoryBlock<T> _memoryBlock;
    private int _size;
    private int _head;
    private int _tail;
    private int _capacity;
    private T* _buffer;

    private const int DefaultCapacity = 8;

    public UnmanagedQueue() : this(null)
    {
        
    }

    public UnmanagedQueue(IMemoryManager memoryManager=null, int capacity=DefaultCapacity)
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
    public bool IsDefault => _memoryManager == null;
    public bool IsDisposed => _size < 0;

    public void Clear()
    {
        _size = 0;
    }

    public ref T Enqueue()
    {
        if (_size == _capacity)
        {
            Grow(_size + 1);
        }

        var curTail = _tail;
        MoveNext(ref _tail);
        _size++;
        return ref _buffer[curTail];
    }

    public void Enqueue(ref T item)
    {
        if (_size == _capacity)
        {
            Grow(_size + 1);
        }

        _buffer[_tail] = item;
        MoveNext(ref _tail);
        _size++;
    }
    
    public void Enqueue(T item)
    {
        if (_size == _capacity)
        {
            Grow(_size + 1);
        }

        _buffer[_tail] = item;
        MoveNext(ref _tail);
        _size++;
    }
    
    private void MoveNext(ref int index)
    {
        // It is tempting to use the remainder operator here but it is actually much slower
        // than a simple comparison and a rarely taken branch.
        // JIT produces better code than with ternary operator ?:
        int tmp = index + 1;
        if (tmp == _capacity)
        {
            tmp = 0;
        }
        index = tmp;
    }
    
    /// <summary>
    /// Removes the object at the head of the queue and returns it.
    /// </summary>
    /// <returns>A reference to the item that was dequeued, don't use this reference after a <see cref="Queue{T}"/> operation as it may induce a resize of the
    /// internal buffer.
    /// </returns>
    /// <remarks>
    /// If the queue is empty, this method throws an InvalidOperationException.
    /// </remarks>
    public ref T Dequeue()
    {
        int head = _head;

        if (_size == 0)
        {
            ThrowForEmptyQueue();
        }

        MoveNext(ref _head);
        _size--;
        return ref _buffer[head];
    }

    public bool TryDequeue(out T result)
    {
        int head = _head;
        if (_size == 0)
        {
            result = default;
            return false;
        }

        result = _buffer[head];
        MoveNext(ref _head);
        _size--;
        return true;
    }

    public ref T Peek()
    {
        if (_size == 0)
        {
            ThrowForEmptyQueue();
        }

        return ref _buffer[_head];
    }

    // ReSharper disable once RedundantAssignment
    public bool TryPeek(ref T result)
    {
        if (_size == 0)
        {
            result = default;
            return false;
        }

        result = _buffer[_head];
        return true;
    }

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
        
        if (newCapacity < capacity) newCapacity = capacity;
        if (newCapacity < _size)
        {
            ThrowHelper.OutOfRange($"New Capacity {newCapacity} can't be less than actual Count {_size}");
        }

        var newItems = _memoryManager.Allocate<T>(newCapacity);
        
        if (_size > 0)
        {
            var src = new Span<T>(_buffer, _capacity);
            var dst = newItems.MemorySegment.ToSpan();
            if (_head < _tail)
            {
                src.Slice(_head, _size).CopyTo(dst);
            }
            else
            {
                src.Slice(_head, _size - _head).CopyTo(dst);
                src.Slice(0, _size - _head).CopyTo(dst.Slice(_size - _head));
            }
        }

        _head = 0;
        _tail = (_size == capacity) ? 0 : _size;
        _memoryManager.Free(_memoryBlock);
        _memoryBlock = newItems;
        _capacity = _memoryBlock.MemorySegment.Length;
        _buffer = _memoryBlock.MemorySegment.Address;
    }

    // Iterates over the objects in the queue, returning an array of the
    // objects in the Queue, or an empty array if the queue is empty.
    // The order of elements in the array is first in to last in, the same
    // order produced by successive calls to Dequeue.
    public T[] ToArray()
    {
        if (_size == 0)
        {
            return Array.Empty<T>();
        }

        T[] arr = new T[_size];

        var src = new Span<T>(_buffer, _capacity);
        var dst = arr.AsSpan();
        
        if (_head < _tail)
        {
            src.Slice(_head, _size).CopyTo(dst);
        }
        else
        {
            src.Slice(_head, _size - _head).CopyTo(dst);
            src.Slice(0, _size - _head).CopyTo(dst.Slice(_size - _head));
        }

        return arr;
    }

    private void ThrowForEmptyQueue()
    {
        Debug.Assert(_size == 0);
        ThrowHelper.EmptyQueue();
    }

    internal sealed class DebugView
    {
        private UnmanagedQueue<T> _queue;

        public DebugView(UnmanagedQueue<T> queue)
        {
            _queue = queue;
        }

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public T[] Items
        {
            get
            {
                var src = _queue._memoryBlock.MemorySegment.Slice(0, _queue.Count).ToSpan();
                var dst = new T[src.Length];
                src.CopyTo(dst);
                Array.Reverse(dst);
                return dst;
            }
        }
    }

    public void Dispose()
    {
        if (IsDefault)
        {
            return;
        }
        _memoryManager.Free(_memoryBlock);
        _memoryBlock = default;
        _size = -1;
    }
}
