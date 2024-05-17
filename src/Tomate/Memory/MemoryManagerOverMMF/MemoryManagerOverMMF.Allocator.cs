using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Tomate;

public unsafe partial class MemoryManagerOverMMF
{
    [PublicAPI]
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct SegmentHeader
    {
        public static readonly int MaxSegmentSize = 0x7FFFFFFF;

        // 0-8
        public TwoWaysLinkedList.Link Link;

        // 8-12
        public int SegmentSize;
        
        // 12-20
        public BlockReferential.GenBlockHeader GenHeader;
    }

    [PublicAPI]
    public struct TwoWaysLinkedList
    {
        public struct Link
        {
            public uint Previous;
            public uint Next;

            public void Clear()
            {
                Previous = Next = 0;
            }
        }

        public int Count => _count;
        public bool IsEmpty => _count == 0;

        public uint FirstId => _first;

        public uint GetLastId(byte* baseAddress)
        {
            if (IsEmpty)
            {
                return default;
            }

            return _accessor(baseAddress, _first).Previous;
        }

        private uint _first;
        private int _count;

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private ref Link _accessor(byte* baseAddress, uint id)
        {
            return ref Unsafe.AsRef<Link>(baseAddress + (int)id * 16 - sizeof(SegmentHeader));
        }

        public uint InsertFirst(byte* baseAddress, uint nodeId)
        {
            ref var node = ref _accessor(baseAddress, nodeId);

            if (_count == 0)
            {
                _first = nodeId;
                node.Previous = _first;
                node.Next = default;

                ++_count;
                return nodeId;
            }

            ref var curFirst = ref _accessor(baseAddress, _first);

            node.Previous = curFirst.Previous;
            node.Next = _first;

            curFirst.Previous = nodeId;

            _first = nodeId;

            ++_count;
            return _first;
        }

        public uint InsertLast(byte* baseAddress, uint curId)
        {
            ref var cur = ref _accessor(baseAddress, curId);

            if (_first == 0)
            {
                return InsertFirst(baseAddress, curId);
            }

            ref var first = ref _accessor(baseAddress, _first);
            var lastId = first.Previous;
            ref var last = ref _accessor(baseAddress, lastId);

            first.Previous = curId;
            last.Next = curId;

            cur.Previous = lastId;
            cur.Next = default;

            ++_count;
            return curId;
        }

        public void Remove(byte* baseAddress, uint id)
        {
            if (id == _first)
            {
                ref var first = ref _accessor(baseAddress, id);
                var nextId = first.Next;

                if (nextId != 0)
                {
                    ref var next = ref _accessor(baseAddress, nextId);
                    next.Previous = first.Previous;
                }

                _first = nextId;

                first.Clear();
                --_count;
            }
            else
            {
                ref var cur = ref _accessor(baseAddress, id);
                var prevId = cur.Previous;
                var nextId = cur.Next;

                ref var prev = ref _accessor(baseAddress, prevId);
                prev.Next = nextId;

                if (nextId != 0)
                {
                    ref var next = ref _accessor(baseAddress, nextId);
                    next.Previous = prevId;
                }
                else
                {
                    _accessor(baseAddress, _first).Previous = prevId;
                }

                cur.Clear();
                --_count;
            }
        }

        public uint Previous(byte* baseAddress, uint id)
        {
            if (id == _first)
            {
                return default;
            }

            return _accessor(baseAddress, id).Previous;
        }

        public uint Next(byte* baseAddress, uint id)
        {
            ref var n = ref _accessor(baseAddress, id);
            return n.Next;
        }

