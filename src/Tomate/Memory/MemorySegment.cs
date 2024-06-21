using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Tomate;

public delegate TResult BinarySearchComp<T1, T2, out TResult>(ref T1 arg1, ref T2 arg2) where T1 : unmanaged where T2 : unmanaged;

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
[DebuggerDisplay("Address: {Address}, Length: {Length}({LengthFriendlySize})")]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
[PublicAPI]
public readonly unsafe struct MemorySegment : IEquatable<MemorySegment>
{
    #region Constants

    public static readonly MemorySegment Empty = new(null, 0);

    private const uint SegmentIsInMMF = 0x80000000;
    private const uint LengthMask = 0x7FFFFFFF;

    #endregion

    #region Public APIs

    #region Properties

    public byte* End => Address + Length;

    public bool IsDefault => _addr==0 && _data==0;

    #endregion

    #region Methods

    /// Beware if the span doesn't map to a fixed address space, you may have issues if you don't handle the MemorySegment's lifetime accordingly
    public static explicit operator MemorySegment(Span<byte> span)
    {
        return new MemorySegment((byte*)Unsafe.AsPointer(ref MemoryMarshal.AsRef<byte>(span)), span.Length * sizeof(byte));
    }

    public static implicit operator Span<byte>(MemorySegment segment)
    {
        return new Span<byte>(segment.Address, segment.Length);
    }

    public static implicit operator void*(MemorySegment segment) => segment.Address;
    public static implicit operator byte*(MemorySegment segment) => segment.Address;

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public MemorySegment<T> Cast<T>() where T : unmanaged => new(Address, Length / sizeof(T), MMFId);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public MemorySegment Slice(int start) => Slice(start, (start < 0) ? -start : (Length - start));

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public MemorySegment Slice(int start, int length)
    {
        if (start < 0)
        {
            start = Length + start;
        }

        if (length < 0)
        {
            length = Length + length;
        }

        if ((start + length) > Length)
        {
            ThrowHelper.OutOfRange($"The given index ({start}) and length ({length}) are out of range. Segment length limit is {Length}, slice's end is {start + length}.");
        }

        return new MemorySegment(Address + start, length, MMFId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public (MemorySegment, MemorySegment) Split(int splitOffset)
    {
        if ((splitOffset < 0) || splitOffset > Length)
            ThrowHelper.OutOfRange($"The given split offset ({splitOffset}) is out of range, segment length limit is {Length}.");

        return (new MemorySegment(Address, splitOffset), new MemorySegment(Address+splitOffset, Length-splitOffset, MMFId));
    }

    public (MemorySegment<TA>, MemorySegment<TB>) Split<TA, TB>(int splitOffset) where TA : unmanaged where TB : unmanaged
    {
        if ((splitOffset < 0) || splitOffset > Length)
            ThrowHelper.OutOfRange($"The given split offset ({splitOffset}) is out of range, segment length limit is {Length}.");

        return (new MemorySegment(Address, splitOffset, MMFId).Cast<TA>(), new MemorySegment(Address+splitOffset, Length-splitOffset, MMFId).Cast<TB>());
    }

    public Span<T> ToSpan<T>() where T : unmanaged => new(Address, Length / sizeof(T));

    public override string ToString() => $"Address: 0x{(ulong)Address:X}, Length: {Length}";

    #endregion

    #endregion

    #region Fields

    public readonly ulong _addr;
    public readonly uint _data;

    public byte* Address
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get
        {
            if (IsInMMF)
            {
                var mmfId = (int)(_addr >> 48);
                var baseAddr = (byte*)MMFRegistry.MMFAddressTable[mmfId];
                var offset = _addr & 0xFFFFFFFFFFFF;
                return baseAddr + offset;
            }
            else
            {
                return (byte*)_addr;
            }
        }
    }
    public int Length => (int)(_data & LengthMask);
    internal string LengthFriendlySize => Length.FriendlySize();
    
    public bool IsInMMF
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get => (_data & ~LengthMask) != 0;
    }

    internal int MMFId => IsInMMF ? (int)(_addr >> 48) : -1;

    #endregion

    #region Constructors

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public MemorySegment(byte* address, int length) : this(address, length, -1)
    {
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal MemorySegment(byte* address, int length, int mmfId)
    {
        Debug.Assert(length >= 0, $"Length must be null or positive");

        if (mmfId == -1)
        {
            _addr = (ulong)address;
            _data = (uint)length;
        }
        else
        {
            _data = (uint)length | SegmentIsInMMF;
            var baseAddr = (byte*)MMFRegistry.MMFAddressTable[mmfId];
            var offset = address - baseAddr;
            _addr = (ulong)mmfId << 48 | (ulong)offset;
        }
    }

    #endregion

    #region GetHashCode & Equality

    public bool Equals(MemorySegment other)
    {
        return Address == other.Address && Length == other.Length;
    }

    public override bool Equals(object obj)
    {
        return obj is MemorySegment other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(unchecked((int)(long)Address), Length);
    }

    public static bool operator ==(MemorySegment left, MemorySegment right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(MemorySegment left, MemorySegment right)
    {
        return !left.Equals(right);
    }

    #endregion
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
[DebuggerDisplay("Type: {typeof(T).Name} Address: {Address}, Length: {Length}({LengthFriendlySize})")]
[DebuggerTypeProxy(typeof(MemorySegment<>.DebugView))]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
[PublicAPI]
public readonly unsafe struct MemorySegment<T> where T : unmanaged
{
    #region Constants

    public static readonly MemorySegment<T> Empty = new(null, 0);

    private const uint SegmentIsInMMF = 0x80000000;
    private const uint LengthMask = 0x7FFFFFFF;

    #endregion

    #region Public APIs

    #region Properties

    public T* End => Address + Length;

    public bool IsDefault => Address == null;

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get
        {
            if ((index < 0) || (index >= Length)) ThrowHelper.OutOfRange($"The given index ({index}) is out of range. Segment length limit is {Length}.");
            return ref Unsafe.AsRef<T>(Address + index);
        }
    }

    #endregion

    #region Methods

    public static implicit operator MemorySegment(MemorySegment<T> source) => new((byte*)source.Address, sizeof(T)*source.Length);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public static implicit operator Span<T>(MemorySegment<T> segment)
    {
        return new Span<T>(segment.Address, segment.Length);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int BinarySearch<TKey>(TKey key, BinarySearchComp<T, TKey, long> f) where TKey : unmanaged
    {
        var length = Length;
        var span = ToSpan();
        
        int lo = 0;
        int hi = length - 1;
        // If length == 0, hi == -1, and loop will not be entered
        while (lo <= hi)
        {
            // PERF: `lo` or `hi` will never be negative inside the loop,
            //       so computing median using uints is safe since we know
            //       `length <= int.MaxValue`, and indices are >= 0
            //       and thus cannot overflow an uint.
            //       Saves one subtraction per loop compared to
            //       `int i = lo + ((hi - lo) >> 1);`
            var i = (int)(((uint)hi + (uint)lo) >> 1);
            var c = f(ref span[i], ref key);
            
            if (c == 0)
            {
                return i;
            }

            if (c > 0)
            {
                lo = i + 1;
            }
            else
            {
                hi = i - 1;
            }
        }
        // If none found, then a negative number that is the bitwise complement
        // of the index of the next element that is larger than or, if there is
        // no larger element, the bitwise complement of `length`, which
        // is `lo` at this point.
        return ~lo;
    }

    public MemorySegment Cast() => new((byte*)Address, Length * sizeof(T), MMFId);
    public MemorySegment<TTo> Cast<TTo>() where TTo : unmanaged => new(Address, Length * sizeof(T) / sizeof(TTo));

    /// <summary>Gets an enumerator for this segment.</summary>
    public Enumerator GetEnumerator() => new(this);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public MemorySegment<T> Slice(int start) => Slice(start, (start < 0) ? -start : (Length - start));

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public MemorySegment<T> Slice(int start, int length)
    {
        if (start < 0)
        {
            start = Length + start;
        }

        if ((start + length) > Length)
        {
            ThrowHelper.OutOfRange($"The given index ({start}) and length ({length}) are out of range. Segment length limit is {Length}, slice's end is {start+length}.");
        }

        return new(Address + start, length);
    }

    public (MemorySegment<TA>, MemorySegment<TB>) Split<TA, TB>(int splitOffset) where TA : unmanaged where TB : unmanaged
    {
        if ((splitOffset < 0) || splitOffset > Length)
            ThrowHelper.OutOfRange($"The given split offset ({splitOffset}) is out of range, segment length limit is {Length}.");

        return (new MemorySegment<T>(Address, splitOffset, MMFId).Cast<TA>(), new MemorySegment<T>(Address+splitOffset, Length-splitOffset, MMFId).Cast<TB>());
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining|MethodImplOptions.AggressiveOptimization)]
    public Span<T> ToSpan() => new(Address, Length);

    public Span<TU> ToSpan<TU>() where TU : unmanaged => new(Address, Length * sizeof(T) / sizeof(TU));

    #endregion

    #endregion

    #region Fields

    public readonly ulong _addr;
    public readonly uint _data;

    public T* Address
    {
        get
        {
            if (IsInMMF)
            {
                var mmfId = (int)(_addr >> 48);
                var baseAddr = (byte*)MMFRegistry.MMFAddressTable[mmfId];
                var offset = _addr & 0xFFFFFFFFFFFF;
                return (T*)(baseAddr + offset);
            }
            else
            {
                return (T*)_addr;
            }
        }
    }
    public int Length => (int)(_data & LengthMask);
    internal string LengthFriendlySize => Length.FriendlySize();
    
    public bool IsInMMF => (_data & ~LengthMask) != 0;
    
    internal int MMFId => IsInMMF ? (int)(_addr >> 48) : -1;

    #endregion

    #region Constructors

    /// <summary>
    /// Construct an instance of a Memory Segment
    /// </summary>
    /// <param name="address">Starting address of the segment</param>
    /// <param name="length">Length of the segment in {T} (NOT in bytes)</param>
    public MemorySegment(void* address, int length) : this(address, length, -1)
    {
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    internal MemorySegment(void* address, int length, int mmfId)
    {
        Debug.Assert(length >= 0, $"Length must be null or positive, {length} was given.");

        if (mmfId == -1)
        {
            _addr = (ulong)address;
            _data = (uint)length;
        }
        else
        {
            _data = (uint)length | SegmentIsInMMF;
            var baseAddr = (byte*)MMFRegistry.MMFAddressTable[mmfId];
            var offset = (byte*)address - baseAddr;
            _addr = (ulong)mmfId << 48 | (ulong)offset;
        }
    }
    
    
    #endregion

    #region Private methods

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

    #endregion

    #region Inner types

    internal sealed class DebugView
    {
        #region Public APIs

        #region Properties

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public T[] Items => _array;

        #endregion

        #endregion

        #region Fields

        private readonly T[] _array;

        #endregion

        #region Constructors

        public DebugView(MemorySegment<T> segment)
        {
            _array = segment.ToArray();
        }

        #endregion
    }

    /// <summary>Enumerates the elements of a <see cref="MemorySegment{T}"/>.</summary>
    public ref struct Enumerator
    {
        #region Public APIs

        #region Properties

        /// <summary>Gets the element at the current position of the enumerator.</summary>
        public ref T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _segment.AsRef(_index);
        }

        #endregion

        #region Methods

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

        #endregion

        #endregion

        #region Fields

        /// <summary>The segment being enumerated.</summary>
        private readonly MemorySegment<T> _segment;

        /// <summary>The next index to yield.</summary>
        private int _index;

        #endregion

        #region Constructors

        /// <summary>Initialize the enumerator.</summary>
        /// <param name="segment">The segment to enumerate.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Enumerator(MemorySegment<T> segment)
        {
            _segment = segment;
            _index = -1;
        }

        #endregion
    }

    #endregion
}

public readonly unsafe struct MemorySegments<T1, T2> where T1 : unmanaged where T2 : unmanaged
{
    #region Public APIs

    #region Properties

    public MemorySegment<T1> Segment1 => new(_baseAddress, _length1);
    public MemorySegment<T2> Segment2 => new(_baseAddress + sizeof(T1) * _length1, _length2);

    #endregion

    #endregion

    #region Fields

    private readonly byte* _baseAddress;
    private readonly int _length1;
    private readonly int _length2;

    #endregion

    #region Constructors

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

    #endregion
}

public readonly unsafe struct MemorySegments<T1, T2, T3> where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
{
    #region Public APIs

    #region Properties

    public MemorySegment<T1> Segment1 => new(_baseAddress, _length1);
    public MemorySegment<T2> Segment2 => new(_baseAddress + sizeof(T1) * _length1, _length2);
    public MemorySegment<T3> Segment3 => new(_baseAddress + (sizeof(T1) * _length1) + (sizeof(T2) * _length2), _length3);

    #endregion

    #endregion

    #region Fields

    private readonly byte* _baseAddress;
    private readonly int _length1;
    private readonly int _length2;
    private readonly int _length3;

    #endregion

    #region Constructors

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

    #endregion
}

public readonly unsafe struct MemorySegments<T1, T2, T3, T4> where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
{
    #region Public APIs

    #region Properties

    public MemorySegment<T1> Segment1 => new(_baseAddress, _length1);
    public MemorySegment<T2> Segment2 => new(_baseAddress + (sizeof(T1) * _length1), _length2);
    public MemorySegment<T3> Segment3 => new(_baseAddress + (sizeof(T1) * _length1) + (sizeof(T2) * _length2), _length3);
    public MemorySegment<T4> Segment4 => new(_baseAddress + (sizeof(T1) * _length1) + (sizeof(T2) * _length2) + (sizeof(T3) * _length3), _length4);

    #endregion

    #endregion

    #region Fields

    private readonly byte* _baseAddress;
    private readonly int _length1;
    private readonly int _length2;
    private readonly int _length3;
    private readonly int _length4;

    #endregion

    #region Constructors

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

    #endregion
}


public readonly unsafe struct MemorySegments<T1, T2, T3, T4, T5> where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged
{
    #region Public APIs

    #region Properties

    public MemorySegment<T1> Segment1 => new(_baseAddress, _length1);
    public MemorySegment<T2> Segment2 => new(_baseAddress + (sizeof(T1) * _length1), _length2);
    public MemorySegment<T3> Segment3 => new(_baseAddress + (sizeof(T1) * _length1) + (sizeof(T2) * _length2), _length3);
    public MemorySegment<T4> Segment4 => new(_baseAddress + (sizeof(T1) * _length1) + (sizeof(T2) * _length2) + (sizeof(T3) * _length3), _length4);
    public MemorySegment<T5> Segment5 => new(_baseAddress + (sizeof(T1) * _length1) + (sizeof(T2) * _length2) + (sizeof(T3) * _length3) + (sizeof(T4) * _length4), _length5);

    #endregion

    #endregion

    #region Fields

    private readonly byte* _baseAddress;
    private readonly int _length1;
    private readonly int _length2;
    private readonly int _length3;
    private readonly int _length4;
    private readonly int _length5;

    #endregion

    #region Constructors

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

    #endregion
}