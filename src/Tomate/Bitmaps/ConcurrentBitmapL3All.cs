using System.Numerics;
using System.Runtime.CompilerServices;

namespace Tomate;

/// <summary>
/// Concurrent storage of a BitMap with three levels (two of aggregation) for efficient looking for empty bit.
/// </summary>
/// <remarks>
/// <para>Thread Safety: safe, designed for concurrent use.</para>
/// <para>
/// Usage:
/// Typically designed to implement an occupancy map.
/// You have a Memory Block that can store x elements and you want to reserve/free these elements in a concurrent fashion.
/// The occupancy map has one bit per element and when you want to allocate an element, you look for a free bit (with <see cref="FindNextUnsetL0"/>) and reserve
/// this bit with <see cref="SetL0"/>. You can also look for and set 64bits at a time (<see cref="FindNextUnsetL1"/> and <see cref="SetL1"/>).
/// This class is designed for multiple thread concurrently looking for any available bit (corresponding entry set to 0) to set.
/// A thread will call <see cref="FindNextUnsetL0"/> to get the position of a bit actually free, and will call <see cref="SetL0"/> to reserve it and set it
/// to one. If another thread beats us and set this bit to one before us, then <see cref="SetL0"/> will return <c>false</c> and we have to look for another bit.
/// </para>
/// <para>
/// Implementation:
/// As the BitMap may hold thousands of bits, looking for free ones, bit by bit, may take too much time as the occupancy rate increases. To alleviate this,
/// we have two levels of aggregation that allow us to skips complete 64bits blocks (level 1) and compete 64^2 blocks (level 2) during our search.
/// e.g. the first bit of the L1ALL map is set to 1 is all bit from [0-63] are set to 1.
/// Finding an unset bit is concurrent friendly, many threads can execute such operation at the same time, but setting or clearing a bit has to be an exclusive
/// operation (because of resize), but it's fast so if another thread has started such operation (<c>TakeControl()</c> is called), then we SpinWait until
/// it can be our turn.
/// </para>
/// </remarks>
public class ConcurrentBitmapL3All
{
    private const int L0All = 0;
    private const int L1All = 1;
    private const int L1Any = 2;
    private const int L2All = 3;

    private volatile int _control;
    private Memory<long>[] _maps;

    public int Capacity { get; private set; }
    public int TotalBitSet { get; private set; }
    public bool IsFull => Capacity == TotalBitSet;

    /// <summary>
    /// Construct an instance
    /// </summary>
    /// <param name="bitCount">Number of bits to host</param>
    public ConcurrentBitmapL3All(int bitCount)
    {
        // Note: could do one array allocation instead of four to store all the map.
        // We could also take the memory from a cheap Memory Manager.

        Capacity = bitCount;
        TotalBitSet = 0;

        _maps = new Memory<long>[4];
        var length = Math.Max(1, (bitCount + 63) / 64);
        _maps[L0All] = new long[length];

        length = Math.Max(1, (length + 63) / 64);
        _maps[L1All] = new long[length];
        _maps[L1Any] = new long[length];

        length = Math.Max(1, (length + 63) / 64);
        _maps[L2All] = new long[length];
    }

    public void Resize(int newBitCount)
    {
        TakeControl();

        var shrink = newBitCount < Capacity;
        Capacity = newBitCount;

        var maps = new Memory<long>[4];
        var length = Math.Max(1, (newBitCount + 63) / 64);
        var copySize = Math.Min(length, _maps[L0All].Length);

        maps[L0All] = new long[length];
        _maps[L0All].Span.Slice(0, copySize).CopyTo(maps[L0All].Span);

        length = Math.Max(1, (length + 63) / 64);
        maps[L1All] = new long[length];
        maps[L1Any] = new long[length];

        copySize = Math.Max(1, (copySize + 63) / 64);
        _maps[L1All].Span.Slice(0, copySize).CopyTo(maps[L1All].Span);
        _maps[L1Any].Span.Slice(0, copySize).CopyTo(maps[L1Any].Span);

        length = Math.Max(1, (length + 63) / 64);
        maps[L2All] = new long[length];

        copySize = Math.Max(1, (copySize + 63) / 64);
        _maps[L2All].Span.Slice(0, copySize).CopyTo(maps[L2All].Span);

        _maps = maps;

        if (shrink)
        {
            var span = maps[L0All].Span.Cast<long, ulong>();
            var spanLength = span.Length;
            var newCount = 0;

            for (int i = 0; i < spanLength; i++)
            {
                newCount += BitOperations.PopCount(span[i]);
            }

            TotalBitSet = newCount;
        }

        _control = 0;
    }


