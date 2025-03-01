﻿using System.Diagnostics;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

// ReSharper disable RedundantUsingDirective
using System.Runtime.Intrinsics;
using System.Text;
using Serilog;
// ReSharper restore RedundantUsingDirective

namespace Tomate;

[PublicAPI]
public class BlockOverrunException : Exception
{
    #region Public APIs

    #region Properties

    public MemoryBlock Block { get; }

    #endregion

    #endregion

    #region Constructors

    public BlockOverrunException(MemoryBlock block, string msg) : base(msg)
    {
        Block = block;
    }

    #endregion
}

/// <summary>
/// General purpose Memory Manager
/// </summary>
/// <remarks>
/// <para>
/// Purpose
/// 
/// Thread-safe, general purpose allocation of memory block of any size (up to almost 2Gb).
/// This implementation was not meant to be very fast and memory efficient, I've just tried to find a good balance between speed, memory overhead
/// and complexity.
/// 
/// I do think though it's quite fast, should behave correctly regarding contention and is definitely more than acceptable regarding fragmentation.
/// </para>
/// <para>
/// Design and implementation
/// 
/// Being thread-safe impose a lot of special care to behave correctly regarding perf and contention. 
///
/// The MemoryManager has several sub-types :
///  - <see cref="NativeBlockInfo"/> which is responsible of making OS level allocation, a given Native Block will then serve memory allocation
///    requests for many <see cref="SmallBlockAllocator"/> and or <see cref="LargeBlockAllocator"/> instances.
///  - <see cref="BlockAllocatorSequence"/>, there are many instances of this type, the number is determined during the manager construction and depending
///    on the CPU core number. Then each Thread is assigned a given instance (we assign them in round-robin fashion). The Block Sequence contains two chained
///    lists, one for <see cref="SmallBlockAllocator"/> and the other for <see cref="LargeBlockAllocator"/>.
///  - <see cref="SmallBlockAllocator"/> is responsible of making small ( >= 65526 bytes) <see cref="MemorySegment"/> allocations. It uses a slab of a
///    given <see cref="NativeBlockInfo"/> instance.
///    Instances of <see cref="SmallBlockAllocator"/> are operating concurrently: two different threads using different instance won't compete for Allocation/Free.
///  - <see cref="LargeBlockAllocator"/> take care of Allocations greater than 65526 bytes and will hold a Native Memory block of at least 64 MiB or sized with the
///    next power of 2 of the allocation that triggered it.
///
///  Allocated <see cref="MemorySegment"/> are guaranteed to be 16 bytes address-aligned. <see cref="MemorySegment"/> that was allocated by
///  <see cref="SmallBlockAllocator"/> have a 14 bytes header that precedes each instance, for <see cref="LargeBlockAllocator"/> the header is 20 bytes. These are the
///  only overhead that is dependent of each Segment allocation.
/// 
///  <see cref="SmallBlockAllocator"/> and <see cref="LargeBlockAllocator"/> have defragmentation routines that are called when free segment fragmentation is too high
///  to merge adjacent free segments.
///
///  <see cref="BlockAllocatorSequence"/> will release empty Blocks (Small and Large), they will be added to a dedicated pool on the Memory Manager and reassigned
///   when possible to other Block Sequence that are requested memory. Which means the allocated Native Memory is never released, but reused. If you want
///   to release is, the only way is to call <see cref="DefaultMemoryManager.Clear"/>.
/// </para>
/// </remarks>
[PublicAPI]
public partial class DefaultMemoryManager : IDisposable, IMemoryManager, IPageAllocator
{
#if DEBUGALLOC
    internal static readonly int BlockMarginSize = 1024;                    // MUST BE A MULTIPLE OF 32 BYTES !!!
    private static readonly ulong MarginFillPattern = 0xfdfdfdfdfdfdfdfd;
    private readonly string _sourceFile;
    private readonly int _lineNb;
    private readonly bool _blockOverrunDetection;
    private readonly Vector256<byte> _blockCheckPattern;
#else
    internal static readonly int BlockMarginSize;                            // MUST BE A MULTIPLE OF 32 BYTES !!!
#endif

