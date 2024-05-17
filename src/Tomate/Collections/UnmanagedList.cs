using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Tomate;

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

    public ref T this[int index]
    {
        get
        {
            var header = _header;
            if ((uint)index >= (uint)header->Count)
            {
                ThrowHelper.OutOfRange($"Index {index} must be less than {header->Count} and greater or equal to 0");
            }
            var items = (T*)(header + 1);
            return ref items[index];
        }
    }

    public MemorySegment<T> Content
    {
        get
        {
            var header = _header;
            var items = (T*)(header + 1);
            return new(items, header->Count);
        }
    }

    public int Count
    {
        get
        {
            var header = _header;
            return header->Count;
        }
    }

    public bool IsDefault => _memoryBlock.IsDefault;
    public bool IsDisposed
    {
        get
        {
            var header = _header;
            return header->Count < 0;
        }
    }

    public MemoryBlock MemoryBlock => _memoryBlock;

    public IMemoryManager MemoryManager => _memoryBlock.IsDefault ? null : _memoryBlock.MemoryManager;

    public int RefCounter => _memoryBlock.RefCounter;

    public int Capacity
    {
        get
        {
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

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public int Add(T item)
    {
        var header = _header;
        var items = (T*)(header + 1);
        var res = header->Count;
        if ((uint)res < header->Capacity)
        {
            items[header->Count++] = item;
        }
        else
        {
            if (IsDisposed)
            {
                ThrowHelper.ObjectDisposed("No name", "Can't add to a disposed instance");
            }
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
    /// </remarks>
    public ref T AddInPlace()
    {
        var header = _header;
        if (header->Count == header->Capacity)
        {
            Grow(header->Count + 1);
            header = _header;
        }

        var items = (T*)(header + 1);
        return ref items[header->Count++];
    }

    public int AddRef() => _memoryBlock.AddRef();

    public void Clear()
    {
        var header = _header;
        header->Count = 0;
    }

    // Non-inline from List.Add to improve its code quality as uncommon path
    public void CopyTo(T[] items, int i)
    {
        var span = new Span<T>(items, i, items.Length - i);
        var srcItems = (T*)(_header + 1);
        new Span<T>(srcItems, span.Length).CopyTo(span); 
    }

    public void Dispose()
    {
        if (IsDefault || IsDisposed)
        {
            return;
        }
        _memoryBlock.Dispose();
        _memoryBlock = default;
    }

    public Enumerator GetEnumerator() => new(this);

    public int IndexOf(T item)
    {
        var header = _header;
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

    // Inserts an element into this list at a given index. The size of the list
    // is increased by one. If required, the capacity of the list is doubled
    // before inserting the new element.
    public void Insert(int index, T item)
    {
        // Note that insertions at the end are legal.
        var header = _header;
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
            var span = new Span<T>(items, header->Count);
            span.Slice(index, header->Count - index).CopyTo(span.Slice(index + 1));
        }
        items[index] = item;
        ++header->Count;
    }

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

    public void RemoveAt(int index)
    {
        var header = _header;
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

    private Header* _header => (Header*)_memoryBlock.MemorySegment.Address;
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
        var maxAllocationLength = _memoryBlock.MemoryManager.MaxAllocationLength;
        var headerSize = sizeof(Header);
        if ((headerSize + newCapacity * sizeof(T)) > maxAllocationLength)
        {
            newCapacity = (maxAllocationLength - headerSize) / sizeof(T);

            if (newCapacity < capacity)
            {
                ThrowHelper.OutOfMemory($"The requested capacity {capacity} is greater than the maximum allowed capacity {newCapacity}. Use a Memory Manager with a greater PMB size");
            }
        }

        Capacity = newCapacity;
    }

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
    private struct Header
    {
        public uint Capacity;
        public int Count;
        public ulong _padding;      // We want Header to be 16 bytes to be aligned with a cache line
    }

    #endregion
}