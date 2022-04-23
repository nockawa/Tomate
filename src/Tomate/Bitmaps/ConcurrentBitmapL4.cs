using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace Tomate;

/// <summary>
/// This type allows to Allocate/Free bits in a bitmap in a concurrent friendly and scalable fashion.
/// It is possible to allocate from 1 to 64 consecutive bits.
/// </summary>
/// <remarks>
/// <para>Thread Safety: safe, designed for concurrent use.</para>
/// <para>
/// Usage:
/// Typically designed to implement an occupancy map, size can be in million range.
/// The required size to store n-bits can be known by calling <see cref="ComputeRequiredSize"/>.
/// You have a Memory Block that can store x elements and you want to reserve/free these elements in a concurrent fashion.
/// The user will call <see cref="AllocateBits"/> to allocate x consecutive elements and <see cref="FreeBits"/> to free them.
/// If allocation fails, the user can decide to wait/retry or give up, based on the concurrent usage of the instance.
/// </para>
/// <para>
/// Implementation:
/// There are four levels:
///  - The L0 stores the bits, packed by 64-bits long.
///  - The L1 stores for each L0 long the size of the biggest free segment the long store. (e.g. 0xFFF000FFFFFFF00F would be 12)
///  - The L2 and L3 stores for each entry the max free segment of the 64 entries of the previous level. (e.g. 12 in L2[0] would mean that the biggest free
///    segment found in the L1[0-63] would be 12.
/// This allows us to skip 64, 4096, 262144 entries per test when looking for a free segment to allocate.
/// L0/L1 are always used, but L2/L3 are activated based on the bitmap size.
/// When you allocate 64bits, we are looking for an empty long in L0, as this type is concurrent friendly, setting the bits has to be an atomic operation so
/// we can allocate bits that overlap multiple long. (e.g. 0xFFFFFFFF00000000 0x00000000FFFFFFFF, can't be used to allocate 64 bits). This is important to
/// understand as allocating big sizes can lead to fragmentation and may not succeed because of it (fragmentation).
/// The implementation is entirely lock-less, relies on Interlocked And/Or/Add.
/// </para>
/// </remarks>
public struct ConcurrentBitmapL4
{
    // L2/L3 will be activated if this threshold is met
    const int levelThreshold = 4;

    private readonly MemorySegments<Header, long, byte, byte, byte> _data;

    private readonly unsafe Header* _header;
    private readonly MemorySegment<long> _l0 => _data.Segment2;
    private readonly MemorySegment<byte> _l1 => _data.Segment3;
    private readonly MemorySegment<byte> _l2 => _data.Segment4;
    private readonly MemorySegment<byte> _l3 => _data.Segment5;
    private readonly int AggregationLevelCount;
    private static readonly Vector256<byte> _64brocasted;

    static ConcurrentBitmapL4()
    {
        var b = Vector128.CreateScalar((byte)64);
        _64brocasted = Avx2.BroadcastScalarToVector256(b);
    }

    /// <summary>
    /// Compute the size in byte, required to store a bitmap of the given size.
    /// </summary>
    /// <param name="bitCount">The bit count to compute the size from</param>
    /// <returns>The size in byte.</returns>
    /// <remarks>
    /// L0/L1 levels are always used, L2/L3 will be activated based on the requested size.
    /// </remarks>
    public static unsafe int ComputeRequiredSize(int bitCount)
    {
        var (l0, l1, l2, l3) = ComputeStorageInternal(bitCount);
        return sizeof(Header) + l0 + l1 + l2 + l3;
    }

    public static ConcurrentBitmapL4 Create(int bitCount, MemorySegment storage) => new(bitCount, storage);
    public static ConcurrentBitmapL4 Map(int bitCount, MemorySegment storage) => new(storage);

    private static (int, int, int, int) ComputeStorageInternal(int bitCount)
    {
        var longCount = (bitCount + 63) / 64;
        var l0 = longCount * sizeof(long);
        var l1 = longCount;
        var l2 = (l1 + 63) / 64;
        var l3 = (l2 + 63) / 64;

        return (l0, l1, l2 >= levelThreshold ? l2 : 0, l3 >= levelThreshold ? l3 : 0);
    }

    /// <summary>
    /// Capacity of the bitmap, in bits.
    /// </summary>
    public int Capacity => _data.Segment1.AsRef().Capacity;

