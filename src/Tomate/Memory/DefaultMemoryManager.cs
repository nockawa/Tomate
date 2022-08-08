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
/// Thread-safe, general purpose allocation of memory block of any size.
/// This implementation was not meant to be very fast and memory efficient, I've just tried to find a good balance between speed, memory overhead
/// and complexity.
/// I do think though it's quite fast, should behave correctly regarding contention and is definitely more than acceptable regarding fragmentation.
/// </para>
/// <para>
/// Design and implementation
/// Being thread-safe impose a lot of special care to behave correctly regarding perf and contention. General consensus for thread-safe memory
/// allocators is to rely on thread-local data, at the expense of internal complexity, but I won't do that.
///
/// The memory manager will allocate NativeBlock (each will be an OS mem alloc), each will be partly used by logical blocks that manage allocation.
/// There will be x BlockSequences of initialized, x depends on the number of CPU core. Each BlockSequence can work concurrently from others:
/// two different threads may allocate concurrently if using a different BlockSequence, but if two threads allocate/free on the same: there will be a stall.
/// A BlockSequence contains a chain of SmallBlocks. 
/// Rule of thumb decides there will be (4 x nb CPU Cores) of BlockSequence, when a thread allocates for the first time, it will be assigned one
/// (determined in round-robin fashion). This should make contention acceptable.
/// It's possible for a Thread A to make an alloc and later on a Thread B to free this particular Segment, such scenario could worsen contention, but
/// still, should be ok.
/// SmallBlocks are 1 MiB of size, they allocate Segment ranging from 0 to 64 KiB, allocated memory segment will be aligned on 16 bytes.
/// If the user allocates a segment greater than 32 KiB, we will defer the operation to a LargeBlock.
/// A LargeBlock is at least 64 MiB or sized with next power of 2 of the allocation that triggered it (if there is no existing MegaBlock that can
/// fulfill the request), it is not associated to a particular thread.
/// Any memory segments allocated from a MegaBlock is 64 bytes aligned.
///
/// Each allocation has a private header that precede the address of the block we return to the user. It contains all the required data for the memory
/// manager to function.
/// On segments allocated from SmallBlocks, the header is 12 bytes long. On segments allocated from LargeBlock, the header is 24 bytes long.
///
/// All segments inside a Block are linked with a two ways linked list that is ordered by their address. All free segments are also linked
/// with a dedicated linked list, following the same principals. When a segment is freed, we will be able to determine if its adjacent segments are
/// also free and we'll merge them into one block if it's possible, in order to create one bigger free segment and fight fragmentation.
/// </para>
/// </remarks>
public class DefaultMemoryManager : IDisposable, IMemoryManager
{
    public static int SmallBlockInitialCount { get; }
    public static int SmallBlockGrowCount = 64;
    public static readonly int SmallBlockSize = 1024 * 1024;
    //public static readonly int SmallBlockSize = 1024;
    public static readonly int LargeBlockBSize = 64 * 1024 * 1024;
    public static readonly int MemorySegmentMaxSizeForSuperBlock = 64 * 1024;
    public static readonly int MinSegmentSize = 16;

    #region Internal Types

    private unsafe class NativeBlockInfo : IDisposable
    {
        private readonly int _blockSize;
        private readonly int _blockCapacity;
        private byte* _alignedAddress;
        private byte[] _array;

        private int _curFreeIndex;

        public NativeBlockInfo(int blockSize, int capacity)
        {
            var nativeBlockSize = blockSize * capacity;
            _blockSize = blockSize;
            _blockCapacity = capacity;
            _array = GC.AllocateUninitializedArray<byte>(nativeBlockSize + 63, true);
            var baseAddress = (byte*)Marshal.UnsafeAddrOfPinnedArrayElement(_array, 0).ToPointer();

            var offsetToAlign64 = (int)((((long)baseAddress + 63L) & -64L) - (long)baseAddress);
            _alignedAddress = baseAddress + offsetToAlign64;

            _curFreeIndex = -1;
        }