    internal static readonly int BlockInitialCount = Environment.ProcessorCount * 4;
    internal static readonly int BlockGrowCount = 64;
    internal static readonly int SmallBlockSize = 1024 * 1024;
    internal static readonly int LargeBlockMinSize = 4 * 1024 * 1024;
    internal static readonly int LargeBlockMaxSize = 256 * 1024 * 1024;
    public static readonly int MinSegmentSize = 16;
#if DEBUGALLOC    
    internal static readonly int MemorySegmentMaxSizeForSmallBlock = SmallBlockAllocator.SegmentHeader.MaxSegmentSize - (BlockMarginSize * 2);
    public static readonly unsafe int MaxMemorySegmentSize = Array.MaxLength - 63 - sizeof(LargeBlockAllocator.SegmentHeader).Pad16() - (BlockMarginSize * 2);
#else
    internal static readonly int MemorySegmentMaxSizeForSmallBlock = SmallBlockAllocator.SegmentHeader.MaxSegmentSize;
    public static readonly unsafe int MaxMemorySegmentSize = Array.MaxLength - 63 - sizeof(LargeBlockAllocator.SegmentHeader).Pad16();
#endif
    public static readonly int PageAllocatorPageSize = 1024 * 1024;
    public static readonly int PageAllocatorMaxPageCount = 1024;
    public static readonly int StoreMaxEntryCount = 1024 * 1024;

    /// <summary>
    /// Access to the global instance of the Memory Manager
    /// </summary>
    /// <remarks>
    /// Most of the time you will want to rely on this instance. Note that you can't dispose it nor <see cref="Clear"/> it as it is a shared instance.
    /// The memory will be freed when the process will exit.
    /// If you need control over the lifetime, create and use your own instance.
    /// </remarks>
    public static readonly DefaultMemoryManager GlobalInstance = new(PageAllocatorPageSize, PageAllocatorMaxPageCount, false, true);

    private readonly List<NativeBlockInfo> _nativeBlockList;
    private ExclusiveAccessControl _nativeBlockListAccess;
    private NativeBlockInfo _curNativeBlockInfo;

    private readonly BlockAllocatorSequence[] _blockAllocatorSequences;
    private int _blockSequenceCounter;

    private readonly Stack<SmallBlockAllocator> _pooledSmallBlockList;
    private ExclusiveAccessControl _smallBlockListAccess;

    private readonly List<LargeBlockAllocator> _pooledLargeBlockList;
    private ExclusiveAccessControl _largeBlockAccess;
    private int _largeBlockMinSize;

    private readonly bool _isGlobal;
    private readonly BlockReferential.GenBlockHeader _zeroHeader;
    private readonly MemoryBlock _zeroMemoryBlock;

    private int _maxPageCount;
    private PageAllocator _pageAllocator;
    private UnmanagedDataStore _dataStore;
    private ExclusiveAccessControl _dataStoreAccess;

#if DEBUGALLOC
    [DebuggerDisplay("Size: {Size}, Source File: {SourceFile}, Line # {LineNb}")]
#else
    [DebuggerDisplay("Size: {Size}")]
#endif
    internal readonly struct MemoryBlockInfo
    {
#if DEBUGALLOC
        public MemoryBlockInfo(int s, string sourceFileName, int sourceLinNb)
        {
            Size = s;
            SourceFile = sourceFileName;
            LineNb = sourceLinNb;
        }
#else
        public MemoryBlockInfo(int s)
        {
            Size = s;
        }
#endif
        public readonly int Size;
#if DEBUGALLOC
        public readonly string SourceFile;
        public readonly int LineNb;
#endif
    }

    /// <summary>
    /// Debugging feature to initialize the MemoryBlock's content upon allocation (available in DEBUGALlOC only)
    /// </summary>
    public enum DebugMemoryInit
    {
        /// Default behavior: the memory is not touched upon allocation
        None,
        /// Memory is cleared
        Zero,
        /// Memory is set to 0xde pattern
        Pattern
    }

    /// <summary>
    /// This property only works in DEBUGALLOC mode, it is primarily used to initialize the content of newly allocated block,
    ///  for debugging/troubleshooting purposes.
    /// </summary>
    public DebugMemoryInit MemoryBlockContentInitialization
    {
#if DEBUGALLOC
        get;
        set;
#else
        get => DebugMemoryInit.None;
        // ReSharper disable once ValueParameterNotUsed
        set {}
#endif
    }

