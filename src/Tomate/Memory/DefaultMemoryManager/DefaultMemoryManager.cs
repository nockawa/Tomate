using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tomate;

/// <summary>
/// General purpose Memory Manager
/// </summary>
/// <remarks>
/// <para>
/// Purpose
/// Thread-safe, general purpose allocation of memory block of any size (up to almost 2Gb).
/// This implementation was not meant to be very fast and memory efficient, I've just tried to find a good balance between speed, memory overhead
/// and complexity.
/// I do think though it's quite fast, should behave correctly regarding contention and is definitely more than acceptable regarding fragmentation.
/// </para>
/// <para>
/// Design and implementation
/// Being thread-safe impose a lot of special care to behave correctly regarding perf and contention. 
///
/// The MemoryManager has several sub-types :
///  - <seealso cref="NativeBlockInfo"/> which is responsible of making OS level allocation, a given Native Block will then serve memory allocation
///    requests for many <seealso cref="SmallBlock"/> and or <seealso cref="LargeBlock"/> instances.
///  - <seealso cref="BlockSequence"/>, there are many instances of this type, the number is determined during the manager construction and depending
///    on the CPU core number. Then each Thread is assigned a given instance (we assign them in round-robin fashion). The Block Sequence contains two chained
///    lists, one for <seealso cref="SmallBlock"/> and the other for <seealso cref="LargeBlock"/>.
///  - <seealso cref="SmallBlock"/> is responsible of making small ( less than 32 KiB) <seealso cref="MemorySegment"/> allocations. It uses a slab of a
///    given <seealso cref="NativeBlockInfo"/> instance.
///    Instances of <seealso cref="SmallBlock"/> are operating concurrently: two different threads using different instance won't compete for Allocation/Free.
///  - <seealso cref="LargeBlock"/> take care of Allocations greater than 32 KiB and will hold a Native Memory block of at least 64 MiB or sized with the
///    next power of 2 of the allocation that triggered it.
///
///  Allocated <seealso cref="MemorySegment"/> are guaranteed to be 16 bytes address-aligned. <seealso cref="MemorySegment"/> that was allocated by
///  <seealso cref="SmallBlock"/> have a 8 bytes header that precedes each instance, for <seealso cref="LargeBlock"/> the header is 14 bytes. These are the
///  only overhead that is dependent of each Segment allocation.
/// 
///  <seealso cref="SmallBlock"/> and <seealso cref="LargeBlock"/> have defragmentation routines that are called when free segment fragmentation is too high
///  to merge adjacent free segments.
///
///  <seealso cref="BlockSequence"/> will release empty Blocks (Small and Large), they will be added to a dedicated pool on the Memory Manager and reassigned
///   when possible to other Block Sequence that are requested memory. Which means the allocated Native Memory is never released, but reused. If you want
///   to release is, the only way is to call <seealso cref="DefaultMemoryManager.Clear"/>.
/// </para>
/// </remarks>
public partial class DefaultMemoryManager : IDisposable, IMemoryManager
{
    public static int BlockInitialCount { get; }
    public static int BlockGrowCount = 64;
    public static readonly int SmallBlockSize = 1024 * 1024;
    public static readonly int LargeBlockMinSize = 4 * 1024 * 1024;
    public static readonly int LargeBlockMaxSize = 256 * 1024 * 1024;
    public static readonly int MemorySegmentMaxSizeForSmallBlock = SmallBlock.SegmentHeader.MaxSegmentSize;
    public static readonly int MinSegmentSize = 16;

    /// <summary>
    /// Access to the global instance of the Memory Manager
    /// </summary>
    /// <remarks>
    /// Most of the time you will want to rely on this instance. Note that you can't dispose it nor <see cref="Clear"/> it as it is a shared instance.
    /// The memory will be freed when the process will exit.
    /// If you need control over the lifetime, create and use your own instance.
    /// </remarks>
    public static readonly DefaultMemoryManager GlobalInstance = new(true);

    static DefaultMemoryManager()
    {
        BlockInitialCount = Environment.ProcessorCount * 4;
    }

    private readonly List<NativeBlockInfo> _nativeBlockList;
    private ExclusiveAccessControl _nativeBlockListAccess;
    private NativeBlockInfo _curNativeBlockInfo;

    private readonly BlockSequence[] _blockSequences;
    private int _blockSequenceCounter;

    private readonly List<SmallBlock> _smallBlockList;
    private readonly Stack<SmallBlock> _pooledSmallBlockList;
    private ExclusiveAccessControl _smallBlockListAccess;

    private readonly List<LargeBlock> _largeBlockList;
    private readonly List<LargeBlock> _pooledLargeBlockList;
    private ExclusiveAccessControl _largeBlockListAccess;
    private int _largeBlockMinSize;

