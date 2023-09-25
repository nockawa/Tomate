using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
// ReSharper disable once RedundantUsingDirective
using System.Text;

namespace Tomate;

public partial class DefaultMemoryManager
{
    internal class LargeBlockAllocator : IBlockAllocator
    {
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

            public uint LastId
            {
                get
                {
                    if (IsEmpty)
                    {
                        return default;
                    }

                    return _accessor(_first).Previous;
                }
            }

            private readonly unsafe byte* _dataAddress;
            private uint _first;
            private int _count;

            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            private unsafe ref Link _accessor(uint id)
            {
                return ref Unsafe.AsRef<Link>(_dataAddress + (int)id * 16 - sizeof(SegmentHeader));
            }

            public unsafe TwoWaysLinkedList(byte* dataAddress)
            {
                _dataAddress = dataAddress;
                _first = default;
                _count = 0;
            }

            public uint InsertFirst(uint nodeId)
            {
                ref var node = ref _accessor(nodeId);

                if (_count == 0)
                {
                    _first = nodeId;
                    node.Previous = _first;
                    node.Next = default;

                    ++_count;
                    return nodeId;
                }

                ref var curFirst = ref _accessor(_first);

                node.Previous = curFirst.Previous;
                node.Next = _first;

                curFirst.Previous = nodeId;

                _first = nodeId;

                ++_count;
                return _first;
            }

            public uint InsertLast(uint curId)
            {
                ref var cur = ref _accessor(curId);

                if (_first == 0)
                {
                    return InsertFirst(curId);
                }

                ref var first = ref _accessor(_first);
                var lastId = first.Previous;
                ref var last = ref _accessor(lastId);

                first.Previous = curId;
                last.Next = curId;

                cur.Previous = lastId;
                cur.Next = default;

                ++_count;
                return curId;
            }

            public void Remove(uint id)
            {
                if (id == _first)
                {
                    ref var first = ref _accessor(id);
                    var nextId = first.Next;

                    if (nextId != 0)
                    {
                        ref var next = ref _accessor(nextId);
                        next.Previous = first.Previous;
                    }

                    _first = nextId;

                    first.Clear();
                    --_count;
                }
                else
                {
                    ref var cur = ref _accessor(id);
                    var prevId = cur.Previous;
                    var nextId = cur.Next;

                    ref var prev = ref _accessor(prevId);
                    prev.Next = nextId;

                    if (nextId != 0)
                    {
                        ref var next = ref _accessor(nextId);
                        next.Previous = prevId;
                    }
                    else
                    {
                        _accessor(_first).Previous = prevId;
                    }

                    cur.Clear();
                    --_count;
                }
            }

            public uint Previous(uint id)
            {
                if (id == _first)
                {
                    return default;
                }

                return _accessor(id).Previous;
            }

            public uint Next(uint id)
            {
                ref var n = ref _accessor(id);
                return n.Next;
            }

            public bool CheckIntegrity()
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
                    ref var header = ref _accessor(cur);
                    cur = header.Next;
                }

                // Check last item is the first's previous
                ref var firstHeader = ref _accessor(first);
                if (firstHeader.Previous != last)
                {
                    return false;
                }

                // Backward link integrity test
                var hashBackward = new HashSet<uint>(Count);
                cur = firstHeader.Previous;
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
                        ref var header = ref _accessor(cur);
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

        public volatile LargeBlockAllocator NextBlockAllocator;

        private BlockAllocatorSequence _owner;
        private readonly MemorySegment _data;
        private ExclusiveAccessControl _control;
        private TwoWaysLinkedList _occupiedSegmentList;
        private TwoWaysLinkedList _freedSegmentList;
        private int _totalAllocatedSegments;
        private int _totalFreeSegments;
        private readonly int _blockId;
        // ReSharper disable once NotAccessedField.Local
        private int _countBetweenDefrag;
        public int BlockIndex => _blockId;

#if DEBUGALLOC
        private readonly Dictionary<uint, MemoryBlockInfo> _allocatedBlocks = new();
#endif