    /// <summary>
    /// Get the total count of bit set to one.
    /// </summary>
    public int TotalBitSet => _data.Segment1.AsRef().Count;

    /// <summary>
    /// <c>true</c> if the map is full, <c>false</c> if there are free bits.
    /// </summary>
    /// <remarks>
    /// A non full map doesn't mean you will succeed to allocate, unless you are requested a bit size of 1.
    /// </remarks>
    public bool IsFull => Capacity == TotalBitSet;

#if DEBUG
    public int LookupIterationCount => _lookupIterationCount;
    public int LookupCount => _lookupCount;
    private int _lookupIterationCount;
    private int _lookupCount;
#else
    public int LookupIterationCount => 0;
    public int LookupCount => 0;
#endif

    [StructLayout(LayoutKind.Sequential, Size = 32)]    // We have to keep the data stored after this struct aligned on 32bytes
    private struct Header
    {
        public int Capacity;
        public int Count;
    }

    private unsafe ConcurrentBitmapL4(int bitCount, MemorySegment storage)
    {
        Debug.Assert(storage.Length >= ComputeRequiredSize(bitCount));

        var (l0S, l1S, l2S, l3S) = ComputeStorageInternal(bitCount);
        _data = new MemorySegments<Header, long, byte, byte, byte>(storage, 1, l0S / sizeof(long), l1S, l2S, l3S);

        AggregationLevelCount = 1 + (l2S > 0 ? 1 : 0) + (l3S > 0 ? 1 : 0);

        _data.Segment1.ToSpan().Clear();
        _data.Segment2.ToSpan().Clear();
        _data.Segment3.ToSpan().Fill(64);
        _data.Segment4.ToSpan().Fill(64);
        _data.Segment5.ToSpan().Fill(64);

        _header = _data.Segment1.Address;
        _header->Capacity = bitCount;
        _header->Count = 0;

#if DEBUG
        _lookupCount = 0;
        _lookupIterationCount = 0;
#endif
    }

    private unsafe ConcurrentBitmapL4(MemorySegment segment)
    {
        _header = (Header*)segment.Address;
        var capacity = _header->Capacity;
        var (l0S, l1S, l2S, l3S) = ComputeStorageInternal(capacity);
        _data = new MemorySegments<Header, long, byte, byte, byte>(segment, 1, l0S / sizeof(long), l1S, l2S, l3S);

        AggregationLevelCount = 1 + (l2S > 0 ? 1 : 0) + (l3S > 0 ? 1 : 0);

#if DEBUG
        _lookupCount = 0;
        _lookupIterationCount = 0;
#endif
    }

