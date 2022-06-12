using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Tomate;

public struct MemoryView<T> where T : unmanaged
{
    public readonly MemorySegment<T> MemorySegment;
    public bool IsEmpty => MemorySegment.IsEmpty;
    public int Position
    {
        get => _position;
        set
        {
            if ((uint)value >= MemorySegment.Length)
            {
                ThrowHelper.OutOfRange($"The given value is out of range, max position allowed is {MemorySegment.Length-1}");
                return;
            }
            _position = value;
        }
    }

    public bool IsFull => _position >= MemorySegment.Length;
    public int Count => _position;

    public unsafe ref T this[int index]
    {
        get
        {
            Debug.Assert((uint)index < MemorySegment.Length);
            return ref Unsafe.AsRef<T>(MemorySegment.Address + index);
        }
    }

    private int _position;

    public MemoryView(MemorySegment<T> memorySegment)
    {
        MemorySegment = memorySegment;
        _position = 0;
    }

    public void Reset()
    {
        _position = 0;
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
    public unsafe bool Reserve(int length, out Span<T> storeArea)
    {
        if (_position + length > MemorySegment.Length)
        {
            storeArea = null;
            return false;
        }

        storeArea = new Span<T>(MemorySegment.Address + _position, sizeof(T) * length);
        _position += length;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool Reserve(int length, out int index)
    {
        if (_position + length > MemorySegment.Length)
        {
            index = -1;
            return false;
        }

        index = _position;
        _position += length;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool Reserve(out int index)
    {
        if (_position + 1 > MemorySegment.Length)
        {
            index = -1;
            return false;
        }

        index = _position;
        ++_position;
        return true;
    }

    public bool Reserve<TD>(int length, out Span<TD> storeArea) where TD : unmanaged
    {
        storeArea = default;
        return true;
    }
}