    /// <summary>
    /// Private method called to take control of the instance to ensure thread-safeness 
    /// </summary>
    private void TakeControl()
    {
        if (Interlocked.CompareExchange(ref _control, 1, 0) != 0)
        {
            var sw = new SpinWait();
            while (Interlocked.CompareExchange(ref _control, 1, 0) != 0)
            {
                // Note: SpinWait may yield too much for us, wasting latency because of unnecessary context switch provoked by Thread.Yield() or Thread.Sleep().
                //  maybe it would be better to avoid yielding on non single processor platform, like this code commented below

                //if (Environment.ProcessorCount == 1)
                //{
                //    sw.SpinOnce(-1);
                //}
                //else
                //{
                //    if (sw.NextSpinWillYield)   sw.Reset();     // Reset to avoid Yield
                //    sw.SpinOnce(-1);
                //}

                sw.SpinOnce();
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool SetL0(int bitIndex)
    {
        var l0Offset = bitIndex >> 6;
        var l0Mask = 1L << (bitIndex & 0x3F);

        TakeControl();

        var prevL0 = Interlocked.Or(ref _maps[L0All].Span[l0Offset], l0Mask);
        if ((prevL0 & l0Mask) != 0)
        {
            _control = 0;
            // The bit was concurrently set by someone else
            return false;
        }

        if (prevL0 != -1 && (prevL0 | l0Mask) == -1)
        {
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);

            var prevL1 = _maps[L1All].Span[l1Offset];
            _maps[L1All].Span[l1Offset] |= l1Mask;

            if (prevL1 != -1 && (prevL1 | l1Mask) == -1)
            {
                var l2Offset = l1Offset >> 6;
                var l2Mask = 1L << (l1Offset & 0x3F);
                _maps[L2All].Span[l2Offset] |= l2Mask;
            }
        }

        if (prevL0 == 0 && (prevL0 | l0Mask) != 0)
        {
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);
            _maps[L1Any].Span[l1Offset] |= l1Mask;
        }

        ++TotalBitSet;
        _control = 0;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool SetL1(int index)
    {
        var l0Offset = index;
        var l0Mask = -1L;

        TakeControl();
        var prevL0 = Interlocked.Or(ref _maps[L0All].Span[l0Offset], l0Mask);
        if (prevL0 != 0)
        {
            _control = 0;
            // Can't allocate the whole L1, some bits are set at L0
            return false;
        }

        if (prevL0 != -1 && (prevL0 | l0Mask) == -1)
        {
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);

            var prevL1 = _maps[L1All].Span[l1Offset];
            _maps[L1All].Span[l1Offset] |= l1Mask;

            if (prevL1 != -1 && (prevL1 | l1Mask) == -1)
            {
                var l2Offset = l1Offset >> 6;
                var l2Mask = 1L << (l1Offset & 0x3F);
                _maps[L2All].Span[l2Offset] |= l2Mask;
            }
        }

        if (prevL0 == 0 && (prevL0 | l0Mask) != 0)
        {
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);

            _maps[L1Any].Span[l1Offset] |= l1Mask;
        }

        TotalBitSet += 64;
        _control = 0;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void ClearL0(int index)
    {
        var l0Offset = index >> 6;
        var l0Mask = ~(1L << (index & 0x3F));

        TakeControl();
        var prevL0 = Interlocked.And(ref _maps[L0All].Span[l0Offset], l0Mask);
        if ((prevL0 == -1) && ((prevL0 & l0Mask) != -1))
        {
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);

            var prevL1 = _maps[L1All].Span[l1Offset];
            _maps[L1All].Span[l1Offset] &= l1Mask;

            if (prevL1 == -1)
            {
                var l2Offset = l1Offset >> 6;
                var l2Mask = 1L << (l1Offset & 0x3F);
                _maps[L2All].Span[l2Offset] &= l2Mask;
            }
        }

        if ((prevL0 != 0) && ((prevL0 & l0Mask) == 0))
        {
            var l1Offset = l0Offset >> 6;
            var l1Mask = 1L << (l0Offset & 0x3F);

            _maps[L1Any].Span[l1Offset] &= l1Mask;
        }

        --TotalBitSet;
        _control = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool IsSet(int index)
    {
        var offset = index >> 6;
        var mask = 1L << (index & 0x3F);

        return (_maps[L0All].Span[offset] & mask) != 0L;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool FindNextUnsetL0(ref int index, ref long mask)
    {
        var capacity = Capacity;
        var maps = _maps;

        var c0 = ++index;
        long v0 = mask;
        long t0;

        var ll0 = (capacity + 63) / 64;
        var ll1 = maps[L1All].Length;
        var ll2 = maps[L2All].Length;

        while (c0 < capacity)
        {
            // Do we have to fetch a new L0?
            if (((c0 & 0x3F) == 0) || (v0 == -1))
            {
                // Check if we can skip the rest of the level 0
                for (int i0 = c0 >> 6; i0 < ll0; i0 = c0 >> 6)
                {
                    t0 = 1L << (c0 & 0x3F);
                    v0 = maps[L0All].Span[i0] | (t0 - 1);

                    if (v0 != -1)
                    {
                        break;
                    }
                    c0 = ++i0 << 6;

                    // Check if we can skip the rest of the level 1
                    for (int i1 = c0 >> 12; i1 < ll1; i1 = c0 >> 12)
                    {
                        var v1 = maps[L1All].Span[i1] >> (i0 & 0x3F);
                        if (v1 != -1)
                        {
                            break;
                        }

                        i0 = 0;
                        c0 = ++i1 << 12;

                        // Check if we can skip the rest of the level 2
                        for (int i2 = c0 >> 18; i2 < ll2; i2 = c0 >> 18)
                        {
                            var v2 = maps[L2All].Span[i2] >> (i1 & 0x3F);
                            if (v2 != -1)
                            {
                                break;
                            }
                            i1 = 0;
                            c0 = ++i2 << 18;
                        }
                    }
                }
            }

            var bitPos = BitOperations.TrailingZeroCount(~v0);
            v0 |= (1L << bitPos);
            index = (c0 & ~0x3F) + bitPos;
            mask = v0;
            return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public bool FindNextUnsetL1(ref int index, ref long mask)
    {
        var maps = _maps;
        var c1 = ++index;
        long v1 = mask;
        var ll1 = maps[L1All].Length;
        var ll2 = maps[L2All].Length;

        while (c1 < (ll1 << 6))
        {
            if (((c1 & 0x3F) == 0) || (v1 == -1))
            {
                // Check if we can skip the rest of the level 1
                for (int i1 = c1 >> 6; i1 < ll1; i1 = c1 >> 6)
                {
                    var t1 = 1L << (c1 & 0x3F);
                    v1 = maps[L1All].Span[i1] | (t1 - 1);
                    if (v1 != -1)
                    {
                        break;
                    }

                    c1 = ++i1 << 6;

                    // Check if we can skip the rest of the level 2
                    for (int i2 = c1 >> 12; i2 < ll2; i2 = c1 >> 12)
                    {
                        var v2 = maps[L2All].Span[i2] >> (i1 & 0x3F);
                        if (v2 != -1)
                        {
                            break;
                        }

                        i1 = 0;
                        c1 = ++i2 << 12;
                    }
                }
            }

            var t = 1L << (c1 & 0x3F);
            v1 = maps[L1Any].Span[c1 >> 6] | (t - 1);
            var bitPos = BitOperations.TrailingZeroCount(~v1);
            v1 |= (1L << bitPos);
            index = (c1 & ~0x3F) + bitPos;
            mask = v1;
            return true;
        }

        return false;
    }
}