    private readonly bool _isGlobal;

    // Note: seems like ThreadLocal doesn't release data allocated for a given thread when this one is destroyed.
    // Which means if the program create/destroy thousand of threads, this won't be good for us...
    private ThreadLocal<BlockSequence> _assignedBlockSequence;

    public DefaultMemoryManager() : this(false)
    {
    }

    private DefaultMemoryManager(bool isGlobal)
    {
        _isGlobal = isGlobal;
        _nativeBlockList = new List<NativeBlockInfo>(16);
        _curNativeBlockInfo = new NativeBlockInfo(SmallBlockSize, BlockInitialCount);
        _nativeBlockList.Add(_curNativeBlockInfo);

        _blockSequences = new BlockSequence[BlockInitialCount];
        _smallBlockList = new List<SmallBlock>(BlockInitialCount + BlockGrowCount);
        _pooledSmallBlockList = new Stack<SmallBlock>(32);
        _largeBlockList = new List<LargeBlock>(BlockInitialCount + BlockGrowCount);
        _pooledLargeBlockList = new List<LargeBlock>(32);
        _largeBlockMinSize = LargeBlockMinSize;

        for (var i = 0; i < _blockSequences.Length; i++)
        {
            _blockSequences[i] = new BlockSequence(this);
        }

        _blockSequenceCounter = -1;
        _assignedBlockSequence = new ThreadLocal<BlockSequence>(() =>
        {
            var v = Interlocked.Increment(ref _blockSequenceCounter);
            return _blockSequences[v % _blockSequences.Length];
        });
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        if (_isGlobal)
        {
            throw new InvalidOperationException("Can't dispose the Global instance of the memory manager. Use your own if you need control on its lifetime.");
        }

        foreach (var nbi in _nativeBlockList)
        {
            nbi.Dispose();
        }

        _nativeBlockList.Clear();
        _smallBlockList.Clear();
        _assignedBlockSequence.Dispose();
    }

    public bool IsDisposed => _nativeBlockList.Count == 0;
    public int MaxAllocationLength => LargeBlock.SegmentHeader.MaxSegmentSize;

    public void Clear()
    {
        if (_isGlobal)
        {
            throw new InvalidOperationException("Can't Clear the Global instance of the memory manager. Use your own if you need control on its resource.");
        }

        foreach (var nbi in _nativeBlockList)
        {
            nbi.Dispose();
        }
        _nativeBlockList.Clear();
        _curNativeBlockInfo = new NativeBlockInfo(SmallBlockSize, BlockInitialCount);
        _nativeBlockList.Add(_curNativeBlockInfo);

        _smallBlockList.Clear();
        _pooledSmallBlockList.Clear();
        _largeBlockList.Clear();
        _pooledLargeBlockList.Clear();
        _largeBlockMinSize = LargeBlockMinSize;

        for (var i = 0; i < _blockSequences.Length; i++)
        {
            _blockSequences[i] = new BlockSequence(this);
        }

        _blockSequenceCounter = -1;
        _assignedBlockSequence = new ThreadLocal<BlockSequence>(() =>
        {
            var v = Interlocked.Increment(ref _blockSequenceCounter);
            return _blockSequences[v % _blockSequences.Length];
        });
    }

    public MemorySegment Allocate(int size)
    {
        var blockSequence = _assignedBlockSequence.Value;
        return blockSequence!.Allocate(size);
    }

    public unsafe MemorySegment<T> Allocate<T>(int size) where T : unmanaged
    {
        return Allocate(size * sizeof(T)).Cast<T>();
    }

    public unsafe bool Free(MemorySegment segment)
    {
        // First check which type of block owns the segment we're freeing
        ref var sbh = ref Unsafe.AsRef<SmallBlock.SegmentHeader>(segment.Address - sizeof(SmallBlock.SegmentHeader));

        if (sbh.IsOwnedBySmallBlock)
        {
            var smallBlock = _smallBlockList[sbh.SmallBlockId];
            return smallBlock.Free(ref sbh);
        }

        ref var lbh = ref Unsafe.AsRef<LargeBlock.SegmentHeader>(segment.Address - sizeof(LargeBlock.SegmentHeader));
        var largeBlock = _largeBlockList[lbh.LargeBlockId];
        return largeBlock.Free(ref lbh);
    }

    public bool Free<T>(MemorySegment<T> segment) where T : unmanaged
    {
        return Free((MemorySegment)segment);
    }

    internal void RecycleBlock(SmallBlock block)
    {
        try
        {
            _smallBlockListAccess.TakeControl(null);
            _pooledSmallBlockList.Push(block);
        }
        finally
        {
            _smallBlockListAccess.ReleaseControl();
        }
    }

