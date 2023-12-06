using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Tomate;

public interface IFacade
{
    #region Public APIs

    #region Properties

    MemoryBlock MemoryBlock { get; }

    #endregion

    #endregion
}

[DebuggerTypeProxy(typeof(UnmanagedList<>.DebugView))]
[DebuggerDisplay("Count = {Count}")]
[PublicAPI]
public unsafe struct UnmanagedList<T> : IFacade, IRefCounted where T : unmanaged
{
    #region Constants

    private const int DefaultCapacity = 4;

    #endregion

    #region Public APIs

    #region Properties

    private ref uint _capacity => ref _header->Capacity;
    private ref int _count => ref _header->Count;
    private Header* _header => (Header*)_memoryBlock.MemorySegment.Address;
    private T* _items => (T*)(_header + 1);

    public MemorySegment<T> Content => new (_items, _count); //_memoryBlock.MemorySegment.Slice(0, _size);

    public int Count => _count;

    public bool IsDefault => _memoryBlock.IsDefault;
    public bool IsDisposed => _count < 0;

    public ref T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
            {
                ThrowHelper.OutOfRange($"Index {index} must be less than {_count} and greater or equal to 0");
            }
            return ref _items[index];
        }
    }

    public MemoryBlock MemoryBlock => _memoryBlock;

    public int RefCounter => _memoryBlock.RefCounter;

    public int Capacity
    {
        get => (int)_capacity;
        set 
        {
            if (value < _count)
            {
                ThrowHelper.OutOfRange($"New Capacity {value} can't be less than actual Count {_count}");
            }
            if (value != _capacity)
            {
                _memoryBlock.Resize(sizeof(Header) + (sizeof(T) * value));
                //_buffer = _memoryBlock.MemorySegment.Address;
                _capacity = (uint)value;
            }
        }
    }

    #endregion

    #region Methods

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public int Add(T item)
    {
        var res = _count;
        if ((uint)_count < _capacity)
        {
            _items[_count++] = item;
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
        if (_count == _capacity)
        {
            Grow(_count + 1);
        }

        return ref _items[_count++];
    }

    public int AddRef() => _memoryBlock.AddRef();

    public void Clear()
    {
        _count = 0;
    }

    // Non-inline from List.Add to improve its code quality as uncommon path
    public void CopyTo(T[] items, int i)
    {
        var span = new Span<T>(items, i, items.Length - i);
        //_memoryBlock.MemorySegment.ToSpan().CopyTo(span);
        new Span<T>(_items, span.Length).CopyTo(span); 
    }

    public void Dispose()
    {
        if (IsDefault || IsDisposed)
        {
            return;
        }
        _memoryBlock.Dispose();
        _memoryBlock = default;
        //_size = -1;
    }

    public Enumerator GetEnumerator() => new(this);

    public int IndexOf(T item)
    {
        if (typeof(T) == typeof(int))
        {
            var src = Unsafe.As<T, int>(ref item);
            var buffer = (int*)_items; //_memoryBlock.MemorySegment.Address;

            var l = _count;
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
            var buffer = (long*)_items; //_memoryBlock.MemorySegment.Address;

            var l = _count;
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
            var buffer = (short*)_items; //_memoryBlock.MemorySegment.Address;

            var l = _count;
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
            var buffer = (byte*)_items; //_memoryBlock.MemorySegment.Address;

            var l = _count;
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
            var span = new Span<T>(_items, _count); //_memoryBlock.MemorySegment.ToSpan();
            var l = _count;

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
        if ((uint)index > (uint)_count)
        {
            ThrowHelper.OutOfRange($"Can't insert, given index {index} is greater than Count {_count}.");
        }

        if (_count == _capacity)
        {
            Grow(_count + 1);
        }
        if (index < _count)
        {
            var span = new Span<T>(_items, _count); // _memoryBlock.MemorySegment.ToSpan();
            span.Slice(index, _count - index).CopyTo(span.Slice(index + 1));
        }
        _items[index] = item;
        ++_count;
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
        if ((uint)index >= (uint)_count)
        {
            ThrowHelper.OutOfRange($"Can't remove, given index {index} is greater than Count {_count}.");
        }
        if (index < _count)
        {
            var span = new Span<T>(_items, _count); // _memoryBlock.MemorySegment.ToSpan();
            span.Slice(index + 1).CopyTo(span.Slice(index));
            _count--;
        }
    }

    #endregion

    #endregion

    #region Fields

    private MemoryBlock _memoryBlock;

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
        if (initialCapacity > 0)
        {
            _memoryBlock = memoryManager.Allocate(sizeof(Header) + (sizeof(T) * initialCapacity));
        }
        _count = 0;
        //_buffer = (T*)(_memoryBlock.MemorySegment.Address + sizeof(Header));
        _capacity = (uint)initialCapacity;
    }

    #endregion

    #region Private methods

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AddWithResize(T item)
    {
        Debug.Assert(_count == _capacity);
        Grow(_count + 1);
        _items[_count++] = item;
    }

    private void Grow(int capacity)
    {
        Debug.Assert(_capacity < capacity);

        var newCapacity = _capacity == 0 ? DefaultCapacity : (int)(2 * _capacity);

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

        #region Fields

        private readonly MemorySegment<T> _data;

        #endregion

        #region Constructors

        public DebugView(UnmanagedList<T> list)
        {
            _data = list.Content;
        }

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

        #region Fields

        private readonly Span<T> _span;
        private int _index;

        #endregion

        #region Constructors

        public Enumerator(UnmanagedList<T> owner)
        {
            _span = new(owner._items, owner.Count);
            //_span = owner._memoryBlock.MemorySegment.Slice(0, owner.Count).ToSpan();
            _index = -1;
        }

        #endregion
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        #region Fields

        public ulong _padding;      // We want Header to be 16 bytes to be aligned with a cache line
        public uint Capacity;
        public int Count;

        #endregion
    }

    #endregion
}