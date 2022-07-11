using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tomate;

/// <summary>
/// Define a memory segment stored at a fixed address
/// </summary>
/// <remarks>
/// This is certainly the fastest way to store/use a memory segment. This allows us to store instances of this struct everywhere we want (as opposed
/// to <see cref="Span{T}"/> which can't be stored in type declarations).
/// The cast/conversion to <see cref="Span{T}"/> is as fast as it could be and the user should rely on <see cref="Span{T}"/> as much as possible, even
/// through direct memory access will be slightly faster.
/// Only work with this type if you are dealing with pinned memory block, otherwise rely on <see cref="Span{T}"/>
/// </remarks>
[DebuggerDisplay("Address: {Address}, Length: {Length}")]
public readonly unsafe struct MemorySegment
{
    public readonly byte* Address;
    public readonly int Length;

    public static readonly MemorySegment Empty = new(null, 0);

    public MemorySegment(byte* address, int length)
    {
        Address = address;
        Length = length;
    }

    public bool IsEmpty => Address == null;
    public byte* End => Address + Length;

    public Span<T> ToSpan<T>() where T : unmanaged => new(Address, Length / sizeof(T));
    public static implicit operator Span<byte>(MemorySegment segment)
    {
        return new Span<byte>(segment.Address, segment.Length);
    }

    public MemorySegment<T> Cast<T>() where T : unmanaged => new(Address, Length / sizeof(T));

    public MemorySegment Slice(int start) => Slice(start, Length - start);

    public MemorySegment Slice(int start, int length)
    {
        if ((start < 0) || (start + length) > Length)
            ThrowHelper.OutOfRange($"The given index ({start}) and length ({length}) are out of range. Segment length limit is {Length}, slice's end is {start + length}.");

        return new MemorySegment(Address + start, length);
    }

    public (MemorySegment, MemorySegment) Split(int splitOffset)
    {
        if ((splitOffset < 0) || splitOffset > Length)
            ThrowHelper.OutOfRange($"The given split offset ({splitOffset}) is out of range, segment length limit is {Length}.");

        return (new MemorySegment(Address, splitOffset), new MemorySegment(Address+splitOffset, Length-splitOffset));
    }
}

