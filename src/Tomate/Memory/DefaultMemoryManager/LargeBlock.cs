using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tomate;

public partial class DefaultMemoryManager
{
    internal class LargeBlock
    {
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        public struct SegmentHeader
        {
            public static readonly int MaxSegmentSize = 0x7FFFFFFF;

            public TwoWaysLinkedList.Link Link;
            public ushort LargeBlockId;

            private ushort _dataMSB;
            // It's very important for this field to be the last of the struct, we use its first bit to determine if we're dealing with a SmallBlock Segment 
            //  or a LargeBlock one.
            private ushort _dataLSB;

            public int SegmentSize
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
                get => (int)((_dataLSB >> 1) | (_dataMSB << 15));
                [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
                set
                {
                    Debug.Assert(value <= MaxSegmentSize, $"requested size is too big, max is {MaxSegmentSize}");
                    _dataLSB = (ushort)(value << 1);
                    _dataMSB = (ushort)(value >> 15);
                }
            }

            public bool IsOwnedByLargeBlock => (_dataLSB & 1) == 0;
        }

        public struct TwoWaysLinkedList
        {
            public struct Link
            {
                public uint Previous;
                public uint Next;
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
                return ref Unsafe.AsRef<Link>(_dataAddress + (int)id * 16 - sizeof(LargeBlock.SegmentHeader));
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

                    first.Previous = first.Next = default;
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

                    cur.Previous = cur.Next = default;
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
                last = cur;
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

        public volatile LargeBlock NextBlock;

        private BlockSequence _owner;
        private readonly MemorySegment _data;
        private ExclusiveAccessControl _control;
        private TwoWaysLinkedList _freeSegmentList;
        private int _totalAllocatedSegments;
        private int _totalFreeSegments;
        private readonly ushort _largeBlockId;
        private int _countBetweenDefrag;

        public unsafe LargeBlock(BlockSequence owner, ushort largeBlockId, int minimumSize)
        {
            var data = owner.Owner.AllocateNativeBlock(minimumSize);
            _owner = owner;
            _largeBlockId = largeBlockId;
            _data = data;
            _freeSegmentList = new TwoWaysLinkedList(data.Address);
            NextBlock = null;
            ref var debugInfo = ref owner.DebugInfo;
            Debug.Assert(debugInfo.IsCoherent);
            debugInfo.TotalCommitted += data.Length;

            // Setup the main & free list with empty segments that span the whole region. Each segment can be up to 64KiB - 16, that's why we need more than one
            var curOffset = 16;
            debugInfo.TotalPaddingSize += curOffset - sizeof(SegmentHeader);
            var size = _data.Length - curOffset;
            var segId = (uint)(curOffset >> 4);
            ref var header = ref SegmentHeaderAccessor(segId);
            Debug.Assert((segId << 4) + size <= data.Length);

            header.LargeBlockId = _largeBlockId;
            header.SegmentSize = size;
            _freeSegmentList.InsertLast(segId);

            debugInfo.TotalFreeMemory += size;
            debugInfo.TotalHeaderSize += sizeof(SegmentHeader);
            ++debugInfo.FreeSegmentCount;
            ++_totalFreeSegments;
            Debug.Assert(debugInfo.IsCoherent);
        }
        public void Recycle()
        {
            _owner = null;
        }

        public void Reassign(BlockSequence newOwner)
        {
            _owner = newOwner;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private unsafe ref SegmentHeader SegmentHeaderAccessor(uint id)
        {
            return ref Unsafe.AsRef<SegmentHeader>(_data.Address + (int)id * 16 - sizeof(SegmentHeader));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private unsafe ref TwoWaysLinkedList.Link SegmentHeaderLinkAccessor(uint id)
        {
            return ref Unsafe.AsRef<TwoWaysLinkedList.Link>(_data.Address + (int)id * 16 - sizeof(SegmentHeader));
        }

        internal unsafe bool DoAllocate(int size, ref BlockSequence.DebugData debugInfo, out MemorySegment segment)
        {
            try
            {
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

                var curSegId = _freeSegmentList.FirstId;
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
                    curSegId = _freeSegmentList.Next(curSegId);
                }

                // Not found? We need another LargeBlock
                if (!found)
                {
                    segment = default;
                    return false;
                }

                // Found, split the segment

                {
                    ref var curSegHeader = ref SegmentHeaderAccessor(curSegId);
                    Debug.Assert(curSegHeader.SegmentSize >= size);

                    // Check if the free segment can't be split because its size doesn't allow it
                    var requiredSize = (size + headerSize).Pad16() - headerSize;                    // Required size that is ensuring the next segment is aligned
                    if (curSegHeader.SegmentSize < requiredSize + MinSegmentSize)
                    {
                        // Remove the segment from the free list
                        _freeSegmentList.Remove(curSegId);

                        // Update stats
                        debugInfo.TotalFreeMemory -= curSegHeader.SegmentSize;
                        --debugInfo.FreeSegmentCount;
                        debugInfo.TotalAllocatedMemory += curSegHeader.SegmentSize;
                        ++debugInfo.AllocatedSegmentCount;
                        --_totalFreeSegments;
                        ++_totalAllocatedSegments;
                    }

                    // We can split the free segment into one free (with the remaining size) and one allocated
                    else
                    {
                        var prevFreeSize = curSegHeader.SegmentSize;
                        var remainingSize = prevFreeSize - requiredSize;
                        curSegHeader.SegmentSize = remainingSize - headerSize;

                        var allocatedSegId = (uint)(curSegId + (remainingSize >> 4));
                        ref var allocatedSegHeader = ref SegmentHeaderAccessor(allocatedSegId);
                        allocatedSegHeader.LargeBlockId = curSegHeader.LargeBlockId;
                        allocatedSegHeader.SegmentSize = requiredSize;

                        debugInfo.TotalFreeMemory += remainingSize - headerSize - prevFreeSize;
                        debugInfo.TotalHeaderSize += headerSize;
                        debugInfo.TotalAllocatedMemory += requiredSize;
                        ++debugInfo.AllocatedSegmentCount;
                        ++_totalAllocatedSegments;

                        segAddress = (byte*)Unsafe.AsPointer(ref allocatedSegHeader) + headerSize;
                    }

                    Debug.Assert(debugInfo.IsCoherent);
                    segment = new MemorySegment(segAddress, size);
                    return true;
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
                var segId = (uint)((byte*)Unsafe.AsPointer(ref header) + sizeof(SegmentHeader) - _data.Address >> 4);

                _freeSegmentList.InsertLast(segId);

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
        internal unsafe void DefragmentFreeSegments(ref BlockSequence.DebugData debugInfo)
        {
            var startFreeSegCount = _freeSegmentList.Count;
            if (startFreeSegCount < 2)
            {
                return;
            }

            var headerSize = sizeof(SegmentHeader);
            var freeSegments = new (uint, int)[_freeSegmentList.Count];
            var curSegId = _freeSegmentList.FirstId;
            var i = 0;
            while (curSegId != default)
            {
                ref var header = ref SegmentHeaderAccessor(curSegId);
                var length = (header.SegmentSize + headerSize >> 4);
                freeSegments[i++] = (curSegId, length);

                curSegId = _freeSegmentList.Next(curSegId);
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
                    _freeSegmentList.Remove(b.start);

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
        internal ref TwoWaysLinkedList FreeList => ref _freeSegmentList;

        #endregion
    }
}