        public bool CheckIntegrity(byte* baseAddress)
        {
            var hashForward = new HashSet<uint>(Count);

            // Forward link integrity test
            var cur = _first;
            var first = cur;
            var last = cur;
            while (cur != 0)
            {
                last = cur;
                if (hashForward.Add(cur) == false)
                {
                    return false;
                }
                ref var header = ref _accessor(baseAddress, cur);
                cur = header.Next;
            }

            // Check last item is the first's previous
            ref var firstHeader = ref _accessor(baseAddress, first);
            if (firstHeader.Previous != last)
            {
                return false;
            }

            // Backward link integrity test
            var hashBackward = new HashSet<uint>(Count);
            cur = firstHeader.Previous;
            //last = cur;
            first = cur;
            while (cur != 0)
            {
                first = cur;
                if (hashBackward.Add(cur) == false)
                {
                    return false;
                }

                if (_first == cur)
                {
                    cur = default;
                }
                else
                {
                    ref var header = ref _accessor(baseAddress, cur);
                    cur = header.Previous;
                }
            }

            // Check first
            if (_first != first)
            {
                return false;
            }

            if (hashForward.Count != hashBackward.Count)
            {
                return false;
            }

            foreach (var e in hashForward)
            {
                if (hashBackward.Contains(e) == false)
                {
                    return false;
                }
            }

            return true;
        }
    }
    
    [PublicAPI]
    private struct BlockAllocator
    {
        public TwoWaysLinkedList OccupiedSegmentList;
        public TwoWaysLinkedList FreedSegmentList;
        public ExclusiveAccessControl AccessControl;
        public int NextAllocatorPageId;
        public float TotalFreeSegments;
        public double TotalAllocatedSegments;
        public int CountBetweenDefrag;
    }

    [PublicAPI]
    internal struct DebugData
    {
        public long TotalCommitted;
        public long TotalAllocatedMemory;
        public long TotalFreeMemory;
        public long ScanFreeListCount;
        public int FreeSegmentDefragCount;
        public int AllocatedSegmentCount;
        public int FreeSegmentCount;
        public int TotalHeaderSize;
        public int TotalPaddingSize;
        public int TotalBlockCount;

        public bool IsCoherent => TotalCommitted == TotalAllocatedMemory + TotalFreeMemory + TotalHeaderSize + TotalPaddingSize;
    }
    
    private int GetAllocatorStartingSegmentId()
    {
        // Get the allocator index to use with the calling process/thread 
        var allocatorIndex = _assignedAllocator.Value;
        
        // Check for uninitialized allocator
        if (_allocators[allocatorIndex] == 0)
        {
            // Allocate the page that will store the data for this allocator
            var blockData = AllocatePages(1);
            var blockId = ToBlockId(blockData);
            
            // Assign and detect race condition
            if (Interlocked.CompareExchange(ref _allocators[allocatorIndex], blockId, 0) != 0)
            {
                // Another process/thread beat us by setting its PageId, so we free ours and use this one instead
                FreePages(blockData);
                Thread.Sleep(0);    // Force thread switch to ensure the other thread is initializing the block completely
            }
            else
            {
                InitializeBlockAllocator(blockId, blockData);
            }
        }

        return _allocators[allocatorIndex];
    }

    private int GetNextAllocatorSegmentId(ref BlockAllocator blockAllocator, int requiredSize)
    {
        // Check if we have a next allocator in the linked list
        if (blockAllocator.NextAllocatorPageId != 0)
        {
            return blockAllocator.NextAllocatorPageId;
        }

        int pageCount = 1;
        int headersSize = sizeof(BlockAllocator).Pad16() + sizeof(SegmentHeader).Pad16();
        if ((requiredSize + headersSize) > PageSize)
        {
            pageCount = (int)Math.Ceiling(requiredSize / (double)PageSize);
            var maxSize = (PageSize * 64) - headersSize;
            Debug.Assert(pageCount <= maxSize, $"The requested block size is too big, maximum is {maxSize}");
        }
        
        // Allocate the page that will store the data for this allocator
        var blockData = AllocatePages(pageCount);
        var blockId = ToBlockId(blockData);
        blockData.Slice(sizeof(BlockAllocator)).ToSpan<byte>().Clear();
            
        // Assign and detect race condition
        var allocatorIndex = _assignedAllocator.Value;
        while (true)
        {
            var curFirstSegmentId = _allocators[allocatorIndex];
            if (Interlocked.CompareExchange(ref _allocators[allocatorIndex], blockId, curFirstSegmentId) == curFirstSegmentId)
            {
                InitializeBlockAllocator(blockId, blockData, curFirstSegmentId);
                break;
            }
        }

        return blockId;
    }

