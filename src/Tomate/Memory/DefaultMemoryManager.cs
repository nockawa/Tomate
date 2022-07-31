using System.Diagnostics;
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
/// The memory manager will allocate SuperBlock (each will be an OS mem alloc), each SuperBlock will be split into x Block.
/// A block is used to allocate/free memory segments and is independent from other: two different threads may allocate concurrently on different
/// blocks, but if two threads allocate/free on the same block: there will be a stall.
/// Rule of thumb decides there will be (4 x nb CPU Cores) of non-full Blocks at a given time, when a thread allocates for the first time, it will
/// be assigned a Block (determined in round-robin fashion). This should make contention acceptable.
/// It's possible for a Thread A to make an alloc and later on a Thread B to free this particular block, such scenario could worsen contention, but
/// still, should be ok.
/// Blocks are 1 MiB of size, they allocate data ranging from 0 to 64 KiB, allocated memory segment will be aligned on 16 bytes.
/// If the user allocates a segment greater than 64 KiB, we will defer the operation to a MegaBlock.
/// A MegaBlock is at least 64 MiB or sized with next power of 2 of the allocation that triggered it (if there is no existing MegaBlock that can
/// fulfill the request), it is not associated to a particular thread.
/// Any memory segments allocated from a MegaBlock is 64 bytes aligned.
///
/// Each allocation has a "descriptor", it's a private header that precede the address of the block we return to the user. This header contains
/// all the required data for the memory manager to function.
/// On segments allocated from Blocks, the header is 10 bytes long. On segments allocated from MegaBlock, the header is 20 bytes long.
///
/// All segments inside a Block are linked with a two ways linked list that is ordered by their address. All free segments are also linked
/// with a dedicated linked list, following the same principals. When a segment is freed, we will be able to determine if its adjacent segments are
/// also free and we'll merge them into one block if it's possible, in order to create one bigger free segment and fight fragmentation.
/// </para>
/// </remarks>
public class DefaultMemoryManager : IDisposable, IMemoryManager
{
    public static int SBBlockInitialCount { get; }
    public static readonly int BlockSize = 1024 * 1024;
    public static readonly int MegaBlockBSize = 64 * 1024 * 1024;
    public static readonly int MemorySegmentMaxSizeForSuperBlock = 64 * 1024;

    #region Internal Types

    private unsafe class NativeBlockInfo
    {
        private readonly int _blockSize;
        private readonly int _blockCapacity;
        private readonly byte* _alignedAddress;
        private readonly byte[] _array;

        public int BlockCount;

        public NativeBlockInfo(int blockSize, int blockCount)
        {
            var nativeBlockSize = blockSize * blockCount;
            _blockSize = blockSize;
            _blockCapacity = blockCount;
            _array = GC.AllocateUninitializedArray<byte>(nativeBlockSize + 63, true);
            var baseAddress = (byte*)Marshal.UnsafeAddrOfPinnedArrayElement(_array, 0).ToPointer();

            var offsetToAlign64 = (int)((((long)baseAddress + 63L) & -64L) - (long)baseAddress);
            _alignedAddress = baseAddress + offsetToAlign64;

            BlockCount = 0;
        }

        public MemorySegment GetBlockSegment(int index)
        {
            Debug.Assert(index < _blockCapacity);
            return new MemorySegment(_alignedAddress + (_blockSize * index), _blockSize);
        }
    }
    private class SBBlock
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct SegmentHeader
        {
            public TwoWaysLinkedList<ushort>.Link MainList;
            public TwoWaysLinkedList<ushort>.Link FreeList;
            public ushort SegmentSize;                                                      // Size in byte, max size of a segment is 65535 bytes
            public bool IsFree => FreeList.Previous == default && FreeList.Next == default;
        }
        private ExclusiveAccessControl _control;
        private TwoWaysLinkedList<ushort> _segmentMainList;
        private TwoWaysLinkedList<ushort> _segmentFreeList;
        private MemorySegment _data;

