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
public unsafe struct UnmanagedStack<T> : IDisposable where T : unmanaged
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
            if ((uint)index >= (uint)header->Count)
            {
                ThrowHelper.OutOfRange($"Index {index} must be less than {header->Count} and greater or equal to 0");
            }
            var buffer = (T*)(header + 1);
            return ref buffer[index];
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
            var header = _header;
            if (header == null)
            {
                ThrowHelper.InvalidObject(null);
            }
            var buffer = (T*)(header + 1);
            return new MemorySegment<T>(buffer, header->Count);
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
    /// Access to a fast accessor, which is the preferred way if there are multiple operations on the list to be done.
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
    /// Beware: setting the capacity will trigger a resize of the stack, be sure not to mix this operation with others that deals with a cached address of the
    ///  stack (like <see cref="MemoryBlock"/>, or <see cref="Accessor"/>) because the address will be invalid after the resize.
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
    
    #endregion

    #region Methods

    /// <summary>
    /// Clear the content of the list
    /// </summary>
    public void Clear()
    {
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
        _memoryBlock = default;
    }

    /// <summary>
    /// Peek the item at the top of the stack
    /// </summary>
    /// <returns>A reference to the item, will throw if the stack is empty.</returns>
    public ref T Peek()
    {
        var header = _header;
        var buffer = (T*)(header + 1);
        var size = header->Count - 1;

        if ((uint)size >= (uint)header->Capacity)
        {
            ThrowForEmptyStack();
        }

        return ref buffer[size];
    }

    /// <summary>
    /// Returns the top object on the stack without removing it.  If the stack is empty, Peek throws an InvalidOperationException.
    /// </summary>
    /// <returns></returns>
    public ref T Pop()
    {
        var header = _header;
        var buffer = (T*)(header + 1);
        --header->Count;

        if ((uint)header->Count >= (uint)header->Capacity)
        {
            ThrowForEmptyStack();
        }

        return ref buffer[header->Count];
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
        var header = _header;
        if (header->Count >= header->Capacity)
        {
            Grow(header->Count + 1);
            header = _header;
        }

        var buffer = (T*)(header + 1);
        return ref buffer[header->Count++];
    }

    /// <summary>
    /// Push an item on the top of the stack
    /// </summary>
    /// <param name="item">A reference to the item to push, which is preferred from the non-reference version if your struct is big.</param>
    public void Push(ref T item)
    {
        var header = _header;
        var buffer = (T*)(header + 1);
        if ((uint)header->Count < (uint)header->Capacity)
        {
            buffer[header->Count] = item;
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
        var header = _header;
        var buffer = (T*)(header + 1);
        if ((uint)header->Count < (uint)header->Capacity)
        {
            buffer[header->Count] = item;
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
        var header = _header;
        var buffer = (T*)(header + 1);
        if (header->Count == 0)
        {
            return [];
        }

        var objArray = new T[header->Count];
        var i = 0;
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
        var header = _header;
        var buffer = (T*)(header + 1);
        var size = header->Count - 1;

        if ((uint)size >= (uint)header->Capacity)
        {
            result = default!;
            return false;
        }
        result = ref buffer[size];
        return true;
    }

    /// <summary>
    /// Try to pop an item from the stack
    /// </summary>
    /// <param name="result">If the stack was not empty, the item will be stored in the parameter upon return of the call</param>
    /// <returns><c>true</c> if an item was popped, <c>false</c> if the stack was empty</returns>
    public bool TryPop(out T result)
    {
        var header = _header;
        var buffer = (T*)(header + 1);
        --header->Count;

        if ((uint)header->Count >= (uint)header->Capacity)
        {
            result = default;
            return false;
        }

        result = buffer[header->Count];
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
    /// Process-dependent accessor to the stack, for faster operations
    /// </summary>
    /// <remarks>
    /// The <see cref="UnmanagedStack{T}"/> type is a process independent type, meaning its instances can lie inside a MemoryMappedFile and be shared across
    ///  processes. This possibility comes with a constraint to deal with indices rather than addresses, which has a performance cost.
    /// Creating an instance of <see cref="Accessor"/> will give you a faster way to access the stack because its implementation deals with addresses rather
    ///  than indices, but it only brings a performance gain if you have multiple operations to perform on the stack.
    /// Also note that APIs don't check for the validity of the instance, it's only done during the construction of the accessor.
    /// WARNING: when using APIs of this type, you must NOT perform any operation on the stack instance itself (or other instances of this accessor),
    ///  simply because <see cref="Accessor"/> caches the addresses of the stack's content and if a resize occurs outside of this instance, the consequences
    ///  will be catastrophic.
    /// </remarks>
    [PublicAPI]
    public ref struct Accessor
    {
        #region Public APIs

        #region Properties

        /// <summary>
        /// Subscript operator for random access to an item in the stack
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
                Debug.Assert(header != null, "Can't access the header, the instance doesn't point to a valid stack");
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
        /// The number of items in the stack or -1 if the instance is invalid.
        /// </returns>
        public int Count => _header->Count;

        /// <summary>
        /// Get/set the capacity of the stack
        /// </summary>
        /// <remarks>
        /// Get accessor will return -1 if the stack is disposed/default.
        /// Set accessor will throw an exception if the new capacity is less than the actual count.
        /// Beware: setting the capacity will trigger a resize of the stack, be sure not to mix this operation with others that deals with a cached address of the
        ///  stack (like <see cref="MemoryBlock"/>, or <see cref="Accessor"/>) because the address will be invalid after the resize.
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
        /// Clear the content of the stack
        /// </summary>
        public void Clear() => _owner.Clear();

        /// <summary>
        /// Peek the item at the top of the stack
        /// </summary>
        /// <returns>A reference to the item, will throw if the stack is empty.</returns>
        public ref T Peek()
        {
            var header = _header;
            var buffer = _buffer;
            var size = header->Count - 1;

            if ((uint)size >= (uint)header->Capacity)
            {
                _owner.ThrowForEmptyStack();
            }

            return ref buffer[size];
        }

        /// <summary>
        /// Returns the top object on the stack without removing it.  If the stack is empty, Peek throws an InvalidOperationException.
        /// </summary>
        /// <returns></returns>
        public ref T Pop()
        {
            var header = _header;
            var buffer = _buffer;
            --header->Count;

            if ((uint)header->Count >= (uint)header->Capacity)
            {
                _owner.ThrowForEmptyStack();
            }

            return ref buffer[header->Count];
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
            var header = _header;
            if (header->Count >= header->Capacity)
            {
                _owner.Grow(header->Count + 1);
                header = _header;
                _header = header;
                _buffer = (T*)(header + 1);
            }

            var buffer = (T*)(header + 1);
            return ref buffer[header->Count++];
        }

        /// <summary>
        /// Push an item on the top of the stack
        /// </summary>
        /// <param name="item">A reference to the item to push, which is preferred from the non-reference version if your struct is big.</param>
        public void Push(ref T item)
        {
            var header = _header;
            var buffer = _buffer;
            if ((uint)header->Count < (uint)header->Capacity)
            {
                buffer[header->Count] = item;
                ++header->Count;
            }
            else
            {
                _owner.PushWithResize(ref item);
                _header = header;
                _buffer = (T*)(header + 1);
            }
        }

        /// <summary>
        /// Push an item in the stack
        /// </summary>
        /// <param name="item">The item to stack up</param>
        public void Push(T item)
        {
            var header = _header;
            var buffer = _buffer;
            if ((uint)header->Count < (uint)header->Capacity)
            {
                buffer[header->Count] = item;
                ++header->Count;
            }
            else
            {
                _owner.PushWithResize(ref item);
                _header = header;
                _buffer = (T*)(header + 1);
            }
        }

        // Copies the Stack to an array, in the same order Pop would return the items.
        public T[] ToArray() => _owner.ToArray();

        /// <summary>
        /// Attempt to peek an item from the stack
        /// </summary>
        /// <param name="result">A reference that will contain the item at the top of the stack.</param>
        /// <returns><c>true</c> if there was an item, <c>false</c> if the stack was empty.</returns>
        // ReSharper disable once RedundantAssignment
        public bool TryPeek(ref T result)
        {
            var header = _header;
            var buffer = _buffer;
            var size = header->Count - 1;

            if ((uint)size >= (uint)header->Capacity)
            {
                result = default!;
                return false;
            }
            result = ref buffer[size];
            return true;
        }

        /// <summary>
        /// Try to pop an item from the stack
        /// </summary>
        /// <param name="result">If the stack was not empty, the item will be stored in the parameter upon return of the call</param>
        /// <returns><c>true</c> if an item was popped, <c>false</c> if the stack was empty</returns>
        public bool TryPop(out T result)
        {
            var header = _header;
            var buffer = _buffer;
            --header->Count;

            if ((uint)header->Count >= (uint)header->Capacity)
            {
                result = default;
                return false;
            }

            result = buffer[header->Count];
            return true;
        }
        
        #endregion

        #endregion

        #region Constructors

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public Accessor(ref UnmanagedStack<T> owner)
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

        private ref UnmanagedStack<T> _owner;

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

    #region Private methods

    // Pops an item from the top of the stack.  If the stack is empty, Pop
    // throws an InvalidOperationException.

    private void Grow(int capacity)
    {
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
        header = _header;
        header->Capacity = newCapacity;
    }

    // Non-inline from Stack.Push to improve its code quality as uncommon path
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PushWithResize(ref T item)
    {
        var header = _header;
        Debug.Assert(header->Count == header->Capacity);
        Grow(header->Count + 1);
        header = _header;
        var buffer = (T*)(header + 1);
        buffer[header->Count] = item;
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