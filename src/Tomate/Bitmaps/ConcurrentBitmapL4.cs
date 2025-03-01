﻿using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Text;
using JetBrains.Annotations;

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
/// we can't allocate bits that overlap multiple longs. (e.g. 0xFFFFFFFF00000000 0x00000000FFFFFFFF, can't be used to allocate 64 bits). This is important to
/// understand as allocating big sizes can lead to fragmentation and may not succeed because of it (fragmentation).
/// </para>
/// </remarks>
[PublicAPI]
public struct ConcurrentBitmapL4
{
    #region Constants

    private static readonly Vector256<byte> _64broadcast256;
    private static readonly Vector128<byte> _64broadcast128;

    // L2/L3 will be activated if this threshold is met
    const int LevelThreshold = 4;

    #endregion

    #region Public APIs

    #region Properties

    private readonly MemorySegment<ulong> _l0 => _data.Segment2;
    private readonly MemorySegment<byte> _l1 => _data.Segment3;
    private readonly MemorySegment<byte> _l2 => _data.Segment4;
    private readonly MemorySegment<byte> _l3 => _data.Segment5;

    /// <summary>
    /// Capacity of the bitmap, in bits.
    /// </summary>
    public int Capacity => _data.Segment1.AsRef().Capacity;

    /// <summary>
    /// <c>true</c> if the map is full, <c>false</c> if there are free bits.
    /// </summary>
    /// <remarks>
    /// A non full map doesn't mean you will succeed to allocate, unless you are requested a bit size of 1.
    /// </remarks>
    public bool IsFull => Capacity == TotalBitSet;

    public int LookupCount => _lookupCount;

    public int LookupIterationCount => _lookupIterationCount;

    /// <summary>
    /// Get the total count of bit set to one.
    /// </summary>
    public int TotalBitSet => _data.Segment1.AsRef().Count;

    #endregion