    /// <summary>
    /// This property only works in DEBUGALLOC mode, it is primarily used to change the content of a memory block being freed,
    ///  for debugging/troubleshooting purposes.
    /// </summary>
    public DebugMemoryInit MemoryBlockContentCleanup
    {
#if DEBUGALLOC
        get;
        set;
#else
        get => DebugMemoryInit.None;
        // ReSharper disable once ValueParameterNotUsed
        set {}
#endif
    }
    
    // Note: seems like ThreadLocal doesn't release data allocated for a given thread when this one is destroyed.
    // Which means if the program create/destroy thousand of threads, this won't be good for us...
    private ThreadLocal<BlockAllocatorSequence> _assignedBlockSequence;

#if DEBUGALLOC
    public DefaultMemoryManager(int pageSize=1024*1024, int maxPageCount=1024, bool enableBlockOverrunDetection = true) : 
        this(pageSize, maxPageCount, enableBlockOverrunDetection, false)
#else
    public DefaultMemoryManager(int pageSize=1024*1024, int maxPageCount=1024) : this(pageSize, maxPageCount, false, false)
#endif
    {
    }

    // ReSharper disable once UnusedParameter.Local
    private unsafe DefaultMemoryManager(int pageSize, int maxPageCount, bool enableBlockOverrunDetection, bool isGlobal
#if DEBUGALLOC
        , [CallerFilePath] string sourceFile = "", [CallerLineNumber] int lineNb = 0
#endif
    )
    {
#if DEBUGALLOC
        _sourceFile = sourceFile;
        _lineNb = lineNb;
        _blockOverrunDetection = enableBlockOverrunDetection;
        _blockCheckPattern = Vector256.Create((byte)MarginFillPattern);

        MemoryBlockContentInitialization = DebugMemoryInit.None;
#endif
        MemoryManagerId = IMemoryManager.RegisterMemoryManager(this);
        _isGlobal = isGlobal;
        _nativeBlockList = new List<NativeBlockInfo>(16);
        _curNativeBlockInfo = new NativeBlockInfo(SmallBlockSize, BlockInitialCount);
        _nativeBlockList.Add(_curNativeBlockInfo);

        _blockAllocatorSequences = new BlockAllocatorSequence[BlockInitialCount];
        _pooledSmallBlockList = new Stack<SmallBlockAllocator>(32);
        _pooledLargeBlockList = new List<LargeBlockAllocator>(32);
        _largeBlockMinSize = LargeBlockMinSize;

        for (var i = 0; i < _blockAllocatorSequences.Length; i++)
        {
            _blockAllocatorSequences[i] = new BlockAllocatorSequence(this);
        }

        _blockSequenceCounter = -1;
        _assignedBlockSequence = new ThreadLocal<BlockAllocatorSequence>(() =>
        {
            var v = Interlocked.Increment(ref _blockSequenceCounter);
            return _blockAllocatorSequences[v % _blockAllocatorSequences.Length];
        });

        {
            _zeroHeader.BlockIndex = _blockAllocatorSequences[0].FirstBlockId;
            _zeroHeader.RefCounter = 1;
            _zeroMemoryBlock = new MemoryBlock((byte*)Unsafe.AsPointer(ref _zeroHeader) + sizeof(BlockReferential.GenBlockHeader), 0, -1);
        }
        
        // Page Allocator part
        PageSize = pageSize;
        _maxPageCount = maxPageCount;
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

#if DEBUGALLOC
        DumpLeaks();
#endif
        foreach (var nbi in _nativeBlockList)
        {
            nbi.Dispose();
        }

        foreach (var bs in _blockAllocatorSequences)
        {
            bs.Dispose();
        }

        foreach (var ba in _pooledSmallBlockList)
        {
            BlockReferential.UnregisterAllocator(ba.BlockIndex);
        }

        foreach (var ba in _pooledLargeBlockList)
        {
            BlockReferential.UnregisterAllocator(ba.BlockIndex);
        }
        
        if (_dataStore.IsDefault == false)
        {
            _dataStore.Dispose();
        }

        _nativeBlockList.Clear();
        _assignedBlockSequence.Dispose();
    }

