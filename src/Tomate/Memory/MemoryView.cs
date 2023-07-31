using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Tomate;

public unsafe struct MemoryView<T> where T : unmanaged
{
    private T* _cur;
    private T* _end;

    public readonly MemorySegment<T> MemorySegment;
    public bool IsDefault => MemorySegment.IsDefault;

    /// <summary>
    /// Get/set the position
    /// </summary>
    /// <remarks>
    /// There is no bound check, if the new position is out of bound then <see cref="IsEndReached"/> will be return <c>true</c>
    /// </remarks>
    public int Position
    {
        get => (int)(((long)_cur - (long)MemorySegment.Address) / sizeof(T));
        set
        {
            if (value < 0)
            {
                ThrowHelper.OutOfRange("Position can't be a negative value");
            }
            _cur = MemorySegment.Address + value;
        }
    }

    public bool IsEndReached => _cur >= _end;
    public int Length => MemorySegment.Length;

    /// <summary>
    /// Random access, doesn't change <see cref="Position"/>
    /// </summary>
    /// <param name="index">Index of the item to access</param>
    /// <returns>Reference to the item for read/write operations</returns>
    public ref T this[int index]
    {
        get
        {
            Debug.Assert((uint)index < MemorySegment.Length);
            return ref Unsafe.AsRef<T>(MemorySegment.Address + index);
        }
    }

    public MemoryView(MemorySegment<T> memorySegment)
    {
        MemorySegment = memorySegment;
        _cur = MemorySegment.Address;
        _end = _cur + MemorySegment.Length;
    }

    public void Reset()
    {
        _cur = MemorySegment.Address;
        _end = _cur + MemorySegment.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool Store(Span<T> data)
    {
        if (Reserve(data.Length, out Span<T> dest) == false)
        {
            return false;
        }

        data.CopyTo(dest);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool Reserve(int length, out Span<T> storeArea)
    {
        if (_cur + length > _end)
        {
            storeArea = null;
            return false;
        }

        storeArea = new Span<T>(_cur, sizeof(T) * length);
        _cur += length;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool Reserve(int length, out int index)
    {
        if (_cur + length > _end)
        {
            index = -1;
            return false;
        }

        index = Position;
        _cur += length;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool BeginReserve(int maxSizeRequired, out Span<T> storeArea)
    {
        if (_cur + maxSizeRequired > _end)
        {
            storeArea = null;
            return false;
        }

        storeArea = new Span<T>(_cur, sizeof(T) * maxSizeRequired);

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool GetReservedArea(int maxLength, out Span<T> area)
    {
        if (_cur + maxLength > _end)
        {
            area = default;
            return false;
        }

        area = new Span<T>(_cur, sizeof(T) * maxLength);

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void EndReserve(int writtenSize)
    {
        _cur += writtenSize;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool Reserve(out int index)
    {
        if (_cur + 1 > _end)
        {
            index = -1;
            return false;
        }

        index = Position;
        ++_cur;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool Fetch(int length, out MemorySegment<T> readArea)
    {
        if (_cur + length > _end)
        {
            readArea = default;
            return false;
        }

        readArea = new MemorySegment<T>(_cur, length);
        _cur += length;

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool Fetch<TD>(int length, out MemorySegment<TD> readArea) where TD : unmanaged
    {
        if (!Fetch(length * sizeof(TD), out var seg))
        {
            readArea = default;
            return false;
        }

        readArea = seg.Cast<TD>();
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public TR Fetch<TR>() where TR : unmanaged
    {
        Debug.Assert((byte*)_cur + sizeof(TR) <= _end);

        var res = *(TR*)_cur;
        _cur = (T*)((byte*)_cur + sizeof(TR));
        return res;
    }

    public bool Skip(int offset)
    {
        if (_cur + offset > _end)
        {
            return false;
        }

        _cur += offset;
        return true;
    }
}