        public bool GetBlockSegment(out MemorySegment block)
        {
            var blockIndex = Interlocked.Increment(ref _curFreeIndex);
            if (blockIndex >= _blockCapacity)
            {
                block = default;
                return false;
            }
            block = new MemorySegment(_alignedAddress + (_blockSize * blockIndex), _blockSize);
            return true;
        }

        public void Dispose()
        {
            _array = null;
            _alignedAddress = null;
        }
    }

    internal class BlockSequence
    {
        internal struct DebugData
        {
            public long TotalCommitted;
            public long TotalAllocatedMemory;
            public long TotalFreeMemory;
            public int AllocatedSegmentCount;
            public int FreeSegmentCount;
            public int TotalHeaderSize;
            public int TotalPaddingSize;

            public bool IsCoherent => TotalCommitted == TotalAllocatedMemory + TotalFreeMemory + TotalHeaderSize + TotalPaddingSize;
        }

        private ExclusiveAccessControl _control;
        private SmallBlock _firstSmallBlock;

        public DefaultMemoryManager Owner { get; }
        internal DebugData DebugInfo;

        public BlockSequence(DefaultMemoryManager owner)
        {
            Owner = owner;
            _control = new ExclusiveAccessControl();
            _firstSmallBlock = owner.AllocateSmallBlock(this);
        }

        public MemorySegment Allocate(int size, int alignment = 16)
        {
            Debug.Assert(alignment is 16 or 32 or 64, "Supported alignment are 16, 32 or 64 bytes");

            try
            {
                var curBlock = _firstSmallBlock;
                while (true)
                {
                    var seg = curBlock.DoAllocate(size, ref DebugInfo);
                    if (seg.IsEmpty == false)
                    {
                        return seg;
                    }

                    // Couldn't allocate, try a defragmentation
                    //curBlock.DefragmentFreeSegments(ref DebugInfo);
                    //seg = curBlock.DoAllocate(size, ref DebugInfo);
                    //if (seg.IsEmpty == false)
                    //{
                    //    Console.Write("*");
                    //    return seg;
                    //}

                    // The first block couldn't make the allocation, go further in the block sequence. If there's no block after, we need to create one and
                    //  this must be made under a lock. Let's use the double-check lock pattern to avoid locking every-time there's already a block next.

                    // No block, create one...but under a lock
                    if (curBlock.NextSmallBlock == null)
                    {
                        try
                        {
                            // Lock
                            _control.TakeControl(null);
                            
                            // Another thread may have beaten us, so check if it's the case or not
                            if (curBlock.NextSmallBlock == null)
                            {
                                var newBlock = Owner.AllocateSmallBlock(this);
                                var next = _firstSmallBlock;
                                _firstSmallBlock = newBlock;
                                newBlock.NextSmallBlock = next;

                                curBlock = newBlock;
                            }
                            else
                            {
                                // If a concurrent thread already made the allocation, take its block as the new one
                                curBlock = curBlock.NextSmallBlock;
                            }
                        }
                        finally
                        {
                            _control.ReleaseControl();
                        }
                    }
                    else
                    {
                        curBlock = curBlock.NextSmallBlock;
                    }
                }
            }
            finally
            {
                _control.ReleaseControl();
            }
        }

        internal void DefragmentFreeSegments()
        {
            try
            {
                _control.TakeControl(null);

                var curBlock = _firstSmallBlock;
                while (curBlock != null)
                {
                    curBlock.DefragmentFreeSegments(ref DebugInfo);

                    curBlock = curBlock.NextSmallBlock;
                }
            }
            finally
            {
                _control.ReleaseControl();
            }
        }
    }

    internal class SmallBlock
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct SegmentHeader
        {
            public static readonly unsafe int MaxSegmentSize = 0x8000 - sizeof(SegmentHeader);
            //public static readonly unsafe int MaxSegmentSize = 64 - sizeof(SegmentHeader);

            public TwoWaysLinkedList<ushort>.Link Link;
            public ushort SmallBlockId;
            // It's very important for this field to be the last of the struct, we use its first bit to determine if we're dealing with a SmallBlock Segment 
            //  or a LargeBlock one.
            private ushort _data;

            public ushort SegmentSize
            {
                get => (ushort)(_data >> 1);
                set
                {
                    Debug.Assert(value <= MaxSegmentSize, $"requested size is too big, max is {MaxSegmentSize}");
                    _data = (ushort)((value << 1) | 1);
                }
            }

            public bool IsOwnedBySmallBlock => (_data & 1) == 1;
        }
        public volatile SmallBlock NextSmallBlock;