    public bool IsDisposed => _nativeBlockList.Count == 0;
    public int MaxAllocationLength => LargeBlockAllocator.SegmentHeader.MaxSegmentSize;
    public int MemoryManagerId { get; }

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
        foreach (var bs in _blockAllocatorSequences)
        {
            bs.Dispose();
        }

        foreach (var ba in _pooledSmallBlockList)
        {
            BlockReferential.UnregisterAllocator(ba.BlockIndex);
        }

        foreach (var ba in _pooledLargeBlockList)
        {
            BlockReferential.UnregisterAllocator(ba.BlockIndex);
        }

        _nativeBlockList.Clear();
        _curNativeBlockInfo = new NativeBlockInfo(SmallBlockSize, BlockInitialCount);
        _nativeBlockList.Add(_curNativeBlockInfo);

        _pooledSmallBlockList.Clear();
        _pooledLargeBlockList.Clear();
        _largeBlockMinSize = LargeBlockMinSize;

        for (var i = 0; i < _blockAllocatorSequences.Length; i++)
        {
            _blockAllocatorSequences[i] = new BlockAllocatorSequence(this);
        }

        _blockSequenceCounter = -1;
        _assignedBlockSequence = new ThreadLocal<BlockAllocatorSequence>(() =>
        {
            var v = Interlocked.Increment(ref _blockSequenceCounter);
            return _blockAllocatorSequences[v % _blockAllocatorSequences.Length];
        });
    }

#if DEBUGALLOC
    private void DumpLeaks()
    {
        var sb = new StringBuilder(4096);
        var totalLeaks = 0;

        foreach (var bsa in _blockAllocatorSequences)
        {
            bsa.DumpLeaks(sb, ref totalLeaks);
        }

        if (totalLeaks > 0)
        {
            Log.Verbose("Memory Manager allocated in {SourceFile}:line {LineNb} has {TotalLeaks} leaked segments\\r\\n{SB}", _sourceFile, _lineNb, totalLeaks, sb);
        }
    }
#endif


#if DEBUGALLOC
    public unsafe MemoryBlock Allocate(int length, [CallerFilePath] string sourceFile = "", [CallerLineNumber] int lineNb = 0)
#else
    public MemoryBlock Allocate(int length)
#endif
    {
        var blockSequence = _assignedBlockSequence.Value;

        // Special case: 0 length
        if (length == 0)
        {
            return _zeroMemoryBlock;
        }

        if ((uint)length > MaxAllocationLength)
        {
            ThrowHelper.InvalidAllocationSize($"The given size {length} is too big or not allowed (negative), the maximum allowed size is {MaxAllocationLength} bytes.");
        }
        
#if DEBUGALLOC
        if (_blockOverrunDetection)
        {
            length += BlockMarginSize * 2;
        }
        var sai = new MemoryBlockInfo(length, sourceFile, lineNb);
#else
        var sai = new MemoryBlockInfo(length);
#endif
        var res = blockSequence!.Allocate(ref sai);

#if DEBUGALLOC
        if (_blockOverrunDetection)
        {
            res.MemorySegment.Slice(0, BlockMarginSize).ToSpan<ulong>().Fill(MarginFillPattern);
            res.MemorySegment.Slice(-BlockMarginSize, BlockMarginSize).ToSpan<ulong>().Fill(MarginFillPattern);

            res = new MemoryBlock(res.MemorySegment.Address + BlockMarginSize, res.MemorySegment.Length - (BlockMarginSize * 2), -1);
        }

        switch (MemoryBlockContentInitialization)
        {
            case DebugMemoryInit.Zero:
                res.MemorySegment.ToSpan<byte>().Clear();
                break;
            case DebugMemoryInit.Pattern:
                if ((length & 0x7) == 0)
                {
                    res.MemorySegment.ToSpan<ulong>().Fill(0xdededededededede);
                }
                if ((length & 0x3) == 0)
                {
                    res.MemorySegment.ToSpan<uint>().Fill(0xdededede);
                }
                else
                {
                    res.MemorySegment.ToSpan<byte>().Fill(0xde);
                }
                break;
        }
#endif

        return res;
    }