    public unsafe bool SanityCheck(out string error)
    {
        var sb = new StringBuilder();
        var errorCount = 0;

        var l0l = _l0.Length;
        var setCount = 0;
        var l0ErrorCount = 0;
        var l1ErrorCount = 0;
        var l2ErrorCount = 0;
        var maxL1 = 0;
        var maxL2 = 0;
        for (int i = 0; i < l0l; i++)
        {
            var ulc = ComputeMaxFreeSegment((ulong)_l0[i]);
            if (_l1[i] != ulc)
            {
                ++l0ErrorCount;
            }
            setCount += BitOperations.PopCount((ulong)_l0[i]);

            maxL1 = Math.Max(ulc, maxL1);

            if ((i & 0x3F) == 0x3F)
            {
                if (_l2[i >> 6] != maxL1)
                {
                    ++l1ErrorCount;
                }
                maxL2 = Math.Max(maxL1, maxL2);
                maxL1 = 0;
            }

            if ((i & 0xFFF) == 0xFFF)
            {
                if (_l3[i >> 12] != maxL2)
                {
                    ++l2ErrorCount;
                }
                maxL2 = 0;
            }
        }

        if (setCount != _header->Count)
        {
            sb.AppendLine($"L0 count mismatch, {setCount} in bit-field for {_header->Count} in TotalBitSet");
            ++errorCount;
        }

        if (l0ErrorCount != 0)
        {
            sb.AppendLine($"L0/L1 mismatch, {l0ErrorCount} error detected");
            ++errorCount;
        }

        if (l1ErrorCount != 0)
        {
            sb.AppendLine($"L1/L2 mismatch, {l1ErrorCount} error detected");
            ++errorCount;
        }

        if (l2ErrorCount != 0)
        {
            sb.AppendLine($"L2/L3 mismatch, {l2ErrorCount} error detected");
            ++errorCount;
        }

        error = sb.ToString();
        return errorCount == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private unsafe bool SetL0(int bitIndex, int requestedLength)
    {
        var requestedmask = (requestedLength == 64) ? -1 : ((1L << requestedLength) - 1) << (bitIndex & 0x3F);
        var l0 = _l0.ToSpan();
        var l0i = bitIndex >> 6;
        var prevValue = Interlocked.Or(ref l0[l0i], requestedmask);
        if ((prevValue & requestedmask) != 0L)
        {
            // Restore the previous value, removing the bits we set with the or that weren't concurrently changed
            Interlocked.And(ref l0[l0i], ~prevValue);
            return false;
        }

        Interlocked.Add(ref Unsafe.AsRef<int>(_header->Count), requestedLength);
        UpdateAggregatedLevels(l0i);

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private unsafe bool ClearL0(int bitIndex, int requestedLength)
    {
        var requestedmask = (requestedLength == 64) ? -1 : ((1L << requestedLength) - 1) << (bitIndex & 0x3F);
        var l0 = _l0.ToSpan();
        var l0i = bitIndex >> 6;
        Interlocked.And(ref l0[l0i], ~requestedmask);

        Interlocked.Add(ref Unsafe.AsRef<int>(_header->Count), -requestedLength);
        UpdateAggregatedLevels(l0i);

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization|MethodImplOptions.AggressiveInlining)]
    private unsafe void UpdateAggregatedLevels(int l0i)
    {
        var l0 = _l0.ToSpan();
        var levelCount = AggregationLevelCount;

        var v0 = l0[l0i];
        var v0b = ComputeMaxFreeSegment((ulong)v0);
        var l1 = _l1.ToSpan();

        var prevLevelMax = l1[l0i];
        l1[l0i] = (byte)v0b;

        var curLevelMaxSegment = v0b;
        var curLevelIndex = l0i;
        for (int i = 2; i <= levelCount; i++)
        {
            var curLevel = (i == 2) ? _l2 : _l3;
            var prevLevel = (i == 2) ? _l1 : _l2;

            // If the aggregated level already contains a bigger value, we have nothing to update
            var curMax = curLevel[curLevelIndex >> 6];
            if (curMax >= curLevelMaxSegment && prevLevelMax != curMax)
            {
                return;
            }

            // We want to aggregate the 64 max segments size of the previous level to this level, which is finding with byte has the biggest value
            // But we want to do it fast, so let's use SIMD
            var curLevelBank = curLevelIndex & ~0x3F;
            var v1 = Avx.LoadVector256(prevLevel.Address + curLevelBank);        // Load 32 bytes
            var v2 = Avx.LoadVector256(prevLevel.Address + curLevelBank + 32);   //  and the rest
            v1 = Avx2.Subtract(_64brocasted, v1);                                           // We want the max, but SIMD only does min on horizontal, so let's invert the values
            v2 = Avx2.Subtract(_64brocasted, v2);
            var v = Avx2.Min(v1, v2);                                         // Min of the two 32 bytes lanes

            v = Avx2.Min(v, Avx2.ShiftRightLogical(v.AsInt16(), 8).AsByte());    // Nice trick to get the min of the remaining values and shifting from byte to short

            var lv = v.GetLower().AsUInt16();                                          // Need to get a v128 packed as ushort
            var hv = v.GetUpper().AsUInt16();
            lv = Sse41.MinHorizontal(lv);                                                               // Compute the min of the 8 ushort
            hv = Sse41.MinHorizontal(hv);
            var m = (byte)(64 - Math.Min(lv.GetElement(0), hv.GetElement(0)));              // Get the max value from the min of the two lanes that we invert back

            prevLevelMax = curMax;

            // If the value didn't change, we have nothing more to do as the upper level won't need to be recomputed
            if (prevLevelMax == m)
            {
                break;
            }

            // The store of the value as it is could not play well with multi-thread but what are the odds and the consequences?
            curLevel[curLevelIndex >> 6] = m;

            curLevelMaxSegment = m;
            curLevelIndex >>= 6;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int ComputeMaxFreeSegment(ulong n)
    {
        if (n == 0) return 64;
        if (n == ulong.MaxValue) return 0;

        var l = 1;
        n = ~n;
        var r = n;

        for (int i = 0; i < 64; i += 8)
        {
            r &= (n >> (i + 1)); if (r == 0) break; ++l;
            r &= (n >> (i + 2)); if (r == 0) break; ++l;
            r &= (n >> (i + 3)); if (r == 0) break; ++l;
            r &= (n >> (i + 4)); if (r == 0) break; ++l;
            r &= (n >> (i + 5)); if (r == 0) break; ++l;
            r &= (n >> (i + 6)); if (r == 0) break; ++l;
            r &= (n >> (i + 7)); if (r == 0) break; ++l;

            if (i == 56) break;
            r &= (n >> (i + 8)); if (r == 0) break; ++l;
        }

        return l;
    }

#if DEBUG
    [Conditional("DEBUG")]
    private void IncrementLookupIteration() => Interlocked.Increment(ref _lookupIterationCount);

    [Conditional("DEBUG")]
    private void IncrementLookup() => Interlocked.Increment(ref _lookupCount);
#else
    [Conditional("DEBUG")]
    private void IncrementLookupIteration() {}
    [Conditional("DEBUG")]
    private void IncrementLookup() {}
#endif
    
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int AllocateBits(int requestedLength)
    {
        if (IsFull)
        {
            return -1;
        }
        var l0 = _l0;
        var l1 = _l1;
        var l2 = _l2;
        var l3 = _l3;
        var l1l = l1.Length;
        var l2l = l2.Length;
        var l3l = l3.Length;
        var maxLevel = AggregationLevelCount;
        var capacity = Capacity;
        var mask = (requestedLength == 64) ? long.MaxValue : ((1L << requestedLength) - 1);
        var maxIt = 64 - requestedLength;
        IncrementLookup();

StartOver:
        var curBitIndex = 0;

        while (curBitIndex < capacity)
        {
RestartL3:
            if (maxLevel == 3)
            {
                var l3Index = curBitIndex >> 18;
                while (l3Index < l3l && l3[l3Index] < requestedLength)
                {
                    IncrementLookupIteration();
                    ++l3Index;
                }
                if (l3Index == l3l)
                {
                    if (IsFull)
                    {
                        return -1;
                    }
                    goto StartOver;
                }

                curBitIndex = l3Index << 18;
            }
RestartL2:
            if (maxLevel >= 2)
            {
                var l2Index = curBitIndex >> 12;
                var l2End = (maxLevel==3) ? (l2Index + 64) : l2l;
                while (l2Index < l2End && l2[l2Index] < requestedLength)
                {
                    IncrementLookupIteration();
                    ++l2Index;
                }
                if (l2Index == l2l)
                {
                    if (IsFull)
                    {
                        return -1;
                    }
                    goto StartOver;
                }

                if (l2Index == l2End)
                {
                    curBitIndex = l2Index >> 6;
                    goto RestartL3;
                }

                curBitIndex = l2Index << 12;
            }
RestartL1:
            var l1Index = curBitIndex >> 6;
            var l1End = (maxLevel >= 2) ? (l1Index + 64) : l1l;
            while (l1Index < l1End && l1[l1Index] < requestedLength)
            {
                IncrementLookupIteration();
                ++l1Index;
            }
            if (l1Index == l1l)
            {
                if (IsFull)
                {
                    return -1;
                }
                goto StartOver;
            }

            if (l1Index == l1End)
            {
                curBitIndex = l1Index >> 6;
                goto RestartL2;
            }

            curBitIndex = l1Index << 6;

            var val = l0[l1Index];
            if (BitOperations.PopCount((ulong)val) > maxIt)
            {
                IncrementLookupIteration();
                curBitIndex += 64;
                goto RestartL1;
            }

            var j = 0;
            while (j <= maxIt)
            {
                var r = BitOperations.TrailingZeroCount(~val);
                j += r;
                if (j > maxIt)
                {
                    curBitIndex += 64;
                    goto RestartL1;
                }

                val >>= r;
                if ((val & mask) == 0 && SetL0(l1Index * 64 + j, requestedLength))
                {
                    return l1Index * 64 + j;
                }
                r = BitOperations.TrailingZeroCount(val);
                val >>= r;
                j += r;
            }
        }
        return -1;
    }

    public void FreeBits(int index, int requestedLength)
    {
        ClearL0(index, requestedLength);
    }
}