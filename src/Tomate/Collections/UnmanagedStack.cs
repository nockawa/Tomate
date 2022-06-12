using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Tomate;

/// <summary>
/// A stack containing unmanaged items with ref access to each of them
/// </summary>
/// <typeparam name="T"></typeparam>
/// <remarks>
/// This class is heavily based from the <see cref="Stack{T}"/> type of the .net framework.
/// Designed for single thread usage only.
/// <see cref="TryPeek"/> and <see cref="Peek"/> return <code>ref of {T}</code>, so don't use this reference after a <see cref="Push"/> or <see cref="Pop"/> operation.
/// </remarks>
public class UnmanagedStack<T> where T : unmanaged
{
    // NOTES: this class is poorly written, a quick clone of Stack<T> but not a true Unmanaged one. Thread safety is absent. Still uses an Array<T>...

    private T[] _array;
    private int _size;

    private const int DefaultCapacity = 8;

    public UnmanagedStack()
    {
        _array = Array.Empty<T>();
    }

    public UnmanagedStack(int capacity)
    {
        if (capacity < 0)
        {
            ThrowHelper.NeedNonNegIndex(nameof(capacity));
        }
        _array = new T[capacity];
    }
    public int Count => _size;

    public ref T this[int index]
    {
        get
        {
            Debug.Assert((uint)index < _size);
            return ref _array[index];
        }
    }

    public void Clear()
    {
        _size = 0;
    }

    public bool Contains(T item)
    {
        // Compare items using the default equality comparer

        // PERF: Internally Array.LastIndexOf calls
        // EqualityComparer<T>.Default.LastIndexOf, which
        // is specialized for different types. This
        // boosts performance since instead of making a
        // virtual method call each iteration of the loop,
        // via EqualityComparer<T>.Default.Equals, we
        // only make one virtual call to EqualityComparer.LastIndexOf.

        return _size != 0 && Array.LastIndexOf(_array, item, _size - 1) != -1;
    }

    // Returns the top object on the stack without removing it.  If the stack
    // is empty, Peek throws an InvalidOperationException.
    public ref T Peek()
    {
        int size = _size - 1;
        T[] array = _array;

        if ((uint)size >= (uint)array.Length)
        {
            ThrowForEmptyStack();
        }

        return ref array[size];
    }

    public bool TryPeek(ref T result)
    {
        int size = _size - 1;
        T[] array = _array;

        if ((uint)size >= (uint)array.Length)
        {
            result = default!;
            return false;
        }
        result = ref array[size];
        return true;
    }

    // Pops an item from the top of the stack.  If the stack is empty, Pop
    // throws an InvalidOperationException.
    public ref T Pop()
    {
        int size = _size - 1;
        T[] array = _array;

        // if (_size == 0) is equivalent to if (size == -1), and this case
        // is covered with (uint)size, thus allowing bounds check elimination
        // https://github.com/dotnet/coreclr/pull/9773
        if ((uint)size >= (uint)array.Length)
        {
            ThrowForEmptyStack();
        }

        _size = size;
        return ref array[size];
    }

    public bool TryPop(out T result)
    {
        int size = _size - 1;
        T[] array = _array;

        if ((uint)size >= (uint)array.Length)
        {
            result = default!;
            return false;
        }

        _size = size;
        result = array[size];
        return true;
    }

    public ref T Push()
    {
        if (_size >= _array.Length)
        {
            Grow(_size + 1);
        }

        return ref _array[_size++];
    }

    // Pushes an item to the top of the stack.
    public void Push(ref T item)
    {
        int size = _size;
        T[] array = _array;

        if ((uint)size < (uint)array.Length)
        {
            array[size] = item;
            _size = size + 1;
        }
        else
        {
            PushWithResize(ref item);
        }
    }

    public void Push(T item)
    {
        int size = _size;
        T[] array = _array;

        if ((uint)size < (uint)array.Length)
        {
            array[size] = item;
            _size = size + 1;
        }
        else
        {
            PushWithResize(ref item);
        }
    }

    // Non-inline from Stack.Push to improve its code quality as uncommon path
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PushWithResize(ref T item)
    {
        Debug.Assert(_size == _array.Length);
        Grow(_size + 1);
        _array[_size] = item;
        _size++;
    }

    private void Grow(int capacity)
    {
        Debug.Assert(_array.Length < capacity);

        int newcapacity = _array.Length == 0 ? DefaultCapacity : 2 * _array.Length;

        // Allow the list to grow to maximum possible capacity (~2G elements) before encountering overflow.
        // Note that this check works even when _items.Length overflowed thanks to the (uint) cast.
        if ((uint)newcapacity > Array.MaxLength) newcapacity = Array.MaxLength;

        // If computed capacity is still less than specified, set to the original argument.
        // Capacities exceeding Array.MaxLength will be surfaced as OutOfMemoryException by Array.Resize.
        if (newcapacity < capacity) newcapacity = capacity;

        Array.Resize(ref _array, newcapacity);
    }

    // Copies the Stack to an array, in the same order Pop would return the items.
    public T[] ToArray()
    {
        if (_size == 0)
            return Array.Empty<T>();

        T[] objArray = new T[_size];
        int i = 0;
        while (i < _size)
        {
            objArray[i] = _array[_size - i - 1];
            i++;
        }
        return objArray;
    }

    private void ThrowForEmptyStack()
    {
        Debug.Assert(_size == 0);
        ThrowHelper.EmptyStack();
    }


}