    internal void RecycleBlock(LargeBlock block)
    {
        try
        {
            _largeBlockListAccess.TakeControl(null);
            _pooledLargeBlockList.Add(block);
        }
        finally
        {
            _largeBlockListAccess.ReleaseControl();
        }
    }

    private MemorySegment AllocateNativeBlockDataSegment()
    {
        try
        {
            _nativeBlockListAccess.TakeControl(null);

            if (_curNativeBlockInfo.GetBlockSegment(out var res))
            {
                return res;
            }

            {
                var nbi = new NativeBlockInfo(SmallBlockSize, BlockInitialCount + BlockGrowCount);
                _nativeBlockList.Add(nbi);
                _curNativeBlockInfo = nbi;

                nbi.GetBlockSegment(out res);
                return res;
            }
        }
        finally
        {
            _nativeBlockListAccess.ReleaseControl();
        }
    }

    private MemorySegment AllocateNativeBlock(int minimumSize)
    {
        try
        {
            _nativeBlockListAccess.TakeControl(null);

            // Only allocate power of 2 size
            var size = minimumSize.NextPowerOf2();

            // If the current Large Block size is bigger than the requested min size, use it
            if (_largeBlockMinSize > size)
            {
                size = _largeBlockMinSize;

                // mul by 2 each time the current large block min size until we reach the max size
                if (_largeBlockMinSize < LargeBlockMaxSize)
                {
                    _largeBlockMinSize = _largeBlockMinSize.NextPowerOf2();
                }
            }

            // Cap the size (+63 is because of the padding 64 we do on the block's address)
            if ((size + 63) > Array.MaxLength)
            {
                size = Array.MaxLength - 63;
            }

            if (minimumSize > size)
            {
                throw new OutOfMemoryException($"Requested Size {minimumSize} is too big for the max allowed size of {Array.MaxLength}");
            }

            var nbi = new NativeBlockInfo(size, 1);

            _nativeBlockList.Add(nbi);

            nbi.GetBlockSegment(out var res);
            return res;
        }
        finally
        {
            _nativeBlockListAccess.ReleaseControl();
        }
    }

    private SmallBlock AllocateSmallBlock(BlockSequence owner)
    {
        try
        {
            _smallBlockListAccess.TakeControl(null);
            var smallBlockIndex = _smallBlockList.Count;
            SmallBlock smallBlock;
            if (_pooledSmallBlockList.Count > 0)
            {
                smallBlock = _pooledSmallBlockList.Pop();
                smallBlock.Reassign(owner);
            }
            else
            {
                smallBlock = new SmallBlock(owner, (ushort)smallBlockIndex);
            }
            _smallBlockList.Add(smallBlock);
            return smallBlock;
        }
        finally
        {
            _smallBlockListAccess.ReleaseControl();
        }
    }
    private LargeBlock AllocateLargeBlock(BlockSequence owner, int minimumSize)
    {
        try
        {
            _largeBlockListAccess.TakeControl(null);
            LargeBlock largeBlock = null;
            for (var i = 0; i < _pooledLargeBlockList.Count; i++)
            {
                if (_pooledLargeBlockList[i].Data.Length >= minimumSize)
                {
                    largeBlock = _pooledLargeBlockList[i];
                    largeBlock.Reassign(owner);
                    _pooledLargeBlockList.RemoveAt(i);
                    break;
                }
            }

            // If we didn't find a suitable bloc, allocate a new one
            if (largeBlock == null)
            {
                largeBlock = new LargeBlock(owner, (ushort)_largeBlockList.Count, minimumSize);
                _largeBlockList.Add(largeBlock);
            }
            return largeBlock;
        }
        finally
        {
            _largeBlockListAccess.ReleaseControl();
        }
    }

    #region Debug Features

    internal BlockSequence GetThreadBlockSequence() => _assignedBlockSequence.Value;

    internal unsafe ref SmallBlock.SegmentHeader GetSegmentHeader(void* segmentAddress)
    {
        return ref Unsafe.AsRef<SmallBlock.SegmentHeader>((byte*)segmentAddress - sizeof(SmallBlock.SegmentHeader));
    }

    internal int NativeBlockCount => _nativeBlockList.Count;

    internal long NativeBlockTotalSize
    {
        get
        {
            try
            {
                _nativeBlockListAccess.TakeControl(null);
                var total = 0L;
                foreach (var nbi in _nativeBlockList)
                {
                    total += nbi.DataSegment.Length;
                }

                return total;
            }
            finally
            {
                _nativeBlockListAccess.ReleaseControl();
            }
        }
    }

    #endregion
}