        private readonly BlockSequence _owner;
        private readonly MemorySegment _data;
        private ExclusiveAccessControl _control;
        private readonly ushort _smallBlockId;
        private TwoWaysLinkedList<ushort> _allocatedSegmentList;
        private TwoWaysLinkedList<ushort> _freeSegmentList;

        public unsafe SmallBlock(BlockSequence owner, ushort smallBlockId)
        {
            var data = owner.Owner.AllocateNativeBlockDataSegment();
            _owner = owner;
            _smallBlockId = smallBlockId;
            _data = data;
            _allocatedSegmentList = new TwoWaysLinkedList<ushort>(null, SegmentHeaderLinkAccessor);
            _freeSegmentList = new TwoWaysLinkedList<ushort>(null, SegmentHeaderLinkAccessor);
            NextSmallBlock = null;
            ref var debugInfo = ref owner.DebugInfo;
            Debug.Assert(debugInfo.IsCoherent);
            debugInfo.TotalCommitted += data.Length;

            // Setup the main & free list with empty segments that span the whole region. Each segment can be up to 64KiB - 16, that's why we need more than one
            var curOffset = 16;
            debugInfo.TotalPaddingSize += curOffset - sizeof(SegmentHeader);
            var remainingLength = _data.Length - (curOffset);
            while (remainingLength > 0)
            {
                Debug.Assert((curOffset & 0xF) == 0);
                var segId = (ushort)(curOffset >> 4);
                ref var header = ref SegmentHeaderAccessor(segId);
                var size = Math.Min(remainingLength, SegmentHeader.MaxSegmentSize);
                Debug.Assert((segId << 4) + size <= data.Length);

                header.SmallBlockId = _smallBlockId;
                header.SegmentSize = (ushort)size;
                _freeSegmentList.InsertLast(segId);

                debugInfo.TotalFreeMemory += size;
                debugInfo.TotalHeaderSize += sizeof(SegmentHeader);
                ++debugInfo.FreeSegmentCount;

                remainingLength -= sizeof(SegmentHeader) + size;
                curOffset += sizeof(SegmentHeader) + size;
            }
            Debug.Assert(debugInfo.IsCoherent);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe ref SegmentHeader SegmentHeaderAccessor(ushort id)
        {
            return ref Unsafe.AsRef<SegmentHeader>(_data.Address + (int)id * 16 - sizeof(SegmentHeader));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe ref TwoWaysLinkedList<ushort>.Link SegmentHeaderLinkAccessor(ushort id)
        {
            return ref Unsafe.AsRef<TwoWaysLinkedList<ushort>.Link>(_data.Address + (int)id * 16 - sizeof(SegmentHeader));
        }

        internal unsafe MemorySegment DoAllocate(int size, ref BlockSequence.DebugData debugInfo)
        {
            try
            {
                _control.TakeControl(null);

                var found = false;

                var curSegId = _freeSegmentList.FirstId;
                var headerSize = sizeof(SegmentHeader);
                byte* segAddress = default;
                while (curSegId != 0)
                {
                    ref var header = ref SegmentHeaderAccessor(curSegId);
                    var availableSize = (int)header.SegmentSize;
                    segAddress = (byte*)Unsafe.AsPointer(ref header) + headerSize;

                    if (size <= availableSize)
                    {
                        found = true;
                        break;
                    }

                    curSegId = _freeSegmentList.Next(curSegId);
                }

                // Not found? We need another SmallBlock
                if (!found)
                {
                    return default;
                }

                // Found, split the segment

                {
                    ref var curSegHeader = ref SegmentHeaderAccessor(curSegId);
                    Debug.Assert(curSegHeader.SegmentSize >= size);

                    // Check if the free segment can't be split because its size doesn't allow it
                    var requiredSize = (size + headerSize).Pad16() - headerSize;                    // Required size that is ensuring the next segment is aligned
                    if (curSegHeader.SegmentSize < (requiredSize + MinSegmentSize))
                    {
                        // Remove the segment from the free list...
                        _freeSegmentList.Remove(curSegId);
                        debugInfo.TotalFreeMemory -= curSegHeader.SegmentSize;
                        --debugInfo.FreeSegmentCount;

                        // ...and add it to the allocated one
                        _allocatedSegmentList.InsertLast(curSegId);
                        debugInfo.TotalAllocatedMemory += curSegHeader.SegmentSize;
                        ++debugInfo.AllocatedSegmentCount;
                    }

                    // We can split the free segment into one free (with the remaining size) and one allocated
                    else
                    {
                        var prevFreeSize = curSegHeader.SegmentSize;
                        var remainingSize = prevFreeSize - requiredSize;
                        curSegHeader.SegmentSize = (ushort)(remainingSize - headerSize);

                        var allocatedSegId = (ushort)(curSegId + (remainingSize >> 4));
                        ref var allocatedSegHeader = ref SegmentHeaderAccessor(allocatedSegId);
                        allocatedSegHeader.SmallBlockId = curSegHeader.SmallBlockId;
                        allocatedSegHeader.SegmentSize = (ushort)requiredSize;
                        _allocatedSegmentList.InsertLast(allocatedSegId);

                        debugInfo.TotalFreeMemory += remainingSize - headerSize - prevFreeSize;
                        debugInfo.TotalHeaderSize += headerSize;
                        debugInfo.TotalAllocatedMemory += requiredSize;
                        ++debugInfo.AllocatedSegmentCount;

                        segAddress = (byte*)Unsafe.AsPointer(ref allocatedSegHeader) + headerSize;
                    }

                    Debug.Assert(debugInfo.IsCoherent);
                    return new MemorySegment(segAddress, size);
                }
            }
            finally
            {
                _control.ReleaseControl();
            }
        }

        public unsafe bool Free(ref SegmentHeader header)
        {
            try
            {
                _control.TakeControl(null);

                // Get the id of the segment we're freeing
                var segId = (ushort)(((byte*)Unsafe.AsPointer(ref header) + sizeof(SegmentHeader) - _data.Address) >> 4);

                //_allocatedSegmentList.Remove(segId);
                _freeSegmentList.InsertLast(segId);

                ref var debugInfo = ref _owner.DebugInfo;
                --debugInfo.AllocatedSegmentCount;
                ++debugInfo.FreeSegmentCount;
                debugInfo.TotalAllocatedMemory -= header.SegmentSize;
                debugInfo.TotalFreeMemory += header.SegmentSize;

                return true;
            }
            finally
            {
                _control.ReleaseControl();
            }
        }

        internal unsafe void DefragmentFreeSegments(ref BlockSequence.DebugData debugInfo)
        {
            try
            {
                _control.TakeControl(null);
                var startFreeSegCount = _freeSegmentList.Count;
                if (startFreeSegCount < 2)
                {
                    return;
                }

                var headerSize = sizeof(SegmentHeader);
                var freeSegments = new (ushort, ushort)[_freeSegmentList.Count];
                var curSegId = _freeSegmentList.FirstId;
                var i = 0;
                while (curSegId != default)
                {
                    ref var header = ref SegmentHeaderAccessor(curSegId);
                    var length = (ushort)((header.SegmentSize + headerSize) >> 4);
                    freeSegments[i++] = (curSegId, length);

                    curSegId = _freeSegmentList.Next(curSegId);
                }

                Array.Sort(freeSegments, (a, b) => a.Item1 - b.Item1);

                var maxSize = (SegmentHeader.MaxSegmentSize >> 4) + 1;
                var end = freeSegments.Length - 1;

                // Merge adjacent free segment (if the combined size allows it)
                for (i = 0; i < end; i++)
                {
                    (ushort start, ushort length) a = freeSegments[i];
                    (ushort start, ushort length) b = freeSegments[i + 1];

                    var aEnd = a.start + a.length;
                    var bEnd = b.start + b.length;
                    if ((aEnd == b.start) && ((bEnd - a.start) <= maxSize))
                    {
                        ref var aHeader = ref SegmentHeaderAccessor(a.start);
                        aHeader.SegmentSize += (ushort)(b.length << 4);
                        _freeSegmentList.Remove(b.start);

                        --debugInfo.FreeSegmentCount;
                        debugInfo.TotalHeaderSize -= headerSize;
                        debugInfo.TotalFreeMemory += headerSize;

                        freeSegments[i + 1] = (a.start, (ushort)(a.length + b.length));
                    }
                }

                Debug.Assert(debugInfo.IsCoherent);
            }
            finally
            {
                _control.ReleaseControl();
            }
        }

        #region Debug Features

        internal MemorySegment Data => _data;
        internal ref TwoWaysLinkedList<ushort> AllocatedList => ref _allocatedSegmentList;
        internal ref TwoWaysLinkedList<ushort> FreeList => ref _freeSegmentList;

        #endregion
    }

    #endregion

    static DefaultMemoryManager()
    {
        SmallBlockInitialCount = Environment.ProcessorCount * 4;
    }


    private readonly List<NativeBlockInfo> _nativeBlockList;
    private ExclusiveAccessControl _nativeBlockListAccess;
    private NativeBlockInfo _curNativeBlockInfo;

    private BlockSequence[] _blockSequences;
    private int _blockSequenceCounter;
    
    private readonly List<SmallBlock> _smallBlockList;
    private ExclusiveAccessControl _smallBlockListAccess;

    // Note: seems like ThreadLocal doesn't release data allocated for a given thread when this one is destroyed.
    // Which means if the program create/destroy thousand of threads, this won't be good for us...
    private readonly ThreadLocal<BlockSequence> _assignedBlockSequence;

    public DefaultMemoryManager()
    {
        _nativeBlockList = new List<NativeBlockInfo>(16);
        _curNativeBlockInfo = new NativeBlockInfo(SmallBlockSize, SmallBlockInitialCount);
        _nativeBlockList.Add(_curNativeBlockInfo);

        _blockSequences = new BlockSequence[SmallBlockInitialCount];
        _smallBlockList = new List<SmallBlock>(SmallBlockInitialCount + SmallBlockGrowCount);
        
        for (var i = 0; i < SmallBlockInitialCount; i++)
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

        foreach (var nbi in _nativeBlockList)
        {
            nbi.Dispose();
        }
        _nativeBlockList.Clear();
        _smallBlockList.Clear();
        _assignedBlockSequence.Dispose();
    }

    public bool IsDisposed => _nativeBlockList.Count == 0;
    public int PinnedMemoryBlockSize { get; }

    public MemorySegment Allocate(int size)
    {
        if (size > MemorySegmentMaxSizeForSuperBlock)
        {
            // TODO Big alloc
            throw new NotImplementedException();
        }

        var block = _assignedBlockSequence.Value;
        return block.Allocate(size);
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

        return true;
    }

    public bool Free<T>(MemorySegment<T> segment) where T : unmanaged
    {
        return Free((MemorySegment)segment);
    }

    public void Clear()
    {
        throw new NotImplementedException();
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
                var nbi = new NativeBlockInfo(SmallBlockSize, SmallBlockGrowCount);
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

    private SmallBlock AllocateSmallBlock(BlockSequence owner)
    {
        try
        {
            _smallBlockListAccess.TakeControl(null);
            var smallBlockIndex = _smallBlockList.Count;
            var smallBlock = new SmallBlock(owner, (ushort)smallBlockIndex);
            _smallBlockList.Add(smallBlock);
            return smallBlock;
        }
        finally
        {
            _smallBlockListAccess.ReleaseControl();
        }
    }

    #region Debug Features

    internal BlockSequence GetThreadBlockSequence() => _assignedBlockSequence.Value;

    internal unsafe ref SmallBlock.SegmentHeader GetSegmentHeader(void* segmentAddress)
    {
        return ref Unsafe.AsRef<SmallBlock.SegmentHeader>((byte*)segmentAddress - sizeof(SmallBlock.SegmentHeader));
    }

    #endregion
}