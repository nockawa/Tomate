using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tomate;

[DebuggerTypeProxy(typeof(UnmanagedList<>.DebugView))]
[DebuggerDisplay("Count = {Count}")]
public unsafe struct UnmanagedList<T> : IDisposable where T : unmanaged
{
    private const int DefaultCapacity = 4;

    private readonly IMemoryManager _memoryManager;
    private MemoryBlock<T> _memoryBlock;
    private int _size;
    private uint _capacity;
    private T* _buffer;


    public UnmanagedList(IMemoryManager memoryManager = null, int initialCapacity = DefaultCapacity)
    {
        if (initialCapacity < 0)
        {
            ThrowHelper.OutOfRange("Initial Capacity can't be a negative number");
        }
        _memoryManager = memoryManager ?? DefaultMemoryManager.GlobalInstance;
        _memoryBlock = default;
        if (initialCapacity > 0)
        {
            _memoryBlock = _memoryManager.Allocate<T>(initialCapacity);
        }
        _size = 0;
        _buffer = _memoryBlock.MemorySegment.Address;
        _capacity = (uint)_memoryBlock.MemorySegment.Length;
    }

    public int Capacity
    {
        get => (int)_capacity;
        set 
        {
            if (value < _size)
            {
                ThrowHelper.OutOfRange($"New Capacity {value} can't be less than actual Count {_size}");
            }
            if (value != _capacity)
            {
                if (value > 0)
                {
                    var newItems = _memoryManager.Allocate<T>(value);
                    if (_size > 0)
                    {
                        _memoryBlock.MemorySegment.Slice(0, _size).ToSpan().CopyTo(newItems.MemorySegment.ToSpan());
                    }

                    _memoryManager.Free(_memoryBlock);
                    _memoryBlock = newItems;
                    _buffer = _memoryBlock.MemorySegment.Address;
                    _capacity = (uint)_memoryBlock.MemorySegment.Length;
                }
                else
                {
                    _memoryBlock = default;
                    _buffer = null;
                }
            }
        }
    }

    public int Count => _size;

    public ref T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_size)
            {
                ThrowHelper.OutOfRange($"Index {index} must be less than {_size} and greater or equal to 0");
            }
            return ref _buffer[index];
        }
    }

    public MemorySegment<T> Content => _memoryBlock.MemorySegment.Slice(0, _size);

    public bool IsEmpty => _memoryManager == null;
    public bool IsDisposed => _size < 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public int Add(T item)
    {
        var res = _size;
        if ((uint)_size < _capacity)
        {
            _buffer[_size++] = item;
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
        if (_size == _capacity)
        {
            Grow(_size + 1);
        }

        return ref _buffer[_size++];
    }

    // Inserts an element into this list at a given index. The size of the list
    // is increased by one. If required, the capacity of the list is doubled
    // before inserting the new element.
    public void Insert(int index, T item)
    {
        // Note that insertions at the end are legal.
        if ((uint)index > (uint)_size)
        {
            ThrowHelper.OutOfRange($"Can't insert, given index {index} is greater than Count {_size}.");
        }

        if (_size == _capacity)
        {
            Grow(_size + 1);
        }
        if (index < _size)
        {
            var span = _memoryBlock.MemorySegment.ToSpan();
            span.Slice(index, _size - index).CopyTo(span.Slice(index + 1));
        }
        _buffer[index] = item;
        ++_size;
    }

    // Non-inline from List.Add to improve its code quality as uncommon path
    public void CopyTo(T[] items, int i)
    {
        var span = new Span<T>(items, i, items.Length - i);
        _memoryBlock.MemorySegment.ToSpan().CopyTo(span);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AddWithResize(T item)
    {
        Debug.Assert(_size == _capacity);
        Grow(_size + 1);
        _buffer[_size++] = item;
    }

    private void Grow(int capacity)
    {
        Debug.Assert(_capacity < capacity);

        var newCapacity = _capacity == 0 ? DefaultCapacity : (int)(2 * _capacity);

        // Check if the new capacity exceed the size of the block we can allocate
        if ((newCapacity * sizeof(T)) > _memoryManager.MaxAllocationLength)
        {
            newCapacity = _memoryManager.MaxAllocationLength / sizeof(T);

            if (newCapacity < capacity)
            {
                ThrowHelper.OutOfMemory($"The requested capacity {capacity} is greater than the maximum allowed capacity {newCapacity}. Use a Memory Manager with a greater PMB size");
            }
        }

        Capacity = newCapacity;
    }

    public ref struct Enumerator
    {
        private readonly Span<T> _span;
        private int _index;

        public Enumerator(UnmanagedList<T> owner)
        {
            _span = owner._memoryBlock.MemorySegment.Slice(0, owner.Count).ToSpan();
            _index = -1;
        }
        
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

        public ref T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            get => ref _span[_index];
        }
    }

    public Enumerator GetEnumerator() => new(this);

    public void Dispose()
    {
        if (IsEmpty || IsDisposed)
        {
            return;
        }
        _memoryManager.Free(_memoryBlock);
        _memoryBlock = default;
        _size = -1;
    }

    public void Clear()
    {
        _size = 0;
    }

    internal sealed class DebugView
    {
        private readonly MemorySegment<T> _data;

        public DebugView(UnmanagedList<T> list)
        {
            _data = list.Content;
        }

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public T[] Items
        {
            get
            {
                T[] items = new T[_data.Length];
                var dest = new Span<T>(items);
                _data.ToSpan().CopyTo(dest);
                return items;
            }
        }
    }
}