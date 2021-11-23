using System.Numerics;

namespace Tomate;

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