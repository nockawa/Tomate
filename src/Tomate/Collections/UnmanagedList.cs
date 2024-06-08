using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
/// An unmanaged list of unmanaged items
/// </summary>
/// <typeparam name="T">The type of items to store</typeparam>
/// <remarks>
/// Be sure to read the overview documentation to learn how to use this type.
/// This type is MemoryMappedFile friendly, meaning you can allocate instances of this type with an <see cref="MemoryManagerOverMMF"/> as memory manager.
/// This comes with a limitation where this type can't store addresses, because a MMF is a cross process object and each process has its own address space.
/// The result is a degraded performance, you can alleviate this by relying on the <see cref="Accessor"/> type when you have multiple operations to perform.
/// </remarks>
[DebuggerTypeProxy(typeof(UnmanagedList<>.DebugView))]
[DebuggerDisplay("Count = {Count}")]
[PublicAPI]
public unsafe struct UnmanagedList<T> : IUnmanagedFacade, IRefCounted where T : unmanaged
{
    #region Constants

    private const int DefaultCapacity = 4;

    #endregion

    #region Public APIs

    #region Properties

    /// <summary>
    /// Subscript operator for random access to an item in the list
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
            if ((uint)index >= (uint)header->Count)
            {
                ThrowHelper.OutOfRange($"Index {index} must be less than {header->Count} and greater or equal to 0");
            }
            var items = (T*)(header + 1);
            return ref items[index];
        }
    }

    /// <summary>
    /// Get a MemorySegment of the content of the list
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
            var items = (T*)(header + 1);
            return new(items, header->Count);
        }
    }

    /// <summary>
    /// Get the item count
    /// </summary>
    /// <returns>
    /// The number of items in the list or -1 if the instance is invalid.
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
    public bool IsDisposed => _memoryBlock.IsDefault;

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
    /// Get/set the capacity of the list
    /// </summary>
    /// <remarks>
    /// Get accessor will return -1 if the list is disposed/default.
    /// Set accessor will throw an exception if the new capacity is less than the actual count.
    /// Beware: setting the capacity will trigger a resize of the list, be sure not to mix this operation with others that deals with a cached address of the
    ///  list (like <see cref="MemoryBlock"/>, or <see cref="Accessor"/>) because the address will be invalid after the resize.
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
            return (int)header->Capacity;
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
                header->Capacity = (uint)value;
            }
        }
    }

    #endregion

    #region Methods

    /// <summary>
    /// Add an item to the list
    /// </summary>
    /// <param name="item">The item to add</param>
    /// <returns>
    /// The index of the added item
    /// </returns>
    /// <remarks>
    /// Will throw <see cref="InvalidObjectException"/> if the instance is default or disposed.
    /// If you have multiple operations to perform on the list, consider using the <see cref="FastAccessor"/> property which is faster.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public int Add(T item)
    {
        var header = _header;
        if (header == null)
        {
            ThrowHelper.InvalidObject(null);
        }
        var items = (T*)(header + 1);
        var res = header->Count;
        if ((uint)res < header->Capacity)
        {
            items[header->Count++] = item;
        }
        else
        {
            AddWithResize(item);
        }
        
        return res;
    }

    /// <summary>
    /// Return the ref of the added element, beware, read remarks
    /// </summary>
    /// <returns>
    /// The reference to the added element.
    /// </returns>
    /// <remarks>
    /// You must be sure any other operation on this list won't trigger a resize of its content for the time you use the ref. Otherwise the ref
    ///  will point to a incorrect address and corruption will most likely to occur. Use this API with great caution.
    /// Will throw <see cref="InvalidObjectException"/> if the instance is default or disposed.
    /// If you have multiple operations to perform on the list, consider using the <see cref="FastAccessor"/> property which is faster.
    /// </remarks>
    public ref T AddInPlace()
    {
        var header = _header;
        if (header == null)
        {
            ThrowHelper.InvalidObject(null);
        }
        if (header->Count == header->Capacity)
        {
            Grow(header->Count + 1);
            header = _header;
        }

        var items = (T*)(header + 1);
        return ref items[header->Count++];
    }

    /// <summary>
    /// Add a reference to the instance
    /// </summary>
    /// <returns>The new value of the reference counter</returns>
    /// <remarks>
    /// A matching call to <see cref="Dispose"/> will have to be made to release the reference.
    /// </remarks>
    public int AddRef() => _memoryBlock.AddRef();

    /// <summary>
    /// Clear the content of the list
    /// </summary>
    public void Clear()
    {
        var header = _header;
        header->Count = 0;
    }

    /// <summary>
    /// Copy the content of the list to an array, starting at the given index
    /// </summary>
    /// <param name="items">The array that will receive the list's items</param>
    /// <param name="i">The index in the array to copy the first element</param>
    /// <remarks>
    /// Will throw is the array's portion (delimited by <param name="i" /> and its length) is too small to contain all the items.
    /// </remarks>
    public void CopyTo(T[] items, int i)
    {
        var span = new Span<T>(items, i, items.Length - i);
        var srcItems = (T*)(_header + 1);
        new Span<T>(srcItems, span.Length).CopyTo(span); 
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
        }
    }

    /// <summary>
    /// Enumerator access for <c>foreach</c>
    /// </summary>
    /// <returns>The enumerator, an instance of the <see cref="Enumerator"/> type</returns>
    public Enumerator GetEnumerator() => new(this);

    /// <summary>
    /// Return the index of the given item in the list 
    /// </summary>
    /// <param name="item">The item to look for.</param>
    /// <returns>The index or <c>-1</c> if there's no such item</returns>
    /// <remarks>
    /// Will throw <see cref="InvalidObjectException"/> if the instance is default or disposed.
    /// If you have multiple operations to perform on the list, consider using the <see cref="FastAccessor"/> property which is faster.
    /// </remarks>
    public int IndexOf(T item)
    {
        var header = _header;
        if (header == null)
        {
            ThrowHelper.InvalidObject(null);
        }
        var items = (T*)(header + 1);
        if (typeof(T) == typeof(int))
        {
            var src = Unsafe.As<T, int>(ref item);
            var buffer = (int*)items;

            var l = header->Count;
            for (int i = 0; i < l; i++)
            {
                if (buffer[i] == src)
                {
                    return i;
                }
            }

            return -1;
        }

        if (typeof(T) == typeof(long))
        {
            var src = Unsafe.As<T, long>(ref item);
            var buffer = (long*)items;

            var l = header->Count;
            for (int i = 0; i < l; i++)
            {
                if (buffer[i] == src)
                {
                    return i;
                }
            }

            return -1;
        }

        if (typeof(T) == typeof(short))
        {
            var src = Unsafe.As<T, short>(ref item);
            var buffer = (short*)items;

            var l = header->Count;
            for (int i = 0; i < l; i++)
            {
                if (buffer[i] == src)
                {
                    return i;
                }
            }

            return -1;
        }

        if (typeof(T) == typeof(byte))
        {
            var src = Unsafe.As<T, byte>(ref item);
            var buffer = (byte*)items;

            var l = header->Count;
            for (int i = 0; i < l; i++)
            {
                if (buffer[i] == src)
                {
                    return i;
                }
            }

            return -1;
        }

        {
            var src = MemoryMarshal.CreateReadOnlySpan(ref item, 1);
            var span = new Span<T>(items, header->Count);
            var l = header->Count;

            for (var i = 0; i < l; i++)
            {
                if (span.Slice(i, 1).SequenceEqual(src))
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// Inserts an element into this list at a given index. The size of the list is increased by one. If required, the capacity of the list is doubled
    ///  before inserting the new element. 
    /// </summary>
    /// <param name="index">The index of the position to insert the given item</param>
    /// <param name="item">The item to insert</param>
    /// <remarks>
    /// Will throw <see cref="InvalidObjectException"/> if the instance is default or disposed.
    /// If you have multiple operations to perform on the list, consider using the <see cref="FastAccessor"/> property which is faster.
    /// </remarks>
    public void Insert(int index, T item)
    {
        // Note that insertions at the end are legal.
        var header = _header;
        if (header == null)
        {
            ThrowHelper.InvalidObject(null);
        }
        if ((uint)index > (uint)header->Count)
        {
            ThrowHelper.OutOfRange($"Can't insert, given index {index} is greater than Count {header->Count}.");
        }

        if (header->Count == header->Capacity)
        {
            Grow(header->Count + 1);
            header = _header;
        }

        var items = (T*)(header + 1);
        if (index < header->Count)
        {
            var span = new Span<T>(items, header->Count + 1);
            span.Slice(index, header->Count - index).CopyTo(span.Slice(index + 1));
        }
        items[index] = item;
        ++header->Count;
    }

    /// <summary>
    /// Remove the given item from the list
    /// </summary>
    /// <param name="item">The item to remove</param>
    /// <returns><c>true</c> if there was such item, <c>false</c> if we couldn't find anything to remove</returns>
    /// <remarks>
    /// Remove will only remove the first occurrence of the item in the list, the whole content succeeding the item will be shifted to fill the gap.
    /// Will throw <see cref="InvalidObjectException"/> if the instance is default or disposed.
    /// If you have multiple operations to perform on the list, consider using the <see cref="FastAccessor"/> property which is faster.
    /// </remarks>
    public bool Remove(T item)
    {
        int index = IndexOf(item);
        if (index >= 0)
        {
            RemoveAt(index);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Remove the item at the given index
    /// </summary>
    /// <param name="index">The index to remove the item at, must be a valid range or <see cref="IndexOutOfRangeException"/> will be thrown</param>
    /// <remarks>
    /// Will throw <see cref="InvalidObjectException"/> if the instance is default or disposed.
    /// If you have multiple operations to perform on the list, consider using the <see cref="FastAccessor"/> property which is faster.
    /// </remarks>
    public void RemoveAt(int index)
    {
        var header = _header;
        if (header == null)
        {
            ThrowHelper.InvalidObject(null);
        }
        if ((uint)index >= (uint)header->Count)
        {
            ThrowHelper.OutOfRange($"Can't remove, given index {index} is greater than Count {header->Count}.");
        }
        if (index < header->Count)
        {
            var items = (T*)(header + 1);
            var span = new Span<T>(items, header->Count);
            span.Slice(index + 1).CopyTo(span.Slice(index));
            header->Count--;
        }
    }

    #endregion

    #endregion

    #region Constructors

    /// <summary>
    /// Create an instance with the <see cref="DefaultMemoryManager.GlobalInstance"/>.
    /// </summary>
    public UnmanagedList() : this(null)
    {
        
    }

    /// <summary>
    /// Create an instance of the UnmanagedList
    /// </summary>
    /// <param name="memoryManager">The Memory Manager to use, if <c>null</c> the <see cref="DefaultMemoryManager.GlobalInstance"/> will be used.</param>
    /// <param name="initialCapacity">Initial capacity of the list, the number of items it can hold without resizing.</param>
    public UnmanagedList(IMemoryManager memoryManager = null, int initialCapacity = DefaultCapacity)
    {
        if (initialCapacity < 0)
        {
            ThrowHelper.OutOfRange("Initial Capacity can't be a negative number");
        }
        memoryManager ??= DefaultMemoryManager.GlobalInstance;
        _memoryBlock = default;
        _memoryBlock = memoryManager.Allocate(sizeof(Header) + (sizeof(T) * initialCapacity));
        var header = _header;
        header->Count = 0;
        header->Capacity = (uint)initialCapacity;
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

    private MemoryBlock _memoryBlock;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AddWithResize(T item)
    {
        var header = _header;
        Debug.Assert(header->Count == header->Capacity);
        Grow(header->Count + 1);
        header = _header;
        var items = (T*)(header + 1);
        items[header->Count++] = item;
    }

    private void Grow(int capacity)
    {
        var header = _header;
        Debug.Assert(header->Capacity < capacity);

        var newCapacity = header->Capacity == 0 ? DefaultCapacity : (int)(2 * header->Capacity);

        // Check if the new capacity exceed the size of the block we can allocate
        var maxAllocationLength = (uint)_memoryBlock.MemoryManager.MaxAllocationLength;
        var headerSize = sizeof(Header);
        if ((uint)(headerSize + newCapacity * sizeof(T)) > maxAllocationLength)
        {
            newCapacity = (int)(maxAllocationLength - headerSize) / sizeof(T);

            if (newCapacity < capacity)
            {
                ThrowHelper.InvalidAllocationSize($"The requested capacity {capacity} is greater than the maximum allowed capacity {newCapacity}. Use a Memory Manager with a greater PMB size");
            }
        }

        Capacity = newCapacity;
    }

    #endregion

    #region Inner types

    /// <summary>
    /// Process-dependent accessor to the list, for faster operations
    /// </summary>
    /// <remarks>
    /// The <see cref="UnmanagedList{T}"/> type is a process independent type, meaning its instances can lie inside a MemoryMappedFile and be shared across
    ///  processes. This possibility comes with a constraint to deal with indices rather than addresses, which has a performance cost.
    /// Creating an instance of <see cref="Accessor"/> will give you a faster way to access the list because its implementation deals with addresses rather
    ///  than indices, but it only brings a performance gain if you have multiple operations to perform on the list.
    /// Also note that APIs don't check for the validity of the instance, it's only done during the construction of the accessor.
    /// WARNING: when using APIs of this type, you must NOT perform any operation on the list instance itself (or other instances of this accessor),
    ///  simply because <see cref="Accessor"/> caches the addresses of the list's content and if a resize occurs outside of this instance, the consequences
    ///  will be catastrophic.
    /// </remarks>
    [PublicAPI]
    [DebuggerTypeProxy(typeof(UnmanagedList<>.Accessor.AccessorDebugView))]
    [DebuggerDisplay("Count = {Count}")]
    public ref struct Accessor
    {
        #region Public APIs

        #region Properties

        /// <summary>
        /// Subscript operator for random access to an item in the list
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
                Debug.Assert(header != null, "Can't access the header, the instance doesn't point to a valid list");
                if ((uint)index >= (uint)header->Count)
                {
                    ThrowHelper.OutOfRange($"Index {index} must be less than {header->Count} and greater or equal to 0");
                }
                return ref _items[index];
            }
        }

        /// <summary>
        /// Get a MemorySegment of the content of the list
        /// </summary>
        /// <remarks>
        /// BEWARE: the segment may no longer be valid if you perform an operation that will resize the list.
        /// </remarks>
        public MemorySegment<T> Content => new(_items, _header->Count);

        /// <summary>
        /// Get the item count
        /// </summary>
        /// <returns>
        /// The number of items in the list or -1 if the instance is invalid.
        /// </returns>
        public int Count => _header->Count;

        /// <summary>
        /// Get/set the capacity of the list
        /// </summary>
        /// <remarks>
        /// Get accessor will return -1 if the list is disposed/default.
        /// Set accessor will throw an exception if the new capacity is less than the actual count.
        /// Beware: setting the capacity will trigger a resize of the list, be sure not to mix this operation with others that deals with a cached address of the
        ///  list (like <see cref="MemoryBlock"/>, or <see cref="Accessor"/>) because the address will be invalid after the resize.
        /// </remarks>
        public int Capacity
        {
            get => _owner.Capacity;
            set
            {
                _owner.Capacity = value;
                _header = _owner._header;
                _items = (T*)(_header + 1);
            }
        }

        #endregion

        #region Methods

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public int Add(T item)
        {
            var header = _header;
            Debug.Assert(header != null, "Can't access the header, the instance doesn't point to a valid list");
            var res = header->Count;
            if ((uint)res < header->Capacity)
            {
                _items[header->Count++] = item;
            }
            else
            {
                _owner.AddWithResize(item);
                _header = _owner._header;
                _items = (T*)(_header + 1);
            }
            
            return res;
        }

        /// <summary>
        /// Return the ref of the added element, beware, read remarks
        /// </summary>
        /// <returns>
        /// The reference to the added element.
        /// </returns>
        /// <remarks>
        /// You must be sure any other operation on this list won't trigger a resize of its content for the time you use the ref. Otherwise the ref
        ///  will point to a incorrect address and corruption will most likely to occur. Use this API with great caution.
        /// </remarks>
        public ref T AddInPlace()
        {
            var header = _header;
            if (header->Count == header->Capacity)
            {
                _owner.Grow(header->Count + 1);
                _header = _owner._header;
                _items = (T*)(_header + 1);
                header = _header;
            }

            return ref _items[header->Count++];
        }

        /// <summary>
        /// Clear the content of the list
        /// </summary>
        public void Clear() => _owner.Clear();

        /// <summary>
        /// Copy the content of the list to an array, starting at the given index
        /// </summary>
        /// <param name="items">The array that will receive the list's items</param>
        /// <param name="i">The index in the array to copy the first element</param>
        /// <remarks>
        /// Will throw is the array's portion (delimited by <param name="i" /> and its length) is too small to contain all the items.
        /// </remarks>
        public void CopyTo(T[] items, int i)
        {
            _owner.CopyTo(items, i);
        }

        /// <summary>
        /// Enumerator access for <c>foreach</c>
        /// </summary>
        /// <returns>The enumerator, an instance of the <see cref="Enumerator"/> type</returns>
        public Enumerator GetEnumerator() => new(_owner);

        public int IndexOf(T item) => _owner.IndexOf(item);
        public void Insert(int index, T item)
        {
            // Note that insertions at the end are legal.
            var header = _owner._header;
            if ((uint)index > (uint)header->Count)
            {
                ThrowHelper.OutOfRange($"Can't insert, given index {index} is greater than Count {header->Count}.");
            }

            if (header->Count == header->Capacity)
            {
                _owner.Grow(header->Count + 1);
                header = _owner._header;
                _header = header;
                _items = (T*)(header + 1);
            }

            var items = _items;
            if (index < header->Count)
            {
                var span = new Span<T>(items, header->Count + 1);
                span.Slice(index, header->Count - index).CopyTo(span.Slice(index + 1));
            }
            items[index] = item;
            ++header->Count;
        }

        public bool Remove(T item) => _owner.Remove(item);
        public void RemoveAt(int index) => _owner.RemoveAt(index);

        #endregion

        #endregion

        #region Constructors

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public Accessor(ref UnmanagedList<T> owner)
        {
            if (owner.IsDefault)
            {
                ThrowHelper.InvalidObject(null);
            }
            _owner = ref owner;
            _header = (Header*)owner._memoryBlock.MemorySegment.Address;
            _items = (T*)(_header + 1);
        }

        #endregion

        #region Privates

        private Header* _header;
        private T* _items;

        private ref UnmanagedList<T> _owner;

        #endregion

        [ExcludeFromCodeCoverage]
        internal sealed class AccessorDebugView
        {
            #region Public APIs

            #region Properties

            [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
            public T[] Items
            {
                get
                {
                    var items = new T[_data.Length];
                    var dest = new Span<T>(items);
                    _data.ToSpan().CopyTo(dest);
                    return items;
                }
            }

            #endregion

            #endregion

            #region Constructors

            public AccessorDebugView(UnmanagedList<T>.Accessor accessor)
            {
                _data = accessor.Content;
            }

            #endregion

            #region Privates

            private readonly MemorySegment<T> _data;

            #endregion
        }
        
    }

    [ExcludeFromCodeCoverage]
    internal sealed class DebugView
    {
        #region Public APIs

        #region Properties

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public T[] Items
        {
            get
            {
                var items = new T[_data.Length];
                var dest = new Span<T>(items);
                _data.ToSpan().CopyTo(dest);
                return items;
            }
        }

        #endregion

        #endregion

        #region Constructors

        public DebugView(UnmanagedList<T> list)
        {
            _data = list.Content;
        }

        #endregion

        #region Privates

        private readonly MemorySegment<T> _data;

        #endregion
    }

    public ref struct Enumerator
    {
        #region Public APIs

        #region Properties

        public ref T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            get => ref _span[_index];
        }

        #endregion

        #region Methods

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool MoveNext()
        {
            int index = _index + 1;
            if (index < _span.Length)
            {
                _index = index;
                return true;
            }

            return false;
        }

        #endregion

        #endregion

        #region Constructors

        public Enumerator(UnmanagedList<T> owner)
        {
            var items = (T*)(owner._header + 1);
            _span = new(items, owner.Count);
            _index = -1;
        }

        #endregion

        #region Fields

        private readonly Span<T> _span;
        private int _index;

        #endregion
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Header
    {
        public uint Capacity;
        public int Count;
        public ulong _padding;      // We want Header to be 16 bytes to be aligned with a cache line
    }

    #endregion
}