#if DEBUGALLOC
    public unsafe MemoryBlock<T> Allocate<T>(int size, [CallerFilePath] string sourceFile = "", [CallerLineNumber] int lineNb = 0) where T : unmanaged
#else
    public unsafe MemoryBlock<T> Allocate<T>(int length) where T : unmanaged
#endif
    {
#if DEBUGALLOC
        // ReSharper disable ExplicitCallerInfoArgument
        return Allocate(size * sizeof(T), sourceFile, lineNb).Cast<T>();
        // ReSharper restore ExplicitCallerInfoArgument
#else
        return Allocate(length * sizeof(T)).Cast<T>();
#endif
    }
    
#if DEBUGALLOC
    private unsafe bool OverrunCheck(MemorySegment<byte> segment)
    {
        var cur = segment.Address;
        var end = cur + segment.Length;
        while (cur < end)
        {
            var remaining = (int)(end - cur);
            if (remaining >= 32)
            {
                var v = Vector256.Load(cur);
                if (v != _blockCheckPattern)
                {
                    return false;
                }
                cur += 32;
            }
        }

        return true;
    }
#endif

    // ReSharper disable once RedundantUnsafeContext
    public unsafe bool Free(MemoryBlock block)
    {
#if DEBUGALLOC
        if (_blockOverrunDetection)
        {
            var wholeSegment = new MemorySegment(block.MemorySegment.Address - BlockMarginSize, block.MemorySegment.Length + (2 * BlockMarginSize), -1);

            var debugSeg = wholeSegment.Cast<byte>();
            if (OverrunCheck(debugSeg.Slice(0, BlockMarginSize)) == false ||
                OverrunCheck(debugSeg.Slice(-BlockMarginSize, BlockMarginSize)) == false)
            {
                throw new BlockOverrunException(block, "Block was corrupted by write overrun");
            }
        }
#endif
        return BlockReferential.Free(block);
    }

    public bool Free<T>(MemoryBlock<T> block) where T : unmanaged
    {
        return BlockReferential.Free(block);
    }

    private void RecycleBlock(SmallBlockAllocator blockAllocator)
    {
        try
        {
            _smallBlockListAccess.TakeControl();
            _pooledSmallBlockList.Push(blockAllocator);
        }
        finally
        {
            _smallBlockListAccess.ReleaseControl();
        }
    }

    private void RecycleBlock(LargeBlockAllocator blockAllocator)
    {
        try
        {
            _largeBlockAccess.TakeControl();
            _pooledLargeBlockList.Add(blockAllocator);
        }
        finally
        {
            _largeBlockAccess.ReleaseControl();
        }
    }

    private MemorySegment AllocateNativeBlockDataSegment()
    {
        try
        {
            _nativeBlockListAccess.TakeControl();

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
            _nativeBlockListAccess.TakeControl();

            // Only allocate power of 2 size
            var size = (uint)minimumSize.NextPowerOf2();

            // If the current Large Block size is bigger than the requested min size, use it
            if (_largeBlockMinSize > size)
            {
                size = (uint)_largeBlockMinSize;

                // mul by 2 each time the current large block min size until we reach the max size
                if (_largeBlockMinSize < LargeBlockMaxSize)
                {
                    _largeBlockMinSize = _largeBlockMinSize.NextPowerOf2();
                }
            }

            // Cap the size (+63 is because of the padding 64 we do on the block's address)
            if ((size + 63) > Array.MaxLength)
            {
                size = (uint)(Array.MaxLength - 63);
            }

            if (minimumSize > size)
            {
                ThrowHelper.InvalidAllocationSize($"Requested Size {minimumSize} is too big for the max allowed size of {Array.MaxLength}");
            }

            var nbi = new NativeBlockInfo((int)size, 1);

            _nativeBlockList.Add(nbi);

            nbi.GetBlockSegment(out var res);
            return res;
        }
        finally
        {
            _nativeBlockListAccess.ReleaseControl();
        }
    }

    private SmallBlockAllocator AllocateSmallBlockAllocator(BlockAllocatorSequence owner)
    {
        try
        {
            _smallBlockListAccess.TakeControl();
            SmallBlockAllocator smallBlockAllocator;
            if (_pooledSmallBlockList.Count > 0)
            {
                smallBlockAllocator = _pooledSmallBlockList.Pop();
                smallBlockAllocator.Reassign(owner);
            }
            else
            {
                smallBlockAllocator = new SmallBlockAllocator(owner);
            }
            return smallBlockAllocator;
        }
        finally
        {
            _smallBlockListAccess.ReleaseControl();
        }
    }
    private LargeBlockAllocator AllocateLargeBlockAllocator(BlockAllocatorSequence owner, int minimumSize)
    {
        try
        {
            _largeBlockAccess.TakeControl();
            LargeBlockAllocator largeBlockAllocator = null;
            for (var i = 0; i < _pooledLargeBlockList.Count; i++)
            {
                if (_pooledLargeBlockList[i].Data.Length >= minimumSize)
                {
                    largeBlockAllocator = _pooledLargeBlockList[i];
                    largeBlockAllocator.Reassign(owner);
                    _pooledLargeBlockList.RemoveAt(i);
                    break;
                }
            }

            // If we didn't find a suitable bloc, allocate a new one
            return largeBlockAllocator ?? new LargeBlockAllocator(owner, minimumSize);
        }
        finally
        {
            _largeBlockAccess.ReleaseControl();
        }
    }