    #region Methods

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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int AllocateBits(int requestedLength)
    {
        // Looking for early exit, doesn't mean we'll find a spot for sure
        if (IsFull || (TotalBitSet + requestedLength) > Capacity)
        {
            return -1;
        }
        var l0 = _l0;
        var l1 = _l1;
        var l2 = _l2;
        var l3 = _l3;
        var l1Length = l1.Length;
        var l2Length = l2.Length;
        var l3Length = l3.Length;
        var maxLevel = _aggregationLevelCount;
        var capacity = Capacity;
        var mask = (requestedLength == 64) ? ulong.MaxValue : ((1UL << requestedLength) - 1);
        var maxIt = 64 - requestedLength;
        IncrementLookup();

        var curBitIndex = 0;

        while (curBitIndex < capacity)
        {
            RestartL3:
            if (maxLevel == 3)
            {
                var l3Index = curBitIndex >> 18;
                while (l3Index < l3Length && l3[l3Index] < requestedLength)
                {
                    IncrementLookupIteration();
                    ++l3Index;
                }
                if (l3Index == l3Length)
                {
                    return -1;
                }

                curBitIndex = l3Index << 18;
            }
            RestartL2:
            if (maxLevel >= 2)
            {
                var l2Index = curBitIndex >> 12;
                var l2End = Math.Min((maxLevel==3) ? (l2Index + 64) : l2Length, l2.Length);
                while (l2Index < l2End && l2[l2Index] < requestedLength)
                {
                    IncrementLookupIteration();
                    ++l2Index;
                }
                if (l2Index == l2Length)
                {
                    return -1;
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
            var l1End = Math.Min((maxLevel >= 2) ? (l1Index + 64) : l1Length, l1.Length);
            while (l1Index < l1End && l1[l1Index] < requestedLength)
            {
                IncrementLookupIteration();
                ++l1Index;
            }
            if (l1Index == l1Length)
            {
                return -1;
            }

            if (l1Index == l1End)
            {
                curBitIndex = l1Index >> 6;
                goto RestartL2;
            }

            curBitIndex = l1Index << 6;

            var val = l0[l1Index];
            if (BitOperations.PopCount(val) > maxIt)
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

    public unsafe bool SanityCheck(out string error)
    {
        var sb = new StringBuilder();
        var errorCount = 0;

        var l0Length = _l0.Length;
        var setCount = 0;
        var l0ErrorCount = 0;
        var l1ErrorCount = 0;
        var l2ErrorCount = 0;
        var maxL1 = 0;
        var maxL2 = 0;
        for (int i = 0; i < l0Length; i++)
        {
            var ulc = ComputeMaxFreeSegment(_l0[i]);
            if (_l1[i] != ulc)
            {
                ++l0ErrorCount;
            }
            setCount += BitOperations.PopCount(_l0[i]);

            maxL1 = Math.Max(ulc, maxL1);

            if (_aggregationLevelCount > 1)
            {
                if ((i & 0x3F) == 0x3F)
                {
                    if (_l2[i >> 6] != maxL1)
                    {
                        ++l1ErrorCount;
                    }
                    maxL2 = Math.Max(maxL1, maxL2);
                    maxL1 = 0;
                }

                if (_aggregationLevelCount > 2 && (i & 0xFFF) == 0xFFF)
                {
                    if (_l3[i >> 12] != maxL2)
                    {
                        ++l2ErrorCount;
                    }
                    maxL2 = 0;
                }
            }
        }

        if (setCount != (_header->Count + _unaddressableBitCount))
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

    #endregion

    #endregion

    #region Fields

    private readonly int _aggregationLevelCount;

    private readonly MemorySegments<Header, ulong, byte, byte, byte> _data;

    private readonly unsafe Header* _header;
    private int _lookupCount;
    private int _lookupIterationCount;
    private int _unaddressableBitCount;

    #endregion

    #region Constructors

    static ConcurrentBitmapL4()
    {
        var b = Vector128.CreateScalar((byte)64);

        if (Avx2.IsSupported)
        {
            _64broadcast256 = Avx2.BroadcastScalarToVector256(b);
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            _64broadcast128 = Vector128.Create((byte)64);
        }
    }

    private unsafe ConcurrentBitmapL4(int capacity, MemorySegment storage)
    {
        Debug.Assert(storage.Length >= ComputeRequiredSize(capacity));

        var (l0S, l1S, l2S, l3S) = ComputeStorageInternal(capacity);
        _data = new MemorySegments<Header, ulong, byte, byte, byte>(storage, 1, l0S / sizeof(ulong), l1S, l2S, l3S);

        _aggregationLevelCount = 1 + (l2S > 0 ? 1 : 0) + (l3S > 0 ? 1 : 0);

        _data.Segment1.ToSpan().Clear();
        _data.Segment2.ToSpan().Clear();
        _data.Segment3.ToSpan().Fill(64);
        _data.Segment4.ToSpan().Fill(64);
        _data.Segment5.ToSpan().Fill(64);

        // The bitcount may not be a multiple of 64, so we need to mark the last long with the bit that can't be allocated
        var mask = (ulong)(~((1L << (capacity % 64)) - 1));
        if (mask != ulong.MaxValue)
        {
            var lastIndex = _data.Segment2.Length - 1;
            _unaddressableBitCount = BitOperations.PopCount(mask);
            _data.Segment2.ToSpan()[lastIndex] = mask;
            UpdateAggregatedLevels(lastIndex);
        }
        
        _header = _data.Segment1.Address;
        _header->AccessControl = default;
        _header->Capacity = capacity;
        _header->Count = 0;

        _lookupCount = 0;
        _lookupIterationCount = 0;
    }

    private unsafe ConcurrentBitmapL4(MemorySegment segment)
    {
        _header = (Header*)segment.Address;
        var capacity = _header->Capacity;
        var (l0S, l1S, l2S, l3S) = ComputeStorageInternal(capacity);
        _data = new MemorySegments<Header, ulong, byte, byte, byte>(segment, 1, l0S / sizeof(long), l1S, l2S, l3S);

        var mask = (ulong)(~((1L << (capacity % 64)) - 1));
        if (mask != ulong.MaxValue)
        {
            _unaddressableBitCount = BitOperations.PopCount(mask);
        }

        _aggregationLevelCount = 1 + (l2S > 0 ? 1 : 0) + (l3S > 0 ? 1 : 0);

        _lookupCount = 0;
        _lookupIterationCount = 0;
    }

    #endregion

    #region Private methods

    private static (int, int, int, int) ComputeStorageInternal(int bitCount)
    {
        var longCount = (bitCount + 63) / 64;
        var l0 = longCount * sizeof(long);
        var l1 = longCount;
        var l2 = (l1 + 63) / 64;
        var l3 = (l2 + 63) / 64;

        return (l0, l1, l2 >= LevelThreshold ? l2 : 0, l3 >= LevelThreshold ? l3 : 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private unsafe bool ClearL0(int bitIndex, int requestedLength)
    {
        var requestedMask = (requestedLength == 64) ? ulong.MaxValue : ((1UL << requestedLength) - 1) << (bitIndex & 0x3F);
        var l0 = _l0.ToSpan();
        var l0Index = bitIndex >> 6;
        
        _header->AccessControl.EnterExclusiveAccess();

        l0[l0Index] &= ~requestedMask;
        _header->Count -= requestedLength;
        UpdateAggregatedLevels(l0Index);

        _header->AccessControl.ExitExclusiveAccess();
        return true;
    }

    [Conditional("DEBUG")]
    private void IncrementLookup() => Interlocked.Increment(ref _lookupCount);

    [Conditional("DEBUG")]
    private void IncrementLookupIteration() => Interlocked.Increment(ref _lookupIterationCount);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private unsafe bool SetL0(int bitIndex, int requestedLength)
    {
        var requestedMask = ((requestedLength == 64) ? ulong.MaxValue : ((1UL << requestedLength) - 1) << (bitIndex & 0x3F));
        var l0 = _l0.ToSpan();
        var l0Index = bitIndex >> 6;

        _header->AccessControl.EnterExclusiveAccess();
        if ((l0[l0Index] & requestedMask) != 0)
        {
            _header->AccessControl.ExitExclusiveAccess();
            return false;
        }

        l0[l0Index] |= requestedMask;
        _header->Count += requestedLength;
        
        UpdateAggregatedLevels(l0Index);

        _header->AccessControl.ExitExclusiveAccess();
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization|MethodImplOptions.AggressiveInlining)]
    private unsafe void UpdateAggregatedLevels(int l0Index)
    {
        var l0 = _l0.ToSpan();
        var levelCount = _aggregationLevelCount;

        var v0 = l0[l0Index];
        var v0Max = ComputeMaxFreeSegment(v0);
        var l1 = _l1.ToSpan();

        var prevLevelMax = l1[l0Index];
        l1[l0Index] = (byte)v0Max;

        var curLevelMaxSegment = v0Max;
        var curLevelIndex = l0Index;
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

            byte m = default;              // Get the max value from the min of the two lanes that we invert back
            
            // Intel/AMD implementation
            if (Avx2.IsSupported)
            {
                // We want to aggregate the 64 max segments size of the previous level to this level, which is finding with byte has the biggest value
                // But we want to do it fast, so let's use SIMD
                var curLevelBank = curLevelIndex & ~0x3F;
                var v1 = Avx.LoadVector256(prevLevel.Address + curLevelBank);        // Load 32 bytes
                var v2 = Avx.LoadVector256(prevLevel.Address + curLevelBank + 32);   //  and the rest

                v1 = Avx2.Subtract(_64broadcast256, v1);                                           // We want the max, but SIMD only does min on horizontal, so let's invert the values
                v2 = Avx2.Subtract(_64broadcast256, v2);
                var v = Avx2.Min(v1, v2);                                         // Min of the two 32 bytes lanes

                v = Avx2.Min(v, Avx2.ShiftRightLogical(v.AsInt16(), 8).AsByte());    // Nice trick to get the min of the remaining values and shifting from byte to short

                var lv = v.GetLower().AsUInt16();                                          // Need to get a v128 packed as ushort
                var hv = v.GetUpper().AsUInt16();
                lv = Sse41.MinHorizontal(lv);                                                               // Compute the min of the 8 ushort
                hv = Sse41.MinHorizontal(hv);
                m = (byte)(64 - Math.Min(lv.GetElement(0), hv.GetElement(0)));
            }
            
            // ARM implementation
            else if (AdvSimd.Arm64.IsSupported)
            {
                var curLevelBank = curLevelIndex & ~0x3F;
                var v1 = AdvSimd.LoadVector128(prevLevel.Address + curLevelBank);        // Load 16 bytes
                var v2 = AdvSimd.LoadVector128(prevLevel.Address + curLevelBank + 16);   // Load 16 bytes
                var v3 = AdvSimd.LoadVector128(prevLevel.Address + curLevelBank + 32);   // Load 16 bytes
                var v4 = AdvSimd.LoadVector128(prevLevel.Address + curLevelBank + 48);   // Load 16 bytes

                v1 = AdvSimd.Max(AdvSimd.Max(v1, v2), AdvSimd.Max(v3, v4));
                m = AdvSimd.Arm64.MaxAcross(v1)[0];
            }
            
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

    #endregion

    #region Inner types

    [StructLayout(LayoutKind.Sequential, Size = 32)]    // We have to keep the data stored after this struct aligned on 32bytes
    private struct Header
    {
        public int Capacity;
        public int Count;
        public AccessControl AccessControl;
    }

    #endregion
}