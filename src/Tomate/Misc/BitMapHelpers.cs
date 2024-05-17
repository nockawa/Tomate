using System.Diagnostics;
using System.Numerics;
using JetBrains.Annotations;

namespace Tomate;

[PublicAPI]
public static class BitMapHelpers
{
    #region Public APIs

    #region Methods

    public static bool ClearBitConcurrent(this Span<ulong> map, int index)
    {
        var offset = index >> 6;
        Debug.Assert(offset < map.Length, "Index out of range");
        var bitMask = ~(1UL << (index & 0x3F));

        return (Interlocked.And(ref map[offset], bitMask) & bitMask) != 0;
    }

    public static bool ClearBitsConcurrent(this Span<ulong> map, int index, int bitLength)
    {
        var offset = index >> 6;
        Debug.Assert(offset < map.Length, "Index out of range");
        var bitMask = ~((1UL << bitLength) - 1UL) << (index & 0x3F);

        return (Interlocked.And(ref map[offset], bitMask) & bitMask) != 0;
    }

    public static bool IsBitSet(this Span<ulong> map, int index)
    {
        var offset = index >> 6;
        Debug.Assert(offset < map.Length, "Index out of range");
        var bitMask = 1UL << (index & 0x3F);

        return (map[offset] & bitMask) != 0;
    }

    /// <summary>
    /// Find the first bit set starting after the given index
    /// </summary>
    /// <param name="map">The bitfield map</param>
    /// <param name="index">
    /// The previous index of a bit found, must be -1 for the first call.
    /// Upon return, will contain the index of the found set bit or -1 if there was none (and the method will return <c>false</c>).
    /// </param>
    /// <returns>Returns <c>true</c> if the search was successful and a set bit was found, or <c>false</c>.</returns>
    public static bool FindSetBitConcurrent(this Span<ulong> map, ref int index)
    {
        var offset = ++index >> 6;
        var mask = ~((1UL << (index & 0x3F)) - 1);

        while (offset < map.Length)
        {
            var v = map[offset] & mask;
            if (v == 0)
            {
                ++offset;
                mask = ulong.MaxValue;
                continue;
            }

            var bit = BitOperations.TrailingZeroCount(v);
            index = offset * 64 + bit;
            return true;
        }

        index = -1;
        return false;
    }
    
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

    public static int FindFreeBitsConcurrent(this Span<ulong> map, int bitLength)
    {
        Debug.Assert(bitLength is > 0 and <= 64, "BitLength is invalid, it muse be [1-64]");

        var mask = (bitLength==64) ? ulong.MaxValue : ((1UL << bitLength) - 1);
        var maxIt = 64 - bitLength;
        var size = map.Length;
        for (int i = 0; i < size; ++i)
        {
            var val = map[i];
            if (val == ulong.MaxValue) continue;

            var j = 0;
            while (j <= maxIt)
            {
                var r = BitOperations.TrailingZeroCount(~val);
                j += r;
                if (j > maxIt) break;

                val >>= r;
                var rmask = mask << j;
                if ((val & mask) == 0 && ((Interlocked.Or(ref map[i], rmask) & rmask) == 0ul))
                {
                    return i * 64 + j;
                }
                r = BitOperations.TrailingZeroCount(val);
                val >>= r;
                j += r;
            }
        }

        return -1;
    }

    public static int FindMaxBitSet(this Span<ulong> map)
    {
        for (int i = map.Length - 1; i >= 0; i--)
        {
            var val = map[i];
            if (val != 0)
            {
                var r = 64 - BitOperations.LeadingZeroCount(val) - 1;
                return i * 64 + r;
            }
        }

        return -1;
    }

    /// <summary>
    /// Switch a bit from 0 to 1, concurrent friendly.
    /// </summary>
    /// <param name="map">The bitmap storing the bits</param>
    /// <param name="index">The index of the bit to set</param>
    /// <returns>
    /// <c>true</c> if the bit was successfully set, or <c>false</c> if another thread beat us to set this bit.
    /// </returns>
    /// <remarks>
    /// For performance sake, there is no exception being thrown, but Debug.Assert fill fire for incorrect calls.
    /// </remarks>
    public static bool SetBitConcurrent(this Span<ulong> map, int index)
    {
        var offset = index >> 6;
        Debug.Assert(offset < map.Length, "Index out of range");
        var bitMask = 1ul << (index & 0x3F);

        return (Interlocked.Or(ref map[offset], bitMask) & bitMask) == 0;
    }

    /// <summary>
    /// Switch several contiguous (all stored in the same ulong) bits from 0 to 1, concurrent friendly.
    /// </summary>
    /// <param name="map">The bitmap storing the bits</param>
    /// <param name="index">The index of the first bit to set</param>
    /// <param name="bitLength">The length, must be at least 1 and no more than 64.</param>
    /// <returns>
    /// <c>true</c> if the bits was successfully set, or <c>false</c> if another thread beat us to set at least one bit.
    /// </returns>
    /// <remarks>
    /// As this is a concurrent friendly operation, only one ulong can be changed at a time, so the bits to change must be stored in the same ulong.
    /// For instance an index of 62 with a bitLength of 3 will generate a Debug Assert as it is addressing more than one ulong.
    /// For performance sake, there is no exception being thrown, but several Debug.Assert
    /// </remarks>
    public static bool SetBitsConcurrent(this Span<ulong> map, int index, int bitLength)
    {
        Debug.Assert(bitLength is > 0 and <= 64, "BitLength is invalid, it muse be [1-64]");

        var offset = index >> 6;
        Debug.Assert(offset < map.Length, "Index out of range");
        var mask = (1UL << bitLength) - 1;
        var bitMask = mask << (index & 0x3F);

        Debug.Assert(BitOperations.PopCount(mask) == BitOperations.PopCount(bitMask), 
            "Bit Length and Bit Index are incompatible, the resulting bitmask must be aligned in a 64bits number. (e.g. an index of 62 with a length of 3 won't work " +
            "because the resulting mask goes above 64bits to overlap two ulong).");

        return (Interlocked.Or(ref map[offset], bitMask) & bitMask) == 0;
    }

    #endregion

    #endregion
}