#region Debug Features

    internal BlockAllocatorSequence GetThreadBlockAllocatorSequence() => _assignedBlockSequence.Value;

    internal unsafe ref SmallBlockAllocator.SegmentHeader GetSegmentHeader(void* segmentAddress)
    {
#if DEBUGALLOC
        return ref Unsafe.AsRef<SmallBlockAllocator.SegmentHeader>((byte*)segmentAddress - BlockMarginSize - sizeof(SmallBlockAllocator.SegmentHeader));
#else
        return ref Unsafe.AsRef<SmallBlockAllocator.SegmentHeader>((byte*)segmentAddress - sizeof(SmallBlockAllocator.SegmentHeader));
#endif
    }

    internal int NativeBlockCount => _nativeBlockList.Count;

    internal long NativeBlockTotalSize
    {
        get
        {
            try
            {
                _nativeBlockListAccess.TakeControl();
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsurePageAllocatorInitialized()
    {
        if (_pageAllocator != null)
        {
            return;
        }
        
        var pa = new PageAllocator(PageSize, _maxPageCount);
        
        if (Interlocked.CompareExchange(ref _pageAllocator, pa, null) != null)
        {
            pa.Dispose();
        }
    }

    public unsafe byte* BaseAddress
    {
        get
        {
            EnsurePageAllocatorInitialized();
            return _pageAllocator.BaseAddress;
        }
    }

    public int PageAllocatorId
    {
        get
        {
            EnsurePageAllocatorInitialized();
            return _pageAllocator.PageAllocatorId;
        }
    }

    public int PageSize { get; }

    public MemorySegment AllocatePages(int length)
    {
        EnsurePageAllocatorInitialized();
        return _pageAllocator.AllocatePages(length);
    }

    public bool FreePages(MemorySegment pages)
    {
        EnsurePageAllocatorInitialized();
        return _pageAllocator.FreePages(pages);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public MemorySegment FromBlockId(int blockId)
    {
        EnsurePageAllocatorInitialized();
        return _pageAllocator.FromBlockId(blockId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ToBlockId(MemorySegment segment)
    {
        EnsurePageAllocatorInitialized();
        return _pageAllocator.ToBlockId(segment);
    }

    public ref UnmanagedDataStore Store
    {
        get
        {
            EnsureDataStoreInitialized();
            return ref _dataStore;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureDataStoreInitialized()
    {
        if (_dataStore.IsDefault == false)
        {
            return;
        }

        try
        {
            _dataStoreAccess.TakeControl();
            if (_dataStore.IsDefault == false)
            {
                return;
            }
            var rootPage = AllocatePages(1);
            var (size, levelsInfo) = UnmanagedDataStore.ComputeStorageSize(this, StoreMaxEntryCount);
            Debug.Assert(size <= rootPage.Length);
            _dataStore = UnmanagedDataStore.Create(this, rootPage.Slice(size), levelsInfo);
        }
        finally
        {
            _dataStoreAccess.ReleaseControl();
        }
    }
}