        public SBBlock(MemorySegment data)
        {
            _control = new ExclusiveAccessControl();
            _data = data;
            _segmentMainList = new TwoWaysLinkedList<ushort>(null, SegmentHeaderMainLinkAccessor);
            _segmentFreeList = new TwoWaysLinkedList<ushort>(null, SegmentHeaderFreeLinkAccessor);

            // Setup the main & free list with empty segments that span the whole region. Each segment can be up to 64KiB - 16, that's why we need more than one
            var remainingLength = _data.Length;
            var curOffset = 16;
            while (remainingLength > 0)
            {
                Debug.Assert((curOffset&0xF) == 0);
                ref var header = ref SegmentHeaderAccessor((ushort)(curOffset>>4));
                var size = Math.Min(remainingLength, ushort.MaxValue - 15);

                header.SegmentSize = (ushort)size;
                _segmentMainList.InsertLast((ushort)(curOffset >> 4));
                _segmentFreeList.InsertLast((ushort)(curOffset >> 4));

                remainingLength -= 16 + size;
                curOffset += 16 + size;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe ref SegmentHeader SegmentHeaderAccessor(ushort id)
        {
            return ref Unsafe.AsRef<SegmentHeader>(_data.Address + (int)id * 16 - sizeof(SegmentHeader));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe ref TwoWaysLinkedList<ushort>.Link SegmentHeaderMainLinkAccessor(ushort id)
        {
            return ref Unsafe.AsRef<TwoWaysLinkedList<ushort>.Link>(_data.Address + (int)id * 16 - sizeof(SegmentHeader));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe ref TwoWaysLinkedList<ushort>.Link SegmentHeaderFreeLinkAccessor(ushort id)
        {
            return ref Unsafe.AsRef<TwoWaysLinkedList<ushort>.Link>(_data.Address + (int)id * 16 - sizeof(SegmentHeader) + sizeof(TwoWaysLinkedList<ushort>.Link));
        }

        public unsafe MemorySegment Allocate(int size, int alignment = 16)
        {
            Debug.Assert(alignment is 16 or 32 or 64, "Supported alignment are 16, 32 or 64 bytes");

            // Let's keep this pretty simple, we have two linked lists, the first one with all the segments, ordered by memory address, the second one with only the free
            //  segments, also ordered by address.
            // We keep the order by address to be able to merge adjacent free segments.
            // For the allocation, we look for a free segment that is big enough, we take it and split it (if possible).
            try
            {
                _control.TakeControl(TimeSpan.MaxValue);

                var found = false;
                void* newSegAddress = default;
                void* curSegAddress = default;

                var curSegId = _segmentFreeList.FirstId;
                while (curSegId != 0)
                {
                    ref var header = ref SegmentHeaderAccessor(curSegId);
                    var requiredSize = (int)header.SegmentSize;

                    curSegAddress = (byte*)Unsafe.AsPointer(ref header) + sizeof(SegmentHeader);
                    newSegAddress = requiredSize.Align(curSegAddress, alignment);       // requiredSize could change due to alignment
                    if (requiredSize >= size)
                    {
                        found = true;
                        break;
                    }

                    curSegId = _segmentFreeList.Next(curSegId);
                }

                // Not found? We need another SBBlock
                if (!found)
                {

                }

                // Found, split the segment

                // First, check if the segment Id need to be fixed because of alignment
                // Alignment may shift the address of the segment, so will the header, so will the id.
                if (alignment > 16)
                {
                    var newSegId = (ushort)(((long)newSegAddress - (long)curSegAddress) >> 4);
                    if (curSegId != newSegId)
                    {
                        // We need to fix the prev & next of main & free, prev main also needs its size to be fixed
                        var prevId = _segmentMainList.Previous(curSegId);
                        if (prevId != default)
                        {
                            ref var prevHeader = ref SegmentHeaderAccessor(prevId);
                            prevHeader.MainList.Next = newSegId;
                            prevHeader.SegmentSize += (ushort)((newSegId - curSegId) << 4);
                        }

                        var nextId = _segmentMainList.Next(curSegId);
                        if (nextId != default)
                        {
                            ref var nextHeader = ref SegmentHeaderAccessor(nextId);
                            nextHeader.MainList.Previous = newSegId;
                        }

                        prevId = _segmentFreeList.Previous(curSegId);
                        if (prevId != default)
                        {
                            ref var prevHeader = ref SegmentHeaderAccessor(prevId);
                            prevHeader.FreeList.Next = newSegId;
                        }

                        nextId = _segmentFreeList.Next(curSegId);
                        if (nextId != default)
                        {
                            ref var nextHeader = ref SegmentHeaderAccessor(nextId);
                            nextHeader.FreeList.Previous = newSegId;
                        }
                        curSegId = newSegId;

                        // Move the header
                        var curHeader = SegmentHeaderAccessor(curSegId);
                        curHeader.SegmentSize -= (ushort)((newSegId - curSegId) << 4);     // Fix the size due to alignment
                        ref var newHeader = ref SegmentHeaderAccessor(newSegId);
                        newHeader = curHeader;
                    }
                }

                {
                    var prevFreeSegId = _segmentFreeList.Previous(curSegId);
                    ref var curSegHeader = ref SegmentHeaderAccessor(curSegId);
                    _segmentFreeList.Remove(curSegId);                              // Remove from FreeList, the seg becomes "occupied"

                    // This segment requires at least its size + the size of the header of the next segment and we need to pad everything to unsure the address of the next
                    //  segment will be aligned on at least 16 bytes.
                    var requiredSize = (size + sizeof(SegmentHeader)).Pad16();

                    // We can split only if the combined size of this segment and the minimum size of a segment fit
                    if (curSegHeader.SegmentSize >= (requiredSize + 16))
                    {
                        var remainingSize = curSegHeader.SegmentSize - requiredSize;
                        curSegHeader.SegmentSize = (ushort)(requiredSize - sizeof(SegmentHeader));

                        var remainingSegId = (ushort)(curSegId + (requiredSize >> 4));
                        ref var remainingSegmentHeader = ref SegmentHeaderAccessor(remainingSegId);
                        remainingSegmentHeader.SegmentSize = (ushort)remainingSize;
                        _segmentFreeList.Insert(prevFreeSegId, remainingSegId);
                        _segmentMainList.Insert(curSegId, remainingSegId);
                    }
                }

                return new MemorySegment((byte*)newSegAddress, size);
            }
            finally
            {
                _control.ReleaseControl();
            }
        }
    }

    /* TO DELETE
    private struct SimpleList<T> where T : struct
    {
        private const int DefaultCapacity = 16;

        private T[] _data;
        private int _count;

        public SimpleList(int capacity)
        {
            _data = new T[capacity];
            _count = 0;
        }

        public int Count => _count;

        public ref T this[int index]
        {
            get
            {
                Debug.Assert((uint)index < _count, $"The given index {index} is out of range, allowed values are [0-{_count-1}]");
                return ref _data[index];
            }
        }

        public ref T AddInPlace()
        {
            if (_count == _data.Length)
            {
                Grow(_count + 1);
            }

            return ref _data[_count++];
        }

        public int Add(ref T item)
        {
            var index = _count;
            ref var i = ref AddInPlace();
            i = item;

            return index;
        }
        public int Add(T item)
        {
            var index = _count;
            ref var i = ref AddInPlace();
            i = item;

            return index;
        }

        public int Capacity
        {
            get => _data.Length;
            set
            {
                if (value < _count)
                {
                    ThrowHelper.OutOfRange($"The specified capacity {value} is invalid.");
                }

                if (value != _data.Length)
                {
                    if (value > 0)
                    {
                        T[] newItems = new T[value];
                        if (_count > 0)
                        {
                            Array.Copy(_data, newItems, _count);
                        }
                        _data = newItems;
                    }
                    else
                    {
                        _data = Array.Empty<T>();
                    }
                }
            }
        }

        public Enumerator GetEnumerator() => new(this);

        public void Clear()
        {
            _count = 0;
        }

        private void Grow(int capacity)
        {
            Debug.Assert(_data.Length < capacity);

            var newCapacity = _data.Length == 0 ? DefaultCapacity : 2 * _data.Length;
            if ((uint)newCapacity > Array.MaxLength) newCapacity = Array.MaxLength;

            if (newCapacity < capacity) newCapacity = capacity;

            Capacity = newCapacity;
        }

        public ref struct Enumerator
        {
            private readonly T[] _data;
            private readonly int _count;
            private int _index;

            public Enumerator(SimpleList<T> owner)
            {
                _data = owner._data;
                _count = owner.Count;
                _index = -1;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
            public bool MoveNext()
            {
                int index = _index + 1;
                if (index < _count)
                {
                    _index = index;
                    return true;
                }

                return false;
            }

            public ref T Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
                get => ref _data[_index];
            }
        }
    }
    */

    #endregion

    static DefaultMemoryManager()
    {
        SBBlockInitialCount = Environment.ProcessorCount * 4;
    }

    private ExclusiveAccessControl _nativeBlockListAccess;

    private List<NativeBlockInfo> _nativeBlockList;

    private SBBlock[] _SBBlocks;

    private int _SBBlockIdCounter;

    // Note: seems like ThreadLocal doesn't release data allocated for a given thread when this one is destroyed.
    // Which means if the program create/destroy thousand of threads, this won't be good for us...
    private ThreadLocal<SBBlock> _assignedSBBlockId;

    public DefaultMemoryManager()
    {
        _nativeBlockList = new List<NativeBlockInfo>(16);

        var nbi = new NativeBlockInfo(BlockSize, SBBlockInitialCount);
        _nativeBlockList.Add(nbi);

        _SBBlocks = new SBBlock[SBBlockInitialCount];
        
        for (var i = 0; i < SBBlockInitialCount; i++)
        {
            _SBBlocks[i] = new SBBlock(nbi.GetBlockSegment(i));
        }

        _SBBlockIdCounter = -1;
        _assignedSBBlockId = new ThreadLocal<SBBlock>(() =>
        {
            var v = Interlocked.Increment(ref _SBBlockIdCounter);
            return _SBBlocks[v % _SBBlocks.Length];
        });
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public bool IsDisposed { get; }
    public int PinnedMemoryBlockSize { get; }

    public MemorySegment Allocate(int size)
    {
        if (size > MemorySegmentMaxSizeForSuperBlock)
        {
            // TODO Big alloc
            throw new NotImplementedException();
        }

        var block = _assignedSBBlockId.Value;
        return block.Allocate(size);
    }

    public unsafe MemorySegment<T> Allocate<T>(int size) where T : unmanaged
    {
        return Allocate(size * sizeof(T)).Cast<T>();
    }

    public bool Free(MemorySegment segment)
    {
        throw new NotImplementedException();
    }

    public bool Free<T>(MemorySegment<T> segment) where T : unmanaged
    {
        throw new NotImplementedException();
    }

    public void Clear()
    {
        throw new NotImplementedException();
    }
}