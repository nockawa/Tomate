using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;
#pragma warning disable CS9084 // Struct member returns 'this' or other instance members by reference

namespace Tomate;

// Implementation notes
// Here, what matters the most is performance, maintainability and readability are secondary.
// The type itself implements all features and the subtype Accessor is a ref struct that allows to access the list in a more efficient way, 
//  but with less safety. Code is duplicated because...I don't have the choice. The performance gain is too important to ignore.

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
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct UnmanagedStack<T> : IUnmanagedCollection where T : unmanaged
{
    #region Constants

    private const int DefaultCapacity = 8;

    #endregion

    #region Public APIs

    #region Properties

    /// <summary>
    /// Subscript operator for random access to an item in the stack
    /// </summary>
    /// <param name="index">The index of the item to retrieve, must be within the range of [0..Length-1]</param>
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
    /// Get a MemorySegment of the content of the stack
    /// </summary>
    /// <remarks>
    /// This API will only assert in debug mode if you attempt to access the content of an uninitialized instance.
    /// BEWARE: the segment may no longer be valid if you perform an operation that will resize the list.
    /// </remarks>
    public MemorySegment<T> Content
    {
        get
        {
            EnsureInternalState();
            var header = _header;
            if (header == null)
            {
                ThrowHelper.InvalidObject(null);
            }
            return new MemorySegment<T>(_buffer, header->Count);
        }
    }

    /// <summary>
    /// Get the item count
    /// </summary>
    /// <returns>
    /// The number of items in the stack or -1 if the instance is invalid.
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
    /// Access to the underlying MemoryBlock of the list, use with caution
    /// </summary>
    public MemoryBlock MemoryBlock => _memoryBlock;

    /// <summary>
    /// Access to the MemoryManager used by the list to allocate its content
    /// </summary>
    public IMemoryManager MemoryManager => _memoryBlock.MemoryManager;

    /// <summary>
    /// Get the reference counter of the instance, will return -1 if the instance is default/disposed
    /// </summary>
    public int RefCounter => _memoryBlock.IsDefault ? -1 : _memoryBlock.RefCounter;

    /// <summary>
    /// Get/set the capacity of the stack
    /// </summary>
    /// <remarks>
    /// Get accessor will return -1 if the stack is disposed/default.
    /// Set accessor will throw an exception if the new capacity is less than the actual count.
    /// </remarks>
    public int Capacity
    {
        get
        {
            EnsureInternalState();
            if (IsDefault)
            {
                return -1;
            }
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
    /// Clear the content of the list
    /// </summary>
    public void Clear()
    {
        EnsureInternalState();
        var header = _header;
        header->Count = 0;
    }

    /// <summary>
    /// Dispose the instance, see remarks
    /// </summary>
    /// <remarks>
    /// This call will decrement the reference counter by 1 and the instance will effectively be disposed if it reaches 0, otherwise it will still be usable.
    /// </remarks>
    public void Dispose()
    {
        if (IsDefault || IsDisposed)
        {
            return;
        }
        
        _memoryBlock.Dispose();
        if (_memoryBlock.IsDisposed)
        {
            _memoryBlock = default;
            EnsureInternalState(true);
        }
    }

    /// <summary>
    /// Peek the item at the top of the stack
    /// </summary>
    /// <returns>A reference to the item, will throw if the stack is empty.</returns>
    public ref T Peek()
    {
        EnsureInternalState();
        var header = _header;
        var size = header->Count - 1;

        if ((uint)size >= (uint)header->Capacity)
        {
            ThrowForEmptyStack();
        }

        return ref _buffer[size];
    }

    /// <summary>
    /// Returns the top object on the stack without removing it.  If the stack is empty, Peek throws an InvalidOperationException.
    /// </summary>
    /// <returns></returns>
    public ref T Pop()
    {
        EnsureInternalState();
        var header = _header;
        --header->Count;

        if ((uint)header->Count >= (uint)header->Capacity)
        {
            ThrowForEmptyStack();
        }

        return ref _buffer[header->Count];
    }

    /// <summary>
    /// Push and return a reference to the new item
    /// </summary>
    /// <returns>
    /// This method allocates the item in the stack and return a reference to it, it's up to the caller to set the value of the item.
    /// Don't keep the reference more than what is strictly necessary because it can be invalidated by operations that resize the content of the stack.
    /// </returns>
    public ref T Push()
    {
        EnsureInternalState();
        var header = _header;
        if (header->Count >= header->Capacity)
        {
            Grow(header->Count + 1);
            EnsureInternalState(true);
            header = _header;
        }

        return ref _buffer[header->Count++];
    }

    /// <summary>
    /// Push an item on the top of the stack
    /// </summary>
    /// <param name="item">A reference to the item to push, which is preferred from the non-reference version if your struct is big.</param>
    public void Push(ref T item)
    {
        EnsureInternalState();
        var header = _header;
        if ((uint)header->Count < (uint)header->Capacity)
        {
            _buffer[header->Count] = item;
            ++header->Count;
        }
        else
        {
            PushWithResize(ref item);
        }
    }

    /// <summary>
    /// Push an item in the stack
    /// </summary>
    /// <param name="item">The item to stack up</param>
    public void Push(T item)
    {
        EnsureInternalState();
        var header = _header;
        if ((uint)header->Count < (uint)header->Capacity)
        {
            _buffer[header->Count] = item;
            ++header->Count;
        }
        else
        {
            PushWithResize(ref item);
        }
    }

    // Copies the Stack to an array, in the same order Pop would return the items.
    public T[] ToArray()
    {
        EnsureInternalState();
        var header = _header;
        if (header->Count == 0)
        {
            return [];
        }

        var objArray = new T[header->Count];
        var i = 0;
        var buffer = _buffer;
        while (i < header->Count)
        {
            objArray[i] = buffer[header->Count - i - 1];
            i++;
        }
        return objArray;
    }

    /// <summary>
    /// Attempt to peek an item from the stack
    /// </summary>
    /// <param name="result">A reference that will contain the item at the top of the stack.</param>
    /// <returns><c>true</c> if there was an item, <c>false</c> if the stack was empty.</returns>
    // ReSharper disable once RedundantAssignment
    public bool TryPeek(ref T result)
    {
        EnsureInternalState();
        var header = _header;
        var size = header->Count - 1;

        if ((uint)size >= (uint)header->Capacity)
        {
            result = default!;
            return false;
        }
        result = ref _buffer[size];
        return true;
    }

    /// <summary>
    /// Try to pop an item from the stack
    /// </summary>
    /// <param name="result">If the stack was not empty, the item will be stored in the parameter upon return of the call</param>
    /// <returns><c>true</c> if an item was popped, <c>false</c> if the stack was empty</returns>
    public bool TryPop(out T result)
    {
        EnsureInternalState();
        var header = _header;
        --header->Count;

        if ((uint)header->Count >= (uint)header->Capacity)
        {
            result = default;
            return false;
        }

        result = _buffer[header->Count];
        return true;
    }

    #endregion

    #endregion

    #region Constructors

    /// <summary>
    /// Construct an instance with the default memory manager (<see cref="DefaultMemoryManager.GlobalInstance"/>) and the default capacity.
    /// </summary>
    public UnmanagedStack() : this(null)
    {
        
    }

    /// <summary>
    /// Construct an instance with the given memory manager and capacity.
    /// </summary>
    /// <param name="memoryManager">Memory manager to use</param>
    /// <param name="capacity">Initial capacity</param>
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
    private MemoryBlock _memoryBlock;           // Offset  0, length 12
    private Header* _header;                    // Offset 12, length 8
    private T* _buffer;                         // Offset 20, length 8

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
                var header = _stack._header;
                var buffer = (T*)(header + 1);
                var data = new Span<T>(buffer, header->Count);
                var items = new T[data.Length];
                var dest = new Span<T>(items);
                data.CopyTo(dest);
                Array.Reverse(items);
                return items;
            }
        }

        #endregion

        #endregion

        #region Constructors

        public DebugView(UnmanagedStack<T> stack)
        {
            _stack = stack;
        }

        #endregion

        #region Privates

        #region Fields

        private UnmanagedStack<T> _stack;

        #endregion

        #endregion
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public int Count;
        public int Capacity;
        private ulong _padding;
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

    // Pops an item from the top of the stack.  If the stack is empty, Pop
    // throws an InvalidOperationException.

    private void Grow(int capacity)
    {
        EnsureInternalState();
        var header = _header;
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
        
        _memoryBlock.Resize(sizeof(Header) + (itemSize * newCapacity));
        EnsureInternalState(true);
        header = _header;
        header->Capacity = newCapacity;
    }

    // Non-inline from Stack.Push to improve its code quality as uncommon path
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PushWithResize(ref T item)
    {
        EnsureInternalState();
        var header = _header;
        Debug.Assert(header->Count == header->Capacity);
        Grow(header->Count + 1);
        header = _header;
        _buffer[header->Count] = item;
        header->Count++;
    }

    private void ThrowForEmptyStack()
    {
        var header = _header;
        Debug.Assert(header->Count == 0);
        ThrowHelper.EmptyStack();
    }

    #endregion
}