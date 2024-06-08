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
public unsafe struct UnmanagedQueue<T> : IDisposable where T : unmanaged
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
    /// If you have multiple operations to perform on the list, consider using the <see cref="FastAccessor"/> property which is faster.
    /// </remarks>
    public ref T this[int index]
    {
        get
        {
            var header = _header;
            if (header == null)
            {
                ThrowHelper.InvalidObject(null);
            }
            var buffer = (T*)(header + 1);
            if ((uint)index >= (uint)header->Count)
            {
                ThrowHelper.OutOfRange($"Index {index} must be less than {header->Count} and greater or equal to 0");
            }
            return ref buffer[index];
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
    /// Access to a fast accessor, which is the preferred way if there are multiple operations on the queue to be done.
    /// </summary>
    /// <remarks>
    /// The accessor will fetch the addresses to work with, which gives an additional performance boost when multiple operations are to perform.
    /// As it is a ref struct, it can't be stored in a field, so you must use it in the same method where you get it.
    /// </remarks>
    public Accessor FastAccessor
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get => new(ref this);
    }

    /// <summary>
    /// Get/set the capacity of the queue
    /// </summary>
    /// <remarks>
    /// Get accessor will return -1 if the queue is disposed/default.
    /// Set accessor will throw an exception if the new capacity is less than the actual count.
    /// Beware: setting the capacity will trigger a resize of the queue, be sure not to mix this operation with others that deals with a cached address of the
    ///  queue (like <see cref="MemoryBlock"/>, or <see cref="Accessor"/>) because the address will be invalid after the resize.
    /// </remarks>
    public int Capacity
    {
        get
        {
            if (IsDefault)
            {
                return -1;
            }
            var header = _header;
            return header->Capacity;
        }
        set 
        {
            // Cache the value, because unfortunately accessing the address of the memory block is not that fast compare to what we need
            var header = _header;
            if (value < header->Count)
            {
                ThrowHelper.OutOfRange($"New Capacity {value} can't be less than actual Count {header->Count}");
            }
            if (value != header->Capacity)
            {
                _memoryBlock.Resize(sizeof(Header) + (sizeof(T) * value));
                header = _header;
                header->Capacity = value;
            }
        }
    }
    
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

    #endregion

    #region Methods

    /// <summary>
    /// Clear the content of the queue
    /// </summary>
    public void Clear()
    {
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
        var header = _header;
        var buffer = (T*)(header + 1);
        var head = header->Head;

        if (header->Count == 0)
        {
            ThrowForEmptyQueue();
        }

        MoveNext(ref header->Head);
        header->Count--;
        return ref buffer[head];
    }

    /// <summary>
    /// Dispose the instance, see remarks
    /// </summary>
    /// <remarks>
    /// This call will decrement the reference counter by 1 and the instance will effectively be disposed if it reaches 0, otherwise it will still be usable.
    /// </remarks>
    public void Dispose()
    {
        if (IsDefault)
        {
            return;
        }
        _memoryBlock.Dispose();
        //_memoryManager.Free(_memoryBlock);
        //_size = -1;
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
        var header = _header;
        if (header->Count == header->Capacity)
        {
            Grow(header->Count + 1);
            header = _header;
        }

        var curTail = header->Tail;
        MoveNext(ref header->Tail);
        header->Count++;
        var buffer = (T*)(header + 1);
        return ref buffer[curTail];
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
        var header = _header;
        if (header->Count == header->Capacity)
        {
            Grow(header->Count + 1);
            header = _header;
        }

        var buffer = (T*)(header + 1);
        buffer[header->Tail] = item;
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
        var header = _header;
        if (header->Count == header->Capacity)
        {
            Grow(header->Count + 1);
            header = _header;
        }

        var buffer = (T*)(header + 1);
        buffer[header->Tail] = item;
        MoveNext(ref header->Tail);
        header->Count++;
    }

    /// <summary>
    /// Peek the item at the head of the queue
    /// </summary>
    /// <returns>A reference to the item, will throw if the queue is empty.</returns>
    public ref T Peek()
    {
        var header = _header;
        var buffer = (T*)(header + 1);
        if (header->Count == 0)
        {
            ThrowForEmptyQueue();
        }

        return ref buffer[header->Head];
    }

    // Iterates over the objects in the queue, returning an array of the
    // objects in the Queue, or an empty array if the queue is empty.
    // The order of elements in the array is first in to last in, the same
    // order produced by successive calls to Dequeue.
    public T[] ToArray()
    {
        var header = _header;
        var buffer = (T*)(header + 1);
        if (header->Count == 0)
        {
            return Array.Empty<T>();
        }

        var arr = new T[header->Count];

        var src = new Span<T>(buffer, header->Capacity);
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
        var header = _header;
        var buffer = (T*)(header + 1);
        var head = header->Head;
        if (header->Count == 0)
        {
            result = default;
            return false;
        }

        result = buffer[head];
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
        var header = _header;
        var buffer = (T*)(header + 1);
        if (header->Count == 0)
        {
            result = default;
            return false;
        }

        result = buffer[header->Head];
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

        //_buffer = _memoryBlock.MemorySegment.Address;
        var header = _header;
        header->Count = 0;
        header->Capacity = capacity;
    }

    #endregion

    #region Privates

    // Unfortunately this access is not as fast as we'd like to. We can't store the address of the memory block because it must remain process independent.
    // This is why we have a FastAccessor property that will give you a ref struct to work with and a faster access.
    private Header* _header
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get => (Header*)_memoryBlock.MemorySegment.Address;
    }

    #region Fields

    private MemoryBlock _memoryBlock;

    #endregion

    #endregion

    #region Inner types

    /// <summary>
    /// Process-dependent accessor to the queue, for faster operations
    /// </summary>
    /// <remarks>
    /// The <see cref="UnmanagedQueue{T}"/> type is a process independent type, meaning its instances can lie inside a MemoryMappedFile and be shared across
    ///  processes. This possibility comes with a constraint to deal with indices rather than addresses, which has a performance cost.
    /// Creating an instance of <see cref="Accessor"/> will give you a faster way to access the queue because its implementation deals with addresses rather
    ///  than indices, but it only brings a performance gain if you have multiple operations to perform on the queue.
    /// Also note that APIs don't check for the validity of the instance, it's only done during the construction of the accessor.
    /// WARNING: when using APIs of this type, you must NOT perform any operation on the queue instance itself (or other instances of this accessor),
    ///  simply because <see cref="Accessor"/> caches the addresses of the queue's content and if a resize occurs outside of this instance, the consequences
    ///  will be catastrophic.
    /// </remarks>
    [PublicAPI]
    public ref struct Accessor
    {
        #region Public APIs

        #region Properties

        /// <summary>
        /// Subscript operator for random access to an item in the queue
        /// </summary>
        /// <param name="index">The index of the item to retrieve, must be within the range of [0..Length-1]</param>
        /// <remarks>
        /// This API checks for the bounds and will throw if the index is incorrect.
        /// </remarks>
        public ref T this[int index]
        {
            get
            {
                var header = _header;
                Debug.Assert(header != null, "Can't access the header, the instance doesn't point to a valid queue");
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
        public int Count => _header->Count;

        /// <summary>
        /// Get/set the capacity of the queue
        /// </summary>
        /// <remarks>
        /// Get accessor will return -1 if the queue is disposed/default.
        /// Set accessor will throw an exception if the new capacity is less than the actual count.
        /// Beware: setting the capacity will trigger a resize of the queue, be sure not to mix this operation with others that deals with a cached address of the
        ///  queue (like <see cref="MemoryBlock"/>, or <see cref="Accessor"/>) because the address will be invalid after the resize.
        /// </remarks>
        public int Capacity
        {
            get => _owner.Capacity;
            set
            {
                _owner.Capacity = value;
                _header = _owner._header;
                _buffer = (T*)(_header + 1);
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Clear the content of the queue
        /// </summary>
        public void Clear() => _owner.Clear();

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
            var header = _header;
            var buffer = _buffer;
            var head = header->Head;

            if (header->Count == 0)
            {
                _owner.ThrowForEmptyQueue();
            }

            _owner.MoveNext(ref header->Head);
            header->Count--;
            return ref buffer[head];
        }
        
        /// <summary>
        /// Enqueue and return a reference to the new item
        /// </summary>
        /// <returns>
        /// This method allocate the item in the queue and return a reference to it, it's up to the caller to set the value of the item.
        /// Don't keep the reference more than what is strictly necessary because it can be invalidated by operations that resize the content of the queue.
        /// </returns>
        public ref T Enqueue()
        {
            var header = _header;
            if (header->Count == header->Capacity)
            {
                _owner.Grow(header->Count + 1);
                header = _owner._header;
                _header = header;
                _buffer = (T*)(header + 1);
            }

            var curTail = header->Tail;
            _owner.MoveNext(ref header->Tail);
            header->Count++;
            var buffer = (T*)(header + 1);
            return ref buffer[curTail];
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
            var header = _header;
            if (header->Count == header->Capacity)
            {
                _owner.Grow(header->Count + 1);
                header = _header;
                _header = header;
                _buffer = (T*)(header + 1);
            }

            var buffer = (T*)(header + 1);
            buffer[header->Tail] = item;
            _owner.MoveNext(ref header->Tail);
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
            var header = _header;
            if (header->Count == header->Capacity)
            {
                _owner.Grow(header->Count + 1);
                header = _header;
                _header = header;
                _buffer = (T*)(header + 1);
            }

            var buffer = (T*)(header + 1);
            buffer[header->Tail] = item;
            _owner.MoveNext(ref header->Tail);
            header->Count++;
        }

        /// <summary>
        /// Peek the item at the head of the queue
        /// </summary>
        /// <returns>A reference to the item, will throw if the queue is empty.</returns>
        public ref T Peek()
        {
            var header = _header;
            var buffer = (T*)(header + 1);
            if (header->Count == 0)
            {
                _owner.ThrowForEmptyQueue();
            }

            return ref buffer[header->Head];
        }
        
        #endregion

        #endregion

        #region Constructors

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public Accessor(ref UnmanagedQueue<T> owner)
        {
            if (owner.IsDefault)
            {
                ThrowHelper.InvalidObject(null);
            }
            _owner = ref owner;
            _header = (Header*)owner._memoryBlock.MemorySegment.Address;
            _buffer = (T*)(_header + 1);
        }

        #endregion

        #region Privates

        private Header* _header;
        private T* _buffer;

        private ref UnmanagedQueue<T> _owner;

        #endregion
    }

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

    #region Private methods

    private void Grow(int capacity)
    {
        var header = _header;
        var buffer = (T*)(header + 1);
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
        header = _header;
        header->Head = 0;
        header->Tail = (header->Count == capacity) ? 0 : header->Count;
        header->Capacity = newCapacity; //_memoryBlock.MemorySegment.Length;
        //_buffer = _memoryBlock.MemorySegment.Address;
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
