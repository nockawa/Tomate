using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;
#pragma warning disable CS9084 // Struct member returns 'this' or other instance members by reference

namespace Tomate;

// Implementation notes
// Here, what matters the most is performance, maintainability and readability are secondary.
// The type itself implements all features and the subtype Accessor is a ref struct that allows to access the queue in a more efficient way, 
//  but with less safety. Code is duplicated because...I don't have the choice. The performance gain is too important to ignore.

/// <summary>
/// A queue containing unmanaged items with (ref) access to each of them
/// </summary>
/// <typeparam name="T"></typeparam>
/// <remarks>
/// This class is heavily based from the <see cref="Queue{T}"/> type of the .net framework.
/// Designed for single thread usage only.
/// <see cref="TryPeek"/> and <see cref="Peek"/> return <code>ref of {T}</code>, so don't use this reference after a <see cref="Enqueue()"/> or
///  <see cref="Dequeue"/> operation.
/// </remarks>
[PublicAPI]
[DebuggerTypeProxy(typeof(UnmanagedQueue<>.DebugView))]
[DebuggerDisplay("Count = {Count}")]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct UnmanagedQueue<T> : IUnmanagedCollection where T : unmanaged
{
    #region Constants

    private const int DefaultCapacity = 8;

    #endregion

    #region Public APIs

    #region Properties

    /// <summary>
    /// Subscript operator for random access to an item in the queue
    /// </summary>
    /// <param name="index">The index of the item to retrieve, must be within the range of [0..Count-1]</param>
    /// <remarks>
    /// This API checks for the bounds and throws the index is incorrect.
    /// Will throw <see cref="InvalidObjectException"/> if the instance is default or disposed.
    /// </remarks>
    public ref T this[int index]
    {
        get
        {
            EnsureInternalState();
            var header = _header;
            if (header == null)
            {
                ThrowHelper.InvalidObject(null);
            }
            if ((uint)index >= (uint)header->Count)
            {
                ThrowHelper.OutOfRange($"Index {index} must be less than {header->Count} and greater or equal to 0");
            }
            return ref _buffer[index];
        }
    }

    /// <summary>
    /// Get the item count
    /// </summary>
    /// <returns>
    /// The number of items in the queue or -1 if the instance is invalid.
    /// </returns>
    public int Count => IsDefault ? -1 : _header->Count;

    /// <summary>
    /// Check if the instance is the default (not constructed) one
    /// </summary>
    /// <remarks>
    /// A default instance can't be used, some APIs will assert in debug mode and crash in release mode, others will throw an exception or return -1.
    /// There is no distinction between a default instance and a disposed one.
    /// </remarks>
    public bool IsDefault => _memoryBlock.IsDefault;

    /// <summary>
    /// Check if the instance is disposed
    /// </summary>
    /// <remarks>
    /// A disposed instance can't be used, some APIs will assert in debug mode and crash in release mode, others will throw an exception or return -1.
    /// There is no distinction between a default instance and a disposed one.
    /// </remarks>
    public bool IsDisposed => _header->Count < 0;

    /// <summary>
    /// Access to the underlying MemoryBlock of the queue, use with caution
    /// </summary>
    public MemoryBlock MemoryBlock => _memoryBlock;

    /// <summary>
    /// Access to the MemoryManager used by the queue to allocate its content
    /// </summary>
    public IMemoryManager MemoryManager => _memoryBlock.MemoryManager;

    /// <summary>
    /// Get the reference counter of the instance, will return -1 if the instance is default/disposed
    /// </summary>
    public int RefCounter => _memoryBlock.IsDefault ? -1 : _memoryBlock.RefCounter;

    /// <summary>
    /// Get/set the capacity of the queue
    /// </summary>
    /// <remarks>
    /// Get accessor will return -1 if the queue is disposed/default.
    /// Set accessor will throw an exception if the new capacity is less than the actual count.
    /// </remarks>
    public int Capacity
    {
        get
        {
            if (IsDefault)
            {
                return -1;
            }
            EnsureInternalState();
            var header = _header;
            return header->Capacity;
        }
        set 
        {
            EnsureInternalState();
            // Cache the value, because unfortunately accessing the address of the memory block is not that fast compare to what we need
            var header = _header;
            if (value < header->Count)
            {
                ThrowHelper.OutOfRange($"New Capacity {value} can't be less than actual Count {header->Count}");
            }
            if (value != header->Capacity)
            {
                _memoryBlock.Resize(sizeof(Header) + (sizeof(T) * value));
                EnsureInternalState(true);
                header = _header;
                header->Capacity = value;
            }
        }
    }

    #endregion

    #region Methods

    public int AddRef() => _memoryBlock.AddRef();

    /// <summary>
    /// Clear the content of the queue
    /// </summary>
    public void Clear()
    {
        EnsureInternalState();
        var header = _header;
        header->Count = 0;
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
        EnsureInternalState();
        var header = _header;
        var head = header->Head;

        if (header->Count == 0)
        {
            ThrowForEmptyQueue();
        }

        MoveNext(ref header->Head);
        header->Count--;
        return ref _buffer[head];
    }

    /// <summary>
    /// Dispose the instance, see remarks
    /// </summary>
    /// <remarks>
    /// This call will decrement the reference counter by 1 and the instance will effectively be disposed if it reaches 0, otherwise it will still be usable.
    /// </remarks>
    public void Dispose()
    {
        EnsureInternalState();
        if (IsDefault)
        {
            return;
        }
        _memoryBlock.Dispose();
        _memoryBlock = default;
    }

    /// <summary>
    /// Enqueue and return a reference to the new item
    /// </summary>
    /// <returns>
    /// This method allocates the item in the queue and return a reference to it, it's up to the caller to set the value of the item.
    /// Don't keep the reference more than what is strictly necessary because it can be invalidated by operations that resize the content of the queue.
    /// </returns>
    public ref T Enqueue()
    {
        EnsureInternalState();
        var header = _header;
        if (header->Count == header->Capacity)
        {
            Grow(header->Count + 1);
            EnsureInternalState(true);
            header = _header;
        }

        var curTail = header->Tail;
        MoveNext(ref header->Tail);
        header->Count++;
        return ref _buffer[curTail];
    }

    /// <summary>
    /// Enqueue an item in the queue
    /// </summary>
    /// <param name="item">A reference to the item to enqueue, which is preferred from the non-reference version if your struct is big.</param>
    /// <remarks>
    /// This method is safer than <see cref="Enqueue()"/>.
    /// </remarks>
    public void Enqueue(ref T item)
    {
        EnsureInternalState();
        var header = _header;
        if (header->Count == header->Capacity)
        {
            Grow(header->Count + 1);
            EnsureInternalState(true);
            header = _header;
        }

        _buffer[header->Tail] = item;
        MoveNext(ref header->Tail);
        header->Count++;
    }

    /// <summary>
    /// Enqueue an item in the queue
    /// </summary>
    /// <param name="item">The item to enqueue</param>
    /// <remarks>
    /// This method is safer than <see cref="Enqueue()"/>.
    /// </remarks>
    public void Enqueue(T item)
    {
        EnsureInternalState();
        var header = _header;
        if (header->Count == header->Capacity)
        {
            Grow(header->Count + 1);
            EnsureInternalState(true);
            header = _header;
        }

        _buffer[header->Tail] = item;
        MoveNext(ref header->Tail);
        header->Count++;
    }

    /// <summary>
    /// Peek the item at the head of the queue
    /// </summary>
    /// <returns>A reference to the item, will throw if the queue is empty.</returns>
    public ref T Peek()
    {
        EnsureInternalState();
        var header = _header;
        if (header->Count == 0)
        {
            ThrowForEmptyQueue();
        }

        return ref _buffer[header->Head];
    }

    // Iterates over the objects in the queue, returning an array of the
    // objects in the Queue, or an empty array if the queue is empty.
    // The order of elements in the array is first in to last in, the same
    // order produced by successive calls to Dequeue.
    public T[] ToArray()
    {
        EnsureInternalState();
        var header = _header;
        if (header->Count == 0)
        {
            return [];
        }

        var arr = new T[header->Count];

        var src = new Span<T>(_buffer, header->Capacity);
        var dst = arr.AsSpan();
        
        if (header->Head < header->Tail)
        {
            src.Slice(header->Head, header->Count).CopyTo(dst);
        }
        else
        {
            src.Slice(header->Head, header->Capacity - header->Head).CopyTo(dst);
            src.Slice(0, header->Tail).CopyTo(dst.Slice(header->Capacity - header->Head));
        }
        return arr;
    }

    /// <summary>
    /// Attempt to dequeue an item from the queue
    /// </summary>
    /// <param name="result">The dequeued item</param>
    /// <returns>Returns <c>true</c> if the queue was not empty and an item was dequeued and returned, <c>false</c> otherwise.</returns>
    public bool TryDequeue(out T result)
    {
        EnsureInternalState();
        var header = _header;
        var head = header->Head;
        if (header->Count == 0)
        {
            result = default;
            return false;
        }

        result = _buffer[head];
        MoveNext(ref header->Head);
        header->Count--;
        return true;
    }

    /// <summary>
    /// Attempt to peek an item from the queue
    /// </summary>
    /// <param name="result">A reference that will contain the item at the head of the queue.</param>
    /// <returns><c>true</c> if there was an item, <c>false</c> if the queue was empty.</returns>
    // ReSharper disable once RedundantAssignment
    public bool TryPeek(ref T result)
    {
        EnsureInternalState();
        var header = _header;
        if (header->Count == 0)
        {
            result = default;
            return false;
        }

        result = _buffer[header->Head];
        return true;
    }

    #endregion

    #endregion

    #region Constructors

    /// <summary>
    /// Construct an instance with the default memory manager (<see cref="DefaultMemoryManager.GlobalInstance"/>) and the default capacity.
    /// </summary>
    public UnmanagedQueue() : this(null)
    {
        
    }

    /// <summary>
    /// Construct an instance with the given memory manager and capacity.
    /// </summary>
    /// <param name="memoryManager">Memory manager to use</param>
    /// <param name="capacity">Initial capacity</param>
    public UnmanagedQueue(IMemoryManager memoryManager=null, int capacity=DefaultCapacity)
    {
        if (capacity < 0)
        {
            ThrowHelper.NeedNonNegIndex(nameof(capacity));
        }
        memoryManager ??= DefaultMemoryManager.GlobalInstance;
        _memoryBlock = default;
        if (capacity > 0)
        {
            _memoryBlock = memoryManager.Allocate(sizeof(Header) + (sizeof(T) * capacity));
        }
        else
        {
            _memoryBlock = memoryManager.Allocate(sizeof(Header));
        }

        EnsureInternalState(true);
        var header = _header;
        header->Count = 0;
        header->Capacity = capacity;
    }

    #endregion

    #region Privates

    ///////////////////////////////////////////////////////////////////////////////////////////////
    // 28 bytes data
    // DON'T REORDER THIS FIELDS DECLARATION

    // The memory block must ALWAYS be the first field of every UnmanagedCollection types
    private MemoryBlock _memoryBlock;       // Offset  0, length 12
    private Header* _header;                // Offset 12, length 8
    private T* _buffer;                     // Offset 20, length 8

    #endregion

    #region Inner types

    internal sealed class DebugView
    {
        #region Public APIs

        #region Properties

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public T[] Items
        {
            get
            {
                //var src = _queue._memoryBlock.MemorySegment.Slice(0, _queue.Count).ToSpan();
                var src = new Span<T>(_queue.ToArray());
                var dst = new T[src.Length];
                src.CopyTo(dst);
                return dst;
            }
        }

        #endregion

        #endregion

        #region Constructors

        public DebugView(UnmanagedQueue<T> queue)
        {
            _queue = queue;
        }

        #endregion

        #region Privates

        #region Fields

        private UnmanagedQueue<T> _queue;

        #endregion

        #endregion
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public int Count;
        public int Head;
        public int Tail;
        public int Capacity;
    }

    #endregion

    // 28 bytes data
    ///////////////////////////////////////////////////////////////////////////////////////////////

    #region Private methods

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void EnsureInternalState(bool force = false)
    {
        // Non MMF means the cached addresses are always valid
        // Note: maybe we could ignore this check in order to allow multiple instances of the same list to be used at the same time
        if (force==false && _memoryBlock.MemorySegment.IsInMMF == false)
        {
            return;
        }

        // Check if the data we've precomputed is still valid
        var header = (Header*)_memoryBlock.MemorySegment.Address;
        if (force==false && _header == header)
        {
            return;
        }
        
        _header = header;
        _buffer = (T*)(header + 1);
    }

    private void Grow(int capacity)
    {
        var header = _header;
        var buffer = this._buffer;
        var newCapacity = header->Capacity == 0 ? DefaultCapacity : 2 * header->Capacity;

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
        
        if (newCapacity < capacity) newCapacity = capacity;
        if (newCapacity < header->Count)
        {
            ThrowHelper.OutOfRange($"New Capacity {newCapacity} can't be less than actual Count {header->Count}");
        }

        var newItems = memoryManager.Allocate(headerSize + (itemSize * newCapacity));
        new Span<Header>(_header, 1).CopyTo(newItems.MemorySegment.Cast<Header>());
        if (header->Count > 0)
        {
            var src = new Span<T>(buffer, header->Capacity);
            var dst = newItems.MemorySegment.Slice(headerSize).Cast<T>().ToSpan();
            if (header->Head < header->Tail)
            {
                src.Slice(header->Head, header->Count).CopyTo(dst);
            }
            else
            {
                src.Slice(header->Head, header->Count - header->Head).CopyTo(dst);
                src.Slice(0, header->Count - header->Head).CopyTo(dst.Slice(header->Count - header->Head));
            }
        }

        memoryManager.Free(_memoryBlock);
        _memoryBlock = newItems;
        EnsureInternalState(true);
        header = _header;
        header->Head = 0;
        header->Tail = (header->Count == capacity) ? 0 : header->Count;
        header->Capacity = newCapacity;
    }

    private void MoveNext(ref int index)
    {
        var header = _header;

        // It is tempting to use the remainder operator here but it is actually much slower
        // than a simple comparison and a rarely taken branch.
        // JIT produces better code than with ternary operator ?:
        var tmp = index + 1;
        if (tmp == header->Capacity)
        {
            tmp = 0;
        }
        index = tmp;
    }

    private void ThrowForEmptyQueue()
    {
        var header = _header;
        Debug.Assert(header->Count == 0);
        ThrowHelper.EmptyQueue();
    }

    #endregion
}