/// <summary>
/// Define a memory segment stored at a fixed address, generic version
/// </summary>
/// <remarks>
/// This is certainly the fastest way to store/use a memory segment. This allows us to store instances of this struct everywhere we want (as opposed
/// to <see cref="Span{T}"/> which can't be stored in type declarations).
/// The cast/conversion to <see cref="Span{T}"/> is as fast as it could be and the user should rely on <see cref="Span{T}"/> as much as possible, even
/// through direct memory access will be slightly faster.
/// Only work with this type if you are dealing with pinned memory block, otherwise rely on <see cref="Span{T}"/>
/// </remarks>
[DebuggerDisplay("Type: {typeof(T).Name} Address: {Address}, Length: {Length}")]
[DebuggerTypeProxy(typeof(MemorySegment<>.DebugView))]
public readonly unsafe struct MemorySegment<T> where T : unmanaged
{
    internal sealed class DebugView
    {
        private readonly T[] _array;

        public DebugView(MemorySegment<T> segment)
        {
            _array = segment.ToArray();
        }

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public T[] Items => _array;
    }

    public readonly T* Address;
    public readonly int Length;

    public static implicit operator MemorySegment(MemorySegment<T> source) => new((byte*)source.Address, sizeof(T)*source.Length);

    public static MemorySegment<T> AllocateHeapPinnedArray(int length)
    {
        var a = GC.AllocateUninitializedArray<T>(length, true);
        var addr = Marshal.UnsafeAddrOfPinnedArrayElement(a, 0).ToPointer();

        // We need to keep a reference on the array, otherwise it will be GCed and the address we have will corrupt things
        _allocatedArrays.TryAdd(new IntPtr(addr), a);
        
        return new(addr, length);
    }

    public static bool FreeHeapPinnedArray(MemorySegment<T> segment) => _allocatedArrays.TryRemove(new IntPtr(segment.Address), out _);

    private static readonly ConcurrentDictionary<IntPtr, T[]> _allocatedArrays;
    static MemorySegment() => _allocatedArrays = new();


    /// <summary>
    /// Construct an instance of a Memory Segment
    /// </summary>
    /// <param name="address">Starting address of the segment</param>
    /// <param name="length">Length of the segment in {T} (NOT in bytes)</param>
    public MemorySegment(void* address, int length)
    {
        Address = (T*)address;
        Length = length;
    }

    public bool IsEmpty => Address == null;
    public static readonly MemorySegment<T> Empty = new(null, 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining|MethodImplOptions.AggressiveOptimization)]
    public Span<T> ToSpan() => new(Address, Length);
    public Span<TU> ToSpan<TU>() where TU : unmanaged => new(Address, Length * sizeof(T) / sizeof(TU));

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static implicit operator Span<T>(MemorySegment<T> segment)
    {
        return new Span<T>(segment.Address, segment.Length);
    }

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get
        {
            if ((index < 0) || (index >= Length)) ThrowHelper.OutOfRange($"The given index ({index}) is out of range. Segment length limit is {Length}.");
            return ref Unsafe.AsRef<T>(Address + index);
        }
    }

    private T[] ToArray()
    {
        var length = Length;
        var res = new T[length];

        for (int i = 0; i < length; i++)
        {
            res[i] = this[i];
        }

        return res;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ref T AsRef()
    {
        if (Length < 1) ThrowHelper.OutOfRange($"Segment's length is too small to access this element, required minimum length 1, actual length {Length}.");
        return ref Unsafe.AsRef<T>(Address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ref T AsRef(int index)
    {
        if ((index < 0) || (index >= Length)) ThrowHelper.OutOfRange($"The given index ({index}) is out of range. Segment length limit is {Length}.");
        return ref Unsafe.AsRef<T>(Address + index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public MemorySegment<T> Slice(int start) => Slice(start, Length - start);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public MemorySegment<T> Slice(int start, int length)
    {
        if ((start < 0) || (start+length) > Length)
            ThrowHelper.OutOfRange($"The given index ({start}) and length ({length}) are out of range. Segment length limit is {Length}, slice's end is {start+length}.");

        return new(Address + start, length);
    }

    public MemorySegment<TTo> Cast<TTo>() where TTo : unmanaged => new(Address, Length * sizeof(T) / sizeof(TTo));

    /// <summary>Gets an enumerator for this segment.</summary>
    public Enumerator GetEnumerator() => new(this);

    /// <summary>Enumerates the elements of a <see cref="MemorySegment{T}"/>.</summary>
    public ref struct Enumerator
    {
        /// <summary>The segment being enumerated.</summary>
        private readonly MemorySegment<T> _segment;
        /// <summary>The next index to yield.</summary>
        private int _index;

        /// <summary>Initialize the enumerator.</summary>
        /// <param name="segment">The segment to enumerate.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Enumerator(MemorySegment<T> segment)
        {
            _segment = segment;
            _index = -1;
        }

        /// <summary>Advances the enumerator to the next element of the segment.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            int index = _index + 1;
            if (index < _segment.Length)
            {
                _index = index;
                return true;
            }

            return false;
        }

        /// <summary>Gets the element at the current position of the enumerator.</summary>
        public ref T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _segment.AsRef(_index);
        }
    }
}

public readonly unsafe struct MemorySegments<T1, T2> where T1 : unmanaged where T2 : unmanaged
{
    private readonly byte* _baseAddress;
    private readonly int _length1;
    private readonly int _length2;

    public MemorySegments(MemorySegment segment, int length1, int length2)
    {
        var requiredLength = (length1 * sizeof(T1)) + (length2 * sizeof(T2));
        if (segment.Length < requiredLength)
        {
            throw new Exception($"The given segment is too small, required size is at least {requiredLength}");
        }

        _baseAddress = segment.Address;
        _length1 = length1;
        _length2 = length2;
    }

    public MemorySegment<T1> Segment1 => new(_baseAddress, _length1);
    public MemorySegment<T2> Segment2 => new(_baseAddress + sizeof(T1) * _length1, _length2);
}

public readonly unsafe struct MemorySegments<T1, T2, T3> where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
{
    private readonly byte* _baseAddress;
    private readonly int _length1;
    private readonly int _length2;
    private readonly int _length3;

    public MemorySegments(MemorySegment segment, int length1, int length2, int length3)
    {
        var requiredLength = (length1 * sizeof(T1)) + (length2 * sizeof(T2)) + (length3 * sizeof(T3));
        if (segment.Length < requiredLength)
        {
            throw new Exception($"The given segment is too small, required size is at least {requiredLength}");
        }

        _baseAddress = segment.Address;
        _length1 = length1;
        _length2 = length2;
        _length3 = length3;
    }

    public MemorySegment<T1> Segment1 => new(_baseAddress, _length1);
    public MemorySegment<T2> Segment2 => new(_baseAddress + sizeof(T1) * _length1, _length2);
    public MemorySegment<T3> Segment3 => new(_baseAddress + (sizeof(T1) * _length1) + (sizeof(T2) * _length2), _length3);
}

public readonly unsafe struct MemorySegments<T1, T2, T3, T4> where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
{
    private readonly byte* _baseAddress;
    private readonly int _length1;
    private readonly int _length2;
    private readonly int _length3;
    private readonly int _length4;

    public MemorySegments(MemorySegment segment, int length1, int length2, int length3, int length4)
    {
        var requiredLength = (length1 * sizeof(T1)) + (length2 * sizeof(T2)) + (length3 * sizeof(T3)) + (length4 * sizeof(T4));
        if (segment.Length < requiredLength)
        {
            throw new Exception($"The given segment is too small, required size is at least {requiredLength}");
        }

        _baseAddress = segment.Address;
        _length1 = length1;
        _length2 = length2;
        _length3 = length3;
        _length4 = length4;
    }

    public MemorySegment<T1> Segment1 => new(_baseAddress, _length1);
    public MemorySegment<T2> Segment2 => new(_baseAddress + (sizeof(T1) * _length1), _length2);
    public MemorySegment<T3> Segment3 => new(_baseAddress + (sizeof(T1) * _length1) + (sizeof(T2) * _length2), _length3);
    public MemorySegment<T4> Segment4 => new(_baseAddress + (sizeof(T1) * _length1) + (sizeof(T2) * _length2) + (sizeof(T3) * _length3), _length4);
}


public readonly unsafe struct MemorySegments<T1, T2, T3, T4, T5> where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged
{
    private readonly byte* _baseAddress;
    private readonly int _length1;
    private readonly int _length2;
    private readonly int _length3;
    private readonly int _length4;
    private readonly int _length5;

    public MemorySegments(MemorySegment segment, int length1, int length2, int length3, int length4, int length5)
    {
        var requiredLength = (length1 * sizeof(T1)) + (length2 * sizeof(T2)) + (length3 * sizeof(T3)) + (length4 * sizeof(T4)) + (length5 * sizeof(T5));
        if (segment.Length < requiredLength)
        {
            throw new Exception($"The given segment is too small, required size is at least {requiredLength}");
        }

        _baseAddress = segment.Address;
        _length1 = length1;
        _length2 = length2;
        _length3 = length3;
        _length4 = length4;
        _length5 = length5;
    }

    public MemorySegment<T1> Segment1 => new(_baseAddress, _length1);
    public MemorySegment<T2> Segment2 => new(_baseAddress + (sizeof(T1) * _length1), _length2);
    public MemorySegment<T3> Segment3 => new(_baseAddress + (sizeof(T1) * _length1) + (sizeof(T2) * _length2), _length3);
    public MemorySegment<T4> Segment4 => new(_baseAddress + (sizeof(T1) * _length1) + (sizeof(T2) * _length2) + (sizeof(T3) * _length3), _length4);
    public MemorySegment<T5> Segment5 => new(_baseAddress + (sizeof(T1) * _length1) + (sizeof(T2) * _length2) + (sizeof(T3) * _length3) + (sizeof(T4) * _length4), _length5);
}
