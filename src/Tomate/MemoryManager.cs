using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Tomate;

/// <summary>
/// Allow to allocate blocks of memory
/// </summary>
/// <remarks>
/// <para>Thread safety: SINGLE-THREAD
/// Common practice is to store instances of it in a <see cref="ThreadLocal{T}"/>.
/// </para>
/// <para>
/// Usage:
/// The memory manager is designed to allocate big blocks of memory that are part of bigger blocks, called segment.
/// The Memory Manager make one memory allocation per segment, the segment is pinned: it has a fixed address and won't be considered for GC collection.
/// At construction the user specifies the size of a segment, which typically falls in the range of tens/hundreds of megabytes.
/// Then blocks are allocated calling <see cref="AllocateBlock"/>, each block must can't be bigger than the segment.
/// Each block has a fixed memory address and is aligned on a cache line (64 bytes).
/// A block is supposed to be allocated to host a group of objects, doing one block-one object is to avoid and is far from optimal. (see implementation)
/// Call <see cref="FreeBlock"/> to release a given block.
/// To release the memory allocated by an instance, either call <see cref="Clear"/> or <see cref="Dispose"/> or to wait for the instance to be collected.
/// </para>
/// <para>
/// Implementation:
/// This class is certainly the one that uses the most reference instances, but it's fine, because its usage is typically a very low frequency, well should even be punctual.
/// There is a <see cref="List{T}"/> that references all segments. Each segment has its own <see cref="List{T}"/> that stores one block per entry.
/// Blocks are identified by their address, the segment list and block lists are keep their entry sorted by address to allow binary search.
/// </para>
/// </remarks>
public unsafe class MemoryManager : IDisposable
{
    /// <summary>
    /// Will be incremented every time a new block is allocated
    /// </summary>
    public int AllocationBlockEpoch { get; private set; }

    /// <summary>
    /// Will be incremented every time a new memory segment is allocated
    /// </summary>
    public int MemorySegmentAllocationEpoch { get; private set; }

    /// <summary>
    /// Construct an instance of the memory manager
    /// </summary>
    /// <param name="segmentSize">The size to allocate for each Memory Segment</param>
    /// <remarks>
    /// A Memory Segment should be big, you should think of it as the third level of storage. Level 1 being the object, which is typically grouped in Memory Blocks, the level 2.
    /// You wont be able to allocate a Memory Block bigger than a Memory Segment.
    /// </remarks>
    public MemoryManager(int segmentSize)
    {
        _segmentSize = segmentSize;
        _segments = new List<MemorySegment>(16);
    }

    /// <summary>
    /// Check if the instance is disposed or not.
    /// </summary>
    public bool IsDisposed => _segments == null;

    /// <summary>
    /// Dispose the instance, free the allocated memory.
    /// </summary>
    public void Dispose()
    {
        foreach (var segment in _segments!)
        {
            segment.Dispose();
        }

        _segments = null;
    }

    /// <summary>
    /// Allocate a Memory Block
    /// </summary>
    /// <param name="size">Size of the block to allocate.</param>
    /// <returns>The address of the block or an exception will be fired if we couldn't allocate one.</returns>
    /// <exception cref="ObjectDisposedException">Can't allocate because the object is disposed.</exception>
    /// <exception cref="OutOfMemoryException">The requested size is too big.</exception>
    /// <remarks>
    /// The returned address will always be aligned on 64 bytes, so the block's size will also be padded on 64 bytes.
    /// The block's address is fixed, you can store it with the lifetime that suits you, it doesn't matter as the block is part of a Memory Segment that is a pinned allocation
    /// (using <see cref="GC.AllocateUninitializedArray{T}"/> with pinned set to true).
    /// </remarks>
    public byte* AllocateBlock(int size)
    {
        if (_segments == null)
        {
            throw new ObjectDisposedException("Memory Manager is disposed, can't allocate a new block");
        }

        for (var i = 0; i < _segments.Count; i++)
        {
            if (_segments[i].AllocateBlock(size, out var res))
            {
                ++AllocationBlockEpoch;
                return res;
            }
        }

        var memorySegment = new MemorySegment(_segmentSize);
        if (memorySegment.BiggestFreeBlock < size)
        {
            throw new OutOfMemoryException($"The requested size ({size}) is too big to be allocated into a single block.");
        }

        _segments.Add(memorySegment);
        ++MemorySegmentAllocationEpoch;
        UpdateSegmentAddressMap();

        return AllocateBlock(size);
    }

    /// <summary>
    /// Free a previously allocated block
    /// </summary>
    /// <param name="blockAddress">The address of the block to free</param>
    /// <returns><c>true</c> if the block was successfully released, <c>false</c> otherwise.</returns>
    /// <exception cref="ObjectDisposedException">Can't free if the instance is disposed, all blocks have been released anyway.</exception>
    /// <remarks>
    /// This method won't prevent you against multiple free attempts on the same block. If no other block has been allocated with the same address, then it will return <c>false</c>.
    /// But if you allocated another block which turns out to have the same address and call <see cref="FreeBlock"/> a second time, then it will free the second block successfully.
    /// </remarks>
    public bool FreeBlock(byte* blockAddress)
    {
        if (_segments == null)
        {
            throw new ObjectDisposedException("Memory Manager is disposed, can't free a block");
        }

        if (blockAddress == null) return false;

        for (var i = 0; i < _segments.Count; i++)
        {
            if (blockAddress >= _segments[i].SegmentAddress)
            {
                return _segments[i].FreeBlock(blockAddress);
            }
        }

        return false;
    }