        public unsafe LargeBlockAllocator(BlockAllocatorSequence owner, int minimumSize)
        {
            var data = owner.Owner.AllocateNativeBlock(minimumSize);
            _owner = owner;
            _blockId = BlockReferential.RegisterAllocator(this);
            _data = data;
            _occupiedSegmentList = new TwoWaysLinkedList(data.Address);
            _freedSegmentList = new TwoWaysLinkedList(data.Address);
            NextBlockAllocator = null;
            ref var debugInfo = ref owner.DebugInfo;
            Debug.Assert(debugInfo.IsCoherent);
            debugInfo.TotalCommitted += data.Length;

            // Setup the free list with one big empty segment that span the whole region.
            
            // Compute offset, size and id of the free segment
            var curOffset = sizeof(SegmentHeader).Pad16();
            debugInfo.TotalPaddingSize += curOffset - sizeof(SegmentHeader);
            var size = _data.Length - curOffset;
            var segId = (uint)(curOffset >> 4);
            ref var header = ref SegmentHeaderAccessor(segId);
            Debug.Assert((segId << 4) + size <= data.Length);

            // Setup of free segment
            header.GenHeader.IsFree = true;
            header.GenHeader.BlockIndex = _blockId;
            header.SegmentSize = size;
            header.GenHeader.RefCounter = 0;
            
            // Add it to the free list
            _freedSegmentList.InsertLast(segId);

            // Update debug info
            debugInfo.TotalFreeMemory += size;
            debugInfo.TotalHeaderSize += sizeof(SegmentHeader);
            ++debugInfo.FreeSegmentCount;
            ++_totalFreeSegments;
            Debug.Assert(debugInfo.IsCoherent);
        }

        public void Dispose()
        {
        }

        public void Recycle()
        {
            _owner = null;
        }

