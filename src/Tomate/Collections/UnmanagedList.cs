using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Tomate;

public struct UnmanagedList<T> : IDisposable where T : unmanaged
{
    private const int DefaultCapacity = 4;

    private readonly IMemoryManager _memoryManager;
    private MemorySegment<T> _dataSegment;
    private int _size;

    public UnmanagedList(IMemoryManager memoryManager, int initialCapacity = DefaultCapacity)
    {
        if (initialCapacity < 0)
        {
            ThrowHelper.OutOfRange("Initial Capacity can't be a negative number");
        }
        _memoryManager = memoryManager;
        _dataSegment = default;
        if (initialCapacity > 0)
        {
            _dataSegment = _memoryManager.Allocate<T>(initialCapacity);
        }
        _size = 0;
    }

    public int Capacity
    {
        get => _dataSegment.Length;
        set 
        {
            if (value < _size)
            {
                ThrowHelper.OutOfRange($"New Capacity {value} can't be less than actual Count {_size}");
            }
            if (value != _dataSegment.Length)
            {
                if (value > 0)
                {
                    var newItems = _memoryManager.Allocate<T>(value);
                    if (_size > 0)
                    {
                        _dataSegment.Slice(0, _size).ToSpan().CopyTo(newItems.ToSpan());
                    }

                    _memoryManager.Free(_dataSegment);
                    _dataSegment = newItems;
                }
                else
                {
                    _dataSegment = default;
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
            return ref _dataSegment[index];
        }
    }

    public MemorySegment<T> Content => _dataSegment.Slice(0, _size);

    public bool IsEmpty => _memoryManager == null;
    public bool IsDisposed => _size < 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public int Add(T item)
    {
        var res = _size;
        if ((uint)_size < (uint)_dataSegment.Length)
        {
            _dataSegment[_size++] = item;
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

    public ref T AddInPlace()
    {
        if (_size == _dataSegment.Length)
        {
            Grow(_size + 1);
        }

        return ref _dataSegment[_size++];
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

        if (_size == _dataSegment.Length)
        {
            Grow(_size + 1);
        }
        if (index < _size)
        {
            var span = _dataSegment.ToSpan();
            span.Slice(index, _size - index).CopyTo(span.Slice(index + 1));
        }
        _dataSegment[index] = item;
        ++_size;
    }

    // Non-inline from List.Add to improve its code quality as uncommon path
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AddWithResize(T item)
    {
        Debug.Assert(_size == _dataSegment.Length);
        Grow(_size + 1);
        _dataSegment[_size++] = item;
    }

    private unsafe void Grow(int capacity)
    {
        Debug.Assert(_dataSegment.Length < capacity);

        var newCapacity = _dataSegment.Length == 0 ? DefaultCapacity : 2 * _dataSegment.Length;

        // Check if the new capacity exceed the size of the block we can allocate
        if ((newCapacity * sizeof(T)) > _memoryManager.PinnedMemoryBlockSize)
        {
            newCapacity = _memoryManager.PinnedMemoryBlockSize / sizeof(T);

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
            _span = owner._dataSegment.ToSpan();
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
        if (IsEmpty)
        {
            return;
        }
        _memoryManager.Free(_dataSegment);
        _dataSegment = default;
        _size = -1;
    }

    public void Clear()
    {
        _size = 0;
    }
}