    /// <summary>
    /// Release all the allocated blocks, free the memory allocated through .net.
    /// </summary>
    public void Clear()
    {
        foreach (var segment in _segments!)
        {
            segment.Clear();
        }
        _segments.Clear();
    }

    private void UpdateSegmentAddressMap() => _segments!.Sort((x, y) => (int)((long)y.SegmentAddress - (long)x.SegmentAddress));

    private readonly int _segmentSize;
    private List<MemorySegment> _segments;

    [DebuggerDisplay("Address: {SegmentAddress}, Biggest Free Block: {BiggestFreeBlock}, Total Free: {TotalFree}")]
    internal class MemorySegment : IDisposable
    {
        public MemorySegment(int size)
        {
            _data = GC.AllocateUninitializedArray<byte>(size + 63, true);
            var baseAddress = (byte*)Marshal.UnsafeAddrOfPinnedArrayElement(_data, 0).ToPointer();

            _blocks = new List<Block>(256);

            var offsetToAlign64 = (int)((((long)baseAddress + 63L) & -64L) - (long)baseAddress);
            _alignedAddress = baseAddress + offsetToAlign64;
            _blocks.Add(new Block(0, size - offsetToAlign64, true));
        }

        public void Dispose()
        {
            _data = null;
            _blocks.Clear();
        }

        public bool AllocateBlock(int size, out byte* address)
        {
            size = (size + 63) & -64;

            for (var i = 0; i < _blocks.Count; i++)
            {
                var block = _blocks[i];
                var length = block.Length;
                if (block.IsFree == false || size > length) continue;

                // Update the current segment with its new size and status
                block.IsFree = false;
                block.Length = size;
                _blocks[i] = block;

                // Check if the segment was bigger than the requested size, allocate another one with the remaining space
                var remainingLength = length - size;
                if (remainingLength > 0)
                {
                    var ns = new Block(block.Offset + size, remainingLength, true);
                    _blocks.Insert(i + 1, ns);
                }

                address = _alignedAddress + block.Offset;
                return true;
            }

            address = default;
            return false;
        }

        public void Clear()
        {
            _data = null;
            _blocks.Clear();
        }

        private class BlockComparer : IComparer<Block>
        {
            public int Compare(Block x, Block y)
            {
                return x.Offset - y.Offset;
            }
        }

        public bool FreeBlock(byte* address)
        {
            var index = _blocks.BinarySearch(new Block((int)(address - _alignedAddress), 0, false), BlockComparerInstance);
            if (index < 0)
            {
                return false;
            }

            var curBlock = _blocks[index];
            if (curBlock.IsFree)
            {
                return false;
            }

            curBlock.IsFree = true;

            // Merge with adjacent blocks if they are free
            var nextBlockIndex = index + 1;
            if ((nextBlockIndex < _blocks.Count) && _blocks[nextBlockIndex].IsFree)
            {
                curBlock.Length += _blocks[nextBlockIndex].Length;
                _blocks.RemoveAt(nextBlockIndex);
            }

            // Merge with free blocks before
            var prevBlockIndex = index - 1;
            if ((prevBlockIndex >= 0) && _blocks[prevBlockIndex].IsFree)
            {
                curBlock.Offset = _blocks[prevBlockIndex].Offset;
                curBlock.Length += _blocks[prevBlockIndex].Length;
                _blocks.RemoveAt(index);
                --index;
            }

            _blocks[index] = curBlock;

            return true;
        }

        public byte* SegmentAddress => _alignedAddress;

        public int BiggestFreeBlock
        {
            get
            {
                var b = 0;
                foreach (var t in _blocks)
                {
                    if (t.IsFree && t.Length > b)
                    {
                        b = t.Length;
                    }
                }

                return b;
            }
        }

        public int TotalFree
        {
            get
            {
                var f = 0;
                foreach (var t in _blocks)
                {
                    if (t.IsFree)
                    {
                        f += t.Length;
                    }
                }

                return f;
            }
        }

        // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
        private byte[] _data;
        private readonly byte* _alignedAddress;
        private readonly List<Block> _blocks;
        private static readonly BlockComparer BlockComparerInstance = new BlockComparer();

        [DebuggerDisplay("Offset: {Offset}, Length: {Length}, IsFree: {IsFree}")]
        struct Block
        {
            public Block(int offset, int length, bool isFree)
            {
                Offset = offset;
                _length = (length << 1) | (isFree ? 1 : 0);
            }
            public int Offset;

            public int Length
            {
                get => _length >> 1;
                set => _length = (value << 1) | (IsFree ? 1 : 0);
            }

            public bool IsFree
            {
                get => (_length & 1) != 0;
                set => _length = (Length << 1) | (value ? 1 : 0);
            }

            private int _length;
        }
    }
}