        public void Reassign(BlockAllocatorSequence newOwner)
        {
            _owner = newOwner;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private unsafe ref SegmentHeader SegmentHeaderAccessor(uint id)
        {
            return ref Unsafe.AsRef<SegmentHeader>(_data.Address + (int)id * 16 - sizeof(SegmentHeader));
        }

        internal unsafe bool DoAllocate(ref MemoryBlockInfo info, ref BlockAllocatorSequence.DebugData debugInfo, out MemoryBlock block)
        {
            try
            {
                var size = info.Size;
                _control.TakeControl(null);

                ++_countBetweenDefrag;
                var fragRatio = _totalAllocatedSegments / (float)_totalFreeSegments;
                if (_totalFreeSegments > 100 && fragRatio < 0.15f)
                {
                    ++debugInfo.FreeSegmentDefragCount;
                    DefragmentFreeSegments(ref debugInfo);
                    //var newFragRatio = _totalAllocatedSegments / (float)_totalFreeSegments;
                    //Console.WriteLine($"Defrag: was {fragRatio} now is {newFragRatio}, Alloc times between: {CountBetweenDefrag}");
                    _countBetweenDefrag = 0;
                }

                var found = false;

                var curSegId = _freedSegmentList.FirstId;
                var headerSize = sizeof(SegmentHeader);
                byte* segAddress = default;
                while (curSegId != 0)
                {
                    ref var header = ref SegmentHeaderAccessor(curSegId);
                    var availableSize = header.SegmentSize;
                    segAddress = (byte*)Unsafe.AsPointer(ref header) + headerSize;

                    if (size <= availableSize)
                    {
                        found = true;
                        break;
                    }
                    ++debugInfo.ScanFreeListCount;
                    curSegId = _freedSegmentList.Next(curSegId);
                }

                // Not found? We need another LargeBlock
                if (!found)
                {
                    block = default;
                    return false;
                }

                {
                    ref var curSegHeader = ref SegmentHeaderAccessor(curSegId);
                    Debug.Assert(curSegHeader.SegmentSize >= size);

                    // Check if the free segment can't be split because its size doesn't allow it
                    var requiredSize = (size + headerSize).Pad16() - headerSize;                    // Required size that is ensuring the next segment is aligned
                    if (curSegHeader.SegmentSize < requiredSize + MinSegmentSize)
                    {
                        // Remove the segment from the free list
                        _freedSegmentList.Remove(curSegId);
                        _occupiedSegmentList.InsertLast(curSegId);
                        curSegHeader.GenHeader.IsFree = false;
                        curSegHeader.GenHeader.RefCounter = 1;

                        // Update stats
                        debugInfo.TotalFreeMemory -= curSegHeader.SegmentSize;
                        --debugInfo.FreeSegmentCount;
                        debugInfo.TotalAllocatedMemory += curSegHeader.SegmentSize;
                        ++debugInfo.AllocatedSegmentCount;
                        --_totalFreeSegments;
                        ++_totalAllocatedSegments;

#if DEBUGALLOC
                        _allocatedBlocks.Add(curSegId, info);
#endif
                    }

                    // We can split the free segment into one free (with the remaining size) and one allocated
                    else
                    {
                        var prevFreeSize = curSegHeader.SegmentSize;
                        var remainingSize = prevFreeSize - requiredSize;
                        curSegHeader.SegmentSize = remainingSize - headerSize;

                        var allocatedSegId = (uint)(curSegId + (remainingSize >> 4));
                        ref var allocatedSegHeader = ref SegmentHeaderAccessor(allocatedSegId);
                        allocatedSegHeader.GenHeader.BlockIndex = curSegHeader.GenHeader.BlockIndex;
                        allocatedSegHeader.SegmentSize = requiredSize;
                        allocatedSegHeader.GenHeader.IsFree = false;
                        allocatedSegHeader.GenHeader.RefCounter = 1;
                        _occupiedSegmentList.InsertLast(allocatedSegId);

                        debugInfo.TotalFreeMemory += remainingSize - headerSize - prevFreeSize;
                        debugInfo.TotalHeaderSize += headerSize;
                        debugInfo.TotalAllocatedMemory += requiredSize;
                        ++debugInfo.AllocatedSegmentCount;
                        ++_totalAllocatedSegments;

#if DEBUGALLOC
                        _allocatedBlocks.Add(allocatedSegId, info);
#endif

                        segAddress = (byte*)Unsafe.AsPointer(ref allocatedSegHeader) + headerSize;
                    }

                    Debug.Assert(debugInfo.IsCoherent);
                    block = new MemoryBlock(segAddress, size);
                    Debug.Assert(segAddress >= _data.Address && segAddress + size <= _data.End);
                    return true;
                }
            }
            finally
            {
                _control.ReleaseControl();
            }
        }

        private unsafe bool Free(ref SegmentHeader header)
        {
            if (Interlocked.Decrement(ref header.GenHeader.RefCounter) > 0)
            {
                return false;
            }
            try
            {

                _control.TakeControl(null);

                Debug.Assert(header.GenHeader.IsFree == false, "This block was already freed");

                // Get the id of the segment we're freeing
                var segId = (uint)((byte*)Unsafe.AsPointer(ref header) + sizeof(SegmentHeader) - _data.Address >> 4);
#if DEBUGALLOC
                var res = _allocatedBlocks.Remove(segId);
                Debug.Assert(res, "The block is no longer in the debug allocated block list but still considered occupied in the block chain...");
#endif

                header.GenHeader.IsFree = true;
                _occupiedSegmentList.Remove(segId);
                _freedSegmentList.InsertLast(segId);

                ref var debugInfo = ref _owner.DebugInfo;
                --debugInfo.AllocatedSegmentCount;
                ++debugInfo.FreeSegmentCount;
                debugInfo.TotalAllocatedMemory -= header.SegmentSize;
                debugInfo.TotalFreeMemory += header.SegmentSize;
                --_totalAllocatedSegments;
                ++_totalFreeSegments;

                // Is the block now empty ?
                if (_totalAllocatedSegments == 0)
                {
                    _owner.RecycleBlock(this);
                }

                return true;
            }
            finally
            {
                _control.ReleaseControl();
            }
        }

        // Must be executed under instance's lock
        public unsafe bool Free(MemoryBlock block)
        {
            ref var lbh = ref Unsafe.AsRef<SegmentHeader>(block.MemorySegment.Address - sizeof(SegmentHeader));
            return Free(ref lbh);
        }

        private unsafe void DefragmentFreeSegments(ref BlockAllocatorSequence.DebugData debugInfo)
        {
            var startFreeSegCount = _freedSegmentList.Count;
            if (startFreeSegCount < 2)
            {
                return;
            }

            var headerSize = sizeof(SegmentHeader);
            var freeSegments = new (uint, int)[_freedSegmentList.Count];
            var curSegId = _freedSegmentList.FirstId;
            var i = 0;
            while (curSegId != default)
            {
                ref var header = ref SegmentHeaderAccessor(curSegId);
                var length = (header.SegmentSize + headerSize >> 4);
                freeSegments[i++] = (curSegId, length);

                curSegId = _freedSegmentList.Next(curSegId);
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
                    ref var aHeader = ref SegmentHeaderAccessor(a.start);
                    aHeader.SegmentSize += (b.length << 4);
                    _freedSegmentList.Remove(b.start);

                    --debugInfo.FreeSegmentCount;
                    --_totalFreeSegments;
                    debugInfo.TotalHeaderSize -= headerSize;
                    debugInfo.TotalFreeMemory += headerSize;

                    freeSegments[i + 1] = (a.start, (ushort)(a.length + b.length));
                }
            }

            Debug.Assert(debugInfo.IsCoherent);
        }

        #region Debug Features

        internal MemorySegment Data => _data;
        internal ref TwoWaysLinkedList FreeList => ref _freedSegmentList;

        #endregion

#if DEBUGALLOC
        public unsafe void DumpLeaks(StringBuilder sb, ref int totalLeakCount)
        {
            foreach (var kvp in _allocatedBlocks)
            {
                var segId = kvp.Key;
                var i = kvp.Value;
                var address = (ulong)_data.Address + (ulong)(16 * segId);
                sb.AppendLine($" - {i.SourceFile}:line {i.LineNb}, Address {address:X}, Length {i.Size}");
                ++totalLeakCount;
            }
        }
#endif
    }
}