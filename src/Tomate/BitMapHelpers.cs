using System.Numerics;

namespace Tomate;

public unsafe struct Block
{
    public int Offset;
    public int Size;

    public Span<T> ToSpan<T>(void* baseAddr) where T : unmanaged
    {
        return new Span<T>((byte*)baseAddr + Offset, Size/sizeof(T));
    }
}

public unsafe struct MemoryBlock
{
    public byte* BaseAddr;
    public int Size;

    public Span<T> ToSpan<T>() where T : unmanaged => new(BaseAddr, Size / sizeof(T));
    public static implicit operator Span<byte>(MemoryBlock block)
    {
        return new Span<byte>(block.BaseAddr, block.Size);
    }
}

public static class BitMapHelpers
{
    public static int FindFreeBitConcurrent(this Span<ulong> map)
    {
        var l = map.Length;
        for (int i = 0; i < l; i++)
        {
            var v = map[i];
            if (v == ulong.MaxValue) continue;

            var bit = BitOperations.TrailingZeroCount(~v);

            var mask = 1UL << bit;
            if ((Interlocked.Or(ref map[i], mask) & mask) != 0)     // Check for concurrent bit set, recurse if another thread beat us at setting this bit
            {
                return FindFreeBitConcurrent(map);
            }

            return i * 8 + bit;
        }
        return -1;
    }
}