    private ref BlockAllocator GetAllocator(int pageId, out MemorySegment data)
    {
        var dataPage = FromBlockId(pageId);
        var (h, d) = dataPage.Split(sizeof(BlockAllocator).Pad16());
        data = d;
        return ref h.Cast<BlockAllocator>().AsRef();
    }

    private void InitializeBlockAllocator(int blockPageId, MemorySegment blockData, int nextAllocatorPageId = 0)
    {
        // Initialize the header
        var (hms, data) = blockData.Split(sizeof(BlockAllocator).Pad16());
        hms.ToSpan<byte>().Clear();
        ref var h = ref hms.Cast<BlockAllocator>().AsRef();
        h.NextAllocatorPageId = nextAllocatorPageId;
        
        // Compute offset, size and id of the free segment
        var curOffset = sizeof(SegmentHeader).Pad16();
        var size = data.Length - curOffset;
        var segId = (uint)(curOffset >> 4);
        ref var header = ref SegmentHeaderAccessor(data, segId);
        Debug.Assert((segId << 4) + size <= data.Length);

        // Setup of free segment
        header.GenHeader.IsFree = true;
        header.GenHeader.IsFromMMF = true;
        header.GenHeader.BlockIndex = blockPageId;
        header.SegmentSize = size;
        header.GenHeader.RefCounter = 0;
            
        // Add it to the free list
        h.FreedSegmentList.InsertLast(data, segId);
        
        // Update debug info
        ref var debugInfo = ref DebugInfo;
        debugInfo.TotalCommitted += data.Length;
        debugInfo.TotalFreeMemory += size;
        debugInfo.TotalPaddingSize += curOffset - sizeof(SegmentHeader);
        debugInfo.TotalHeaderSize += sizeof(SegmentHeader);
        ++debugInfo.FreeSegmentCount;
        ++debugInfo.TotalBlockCount;
        Debug.Assert(debugInfo.IsCoherent);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private ref SegmentHeader SegmentHeaderAccessor(byte* baseAddress, uint id)
    {
        return ref Unsafe.AsRef<SegmentHeader>(baseAddress + (int)id * 16 - sizeof(SegmentHeader));
    }
    
#if DEBUGALLOC
    public MemoryBlock Allocate(int length, [CallerFilePath] string sourceFile = "", [CallerLineNumber] int lineNb = 0)
#else
    public MemoryBlock Allocate(int length)
#endif
    {
        var pageId = GetAllocatorStartingSegmentId();

        while (true)
        {
            ref var allocator = ref GetAllocator(pageId, out var data);
            if (DoAllocate(ref allocator, data, length, out var memoryBlock))
            {
                return memoryBlock;
            }

            pageId = GetNextAllocatorSegmentId(ref allocator, length);
        }
    }

    private bool DoAllocate(ref BlockAllocator blockAllocator, MemorySegment data, int size, out MemoryBlock memoryBlock)
    {
        try
        {
            blockAllocator.AccessControl.TakeControl(null);

            ++blockAllocator.CountBetweenDefrag;
            var fragRatio = blockAllocator.TotalAllocatedSegments / blockAllocator.TotalFreeSegments;
            if (blockAllocator.TotalFreeSegments > 100 && fragRatio < 0.15f)
            {
                ++DebugInfo.FreeSegmentDefragCount;
                DefragmentFreeSegments(ref blockAllocator, data);
                blockAllocator.CountBetweenDefrag = 0;
            }

            var found = false;

            var baseAddress = data.Address;
            var curSegId = blockAllocator.FreedSegmentList.FirstId;
            var headerSize = sizeof(SegmentHeader);
            byte* segAddress = default;
            while (curSegId != 0)
            {
                ref var header = ref SegmentHeaderAccessor(baseAddress, curSegId);
                var availableSize = header.SegmentSize;
                segAddress = (byte*)Unsafe.AsPointer(ref header) + headerSize;

                if (size <= availableSize)
                {
                    found = true;
                    break;
                }
                ++DebugInfo.ScanFreeListCount;
                curSegId = blockAllocator.FreedSegmentList.Next(baseAddress, curSegId);
            }

            // Not found? We need another LargeBlock
            if (!found)
            {
                memoryBlock = default;
                return false;
            }

            {
                ref var curSegHeader = ref SegmentHeaderAccessor(baseAddress, curSegId);
                Debug.Assert(curSegHeader.SegmentSize >= size);

                // Check if the free segment can't be split because its size doesn't allow it
                var requiredSize = (size + headerSize).Pad16() - headerSize;                    // Required size that is ensuring the next segment is aligned
                if (curSegHeader.SegmentSize < requiredSize + MinSegmentSize)
                {
                    // Remove the segment from the free list
                    blockAllocator.FreedSegmentList.Remove(baseAddress, curSegId);
                    blockAllocator.OccupiedSegmentList.InsertLast(baseAddress, curSegId);
                    curSegHeader.GenHeader.IsFree = false;
                    curSegHeader.GenHeader.RefCounter = 1;

                    // Update stats
                    DebugInfo.TotalFreeMemory -= curSegHeader.SegmentSize;
                    --DebugInfo.FreeSegmentCount;
                    DebugInfo.TotalAllocatedMemory += curSegHeader.SegmentSize;
                    ++DebugInfo.AllocatedSegmentCount;
                    --blockAllocator.TotalFreeSegments;
                    ++blockAllocator.TotalAllocatedSegments;

/*
#if DEBUGALLOC
                    _allocatedBlocks.Add(curSegId, info);
#endif
*/
                }

                // We can split the free segment into one free (with the remaining size) and one allocated
                else
                {
                    var prevFreeSize = curSegHeader.SegmentSize;
                    var remainingSize = prevFreeSize - requiredSize;
                    curSegHeader.SegmentSize = remainingSize - headerSize;

                    var allocatedSegId = (uint)(curSegId + (remainingSize >> 4));
                    ref var allocatedSegHeader = ref SegmentHeaderAccessor(baseAddress, allocatedSegId);
                    allocatedSegHeader.GenHeader.IsFree = false;
                    allocatedSegHeader.GenHeader.IsFromMMF = true;
                    allocatedSegHeader.GenHeader.BlockIndex = curSegHeader.GenHeader.BlockIndex;
                    allocatedSegHeader.SegmentSize = requiredSize;
                    allocatedSegHeader.GenHeader.RefCounter = 1;
                    blockAllocator.OccupiedSegmentList.InsertLast(baseAddress, allocatedSegId);

                    DebugInfo.TotalFreeMemory += remainingSize - headerSize - prevFreeSize;
                    DebugInfo.TotalHeaderSize += headerSize;
                    DebugInfo.TotalAllocatedMemory += requiredSize;
                    ++DebugInfo.AllocatedSegmentCount;
                    ++blockAllocator.TotalAllocatedSegments;

/*
#if DEBUGALLOC
                    _allocatedBlocks.Add(allocatedSegId, info);
#endif
*/

                    segAddress = (byte*)Unsafe.AsPointer(ref allocatedSegHeader) + headerSize;
                }

                Debug.Assert(DebugInfo.IsCoherent);
                memoryBlock = new MemoryBlock(segAddress, size, _mmfId);
                return true;
            }
        }
        finally
        {
            blockAllocator.AccessControl.ReleaseControl();
        }
    }

#if DEBUGALLOC
    public MemoryBlock<T> Allocate<T>(int length, [CallerFilePath] string sourceFile = "", [CallerLineNumber] int lineNb = 0) where T : unmanaged
#else
    public MemoryBlock<T> Allocate<T>(int length) where T : unmanaged
#endif
    {
        return Allocate(length * sizeof(T)).Cast<T>();
    }

    public bool Free(MemoryBlock block)
    {
        ref var header = ref Unsafe.AsRef<SegmentHeader>(block.MemorySegment.Address - sizeof(SegmentHeader));
        if (Interlocked.Decrement(ref header.GenHeader.RefCounter) > 0)
        {
            return false;
        }

        var allocatorMemorySegment = FromBlockId(header.GenHeader.BlockIndex);
        var allocatorDataAddress = allocatorMemorySegment.Address + sizeof(BlockAllocator).Pad16();
        ref var allocator = ref allocatorMemorySegment.Cast<BlockAllocator>().AsRef();
        allocator.AccessControl.TakeControl(null);
        try
        {

            Debug.Assert(header.GenHeader.IsFree == false, "This block was already freed");

            // Get the id of the segment we're freeing
            var segId = (uint)(block.MemorySegment.Address - allocatorDataAddress) >> 4;
/*
#if DEBUGALLOC
                var res = _allocatedBlocks.Remove(segId);
                Debug.Assert(res, "The block is no longer in the debug allocated block list but still considered occupied in the block chain...");
#endif
*/

            header.GenHeader.IsFree = true;
            allocator.OccupiedSegmentList.Remove(allocatorDataAddress, segId);
            allocator.FreedSegmentList.InsertLast(allocatorDataAddress, segId);

            ref var debugInfo = ref DebugInfo;
            --debugInfo.AllocatedSegmentCount;
            ++debugInfo.FreeSegmentCount;
            debugInfo.TotalAllocatedMemory -= header.SegmentSize;
            debugInfo.TotalFreeMemory += header.SegmentSize;
            --allocator.TotalAllocatedSegments;
            ++allocator.TotalFreeSegments;
            Debug.Assert(debugInfo.IsCoherent);

            // Is the block now empty ?
            if (allocator.TotalAllocatedSegments == 0)
            {
            }

            return true;
        }
        finally
        {
            allocator.AccessControl.ReleaseControl();
        }
    }

    public bool Free<T>(MemoryBlock<T> block) where T : unmanaged
    {
        return BlockReferential.Free(block);
    }

    public void Defragment()
    {
        for (var i = 0; i < _allocators.Length; i++)
        {
            if (_allocators[i] == 0)
            {
                continue;
            }

            ref var allocator = ref GetAllocator(_allocators[i], out var data);
            while (true)
            {
                DefragmentFreeSegments(ref allocator, data);

                if (allocator.NextAllocatorPageId == 0)
                {
                    break;
                }

                allocator = ref GetAllocator(allocator.NextAllocatorPageId, out data);
            }
        }
    }

    private void DefragmentFreeSegments(ref BlockAllocator blockAllocator, MemorySegment memorySegment)
    {
        ref var debugInfo = ref DebugInfo;
        var startFreeSegCount = blockAllocator.FreedSegmentList.Count;
        if (startFreeSegCount < 2)
        {
            return;
        }

        var baseAddress = memorySegment.Address;
        var headerSize = sizeof(SegmentHeader);
        var freeSegments = new (uint, int)[blockAllocator.FreedSegmentList.Count];
        var curSegId = blockAllocator.FreedSegmentList.FirstId;
        var i = 0;
        while (curSegId != default)
        {
            ref var header = ref SegmentHeaderAccessor(baseAddress, curSegId);
            var length = (header.SegmentSize + headerSize >> 4);
            freeSegments[i++] = (curSegId, length);

            curSegId = blockAllocator.FreedSegmentList.Next(baseAddress, curSegId);
        }

        Array.Sort(freeSegments, (a, b) => (int)(a.Item1 - b.Item1));

        var maxSize = (SegmentHeader.MaxSegmentSize >> 4) + 1;
        var end = freeSegments.Length - 1;

        // Merge adjacent free segment (if the combined size allows it)
        for (i = 0; i < end; i++)
        {
            (uint start, int length) a = freeSegments[i];
            (uint start, int length) b = freeSegments[i + 1];

            var aEnd = a.start + a.length;
            var bEnd = b.start + b.length;
            if (aEnd == b.start && bEnd - a.start <= maxSize)
            {
                ref var aHeader = ref SegmentHeaderAccessor(baseAddress, a.start);
                aHeader.SegmentSize += (b.length << 4);
                blockAllocator.FreedSegmentList.Remove(baseAddress, b.start);

                --debugInfo.FreeSegmentCount;
                --blockAllocator.TotalFreeSegments;
                debugInfo.TotalHeaderSize -= headerSize;
                debugInfo.TotalFreeMemory += headerSize;

                freeSegments[i + 1] = (a.start, (ushort)(a.length + b.length));
            }
        }

        Debug.Assert(debugInfo.IsCoherent);
    }

    public void Clear()
    {
        throw new NotImplementedException();
    }
}