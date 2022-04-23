﻿using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tomate;

/// <summary>
/// Allow to allocate segments of memory
/// </summary>
/// <remarks>
/// <para>Thread safety: SINGLE-THREAD
/// Common practice is to store instances of it in a <see cref="ThreadLocal{T}"/>.
/// </para>
/// <para>
/// Usage:
/// The memory manager is designed to allocate big segments of memory that are part of bigger segments (called PinedMemoryBlock (PMB)).
/// The Memory Manager makes one .net memory allocation per PMB, the block is pinned: it has a fixed address and won't be considered for GC collection,
/// and will be stored in the dedicated GC area for pinned objects (POH).
/// At construction the user specifies the size of each PMB, which typically falls in the range of tens/hundreds of megabytes.
/// Then segments are allocated calling <see cref="Allocate"/>, each segment can't be bigger than the PMB.
/// Each segment has a fixed memory address and is aligned on a cache line (64 bytes).
/// A segment is supposed to be allocated to host a group of objects, doing one segment-one object is to avoid and is far from optimal. (see implementation)
/// Call <see cref="Free"/> to release a given segment.
/// To release the memory allocated by an instance, either call <see cref="Clear"/> or <see cref="Dispose"/> or to wait for the instance to be collected.
/// </para>
/// <para>
/// Implementation:
/// This class is certainly the one that uses the most reference instances, but it's fine, because its usage is typically a very low frequency, well should even be punctual.
/// There is a <see cref="List{T}"/> that references all PMB. Each PMB has its own <see cref="List{T}"/> that stores one segment per entry.
/// Segments are identified by their address, the PMB list and block lists are keep their entry sorted by address to allow binary search.
/// </para>
/// </remarks>
public unsafe class MemoryManager : IDisposable, IMemoryManager
{
    /// <summary>
    /// Will be incremented every time a new Pinned Memory Block is allocated
    /// </summary>
    public int AllocationPinnedMemoryBlockEpoch { get; private set; }

    /// <summary>
    /// Will be incremented every time a new memory segment is allocated
    /// </summary>
    public int MemorySegmentAllocationEpoch { get; private set; }

    /// <summary>
    /// Construct an instance of the memory manager
    /// </summary>
    /// <param name="pinnedMemoryBlockSize">The size to allocate for each Pinned Memory Block</param>
    /// <remarks>
    /// A Pinned Memory Block should be big, you should think of it as the third level of storage.
    /// Level 1 being the object, which is typically grouped in Memory Segments, the level 2.
    /// You wont be able to allocate a Memory Segment bigger than a Pinned Memory Block.
    /// </remarks>
    public MemoryManager(int pinnedMemoryBlockSize)
    {
        _pinnedMemoryBlockSize = pinnedMemoryBlockSize;
        _pinnedMemoryBlocks = new List<PinnedMemoryBlock>(16);
    }

    /// <summary>
    /// Check if the instance is disposed or not.
    /// </summary>
    public bool IsDisposed => _pinnedMemoryBlocks == null;

    /// <summary>
    /// Dispose the instance, free the allocated memory.
    /// </summary>
    public void Dispose()
    {
        foreach (var segment in _pinnedMemoryBlocks!)
        {
            segment.Dispose();
        }

        _pinnedMemoryBlocks = null;
    }

    /// <summary>
    /// Allocate a Memory Segment
    /// </summary>
    /// <param name="size">Length of the segment to allocate.</param>
    /// <returns>The segment or an exception will be fired if we couldn't allocate one.</returns>
    /// <exception cref="ObjectDisposedException">Can't allocate because the object is disposed.</exception>
    /// <exception cref="OutOfMemoryException">The requested size is too big.</exception>
    /// <remarks>
    /// The segment's address will always be aligned on 64 bytes, its size will also be padded on 64 bytes.
    /// The segment's address is fixed, you can store it with the lifetime that suits you, it doesn't matter as the segment is part of a
    /// Pinned Memory Block that is a pinned allocation (using <see cref="GC.AllocateUninitializedArray{T}"/> with pinned set to true).
    /// </remarks>
    public MemorySegment Allocate(int size)
    {
        if (_pinnedMemoryBlocks == null)
        {
            ThrowHelper.ObjectDisposed(null, "Memory Manager is disposed, can't allocate a new block");
        }

        for (var i = 0; i < _pinnedMemoryBlocks.Count; i++)
        {
            if (_pinnedMemoryBlocks[i].AllocateSegment(size, out var res))
            {
                ++AllocationPinnedMemoryBlockEpoch;
                return new MemorySegment(res, size);
            }
        }

        var memorySegment = new PinnedMemoryBlock(_pinnedMemoryBlockSize);
        if (memorySegment.BiggestFreeSegment < size)
        {
            ThrowHelper.OutOfMemory($"The requested size ({size}) is too big to be allocated into a single block.");
        }

        _pinnedMemoryBlocks.Add(memorySegment);
        ++MemorySegmentAllocationEpoch;
        UpdatePinnedMemoryBlockAddressMap();

        return Allocate(size);
    }

    /// <summary>
    /// Free a previously allocated segment
    /// </summary>
    /// <param name="segment">The memory segment to free</param>
    /// <returns><c>true</c> if the segment was successfully released, <c>false</c> otherwise.</returns>
    /// <exception cref="ObjectDisposedException">Can't free if the instance is disposed, all segments have been released anyway.</exception>
    /// <remarks>
    /// This method won't prevent you against multiple free attempts on the same segment. If no other segment has been allocated with the same address, then it will return <c>false</c>.
    /// But if you allocated another segment which turns out to have the same address and call <see cref="Free"/> a second time, then it will free the second segment successfully.
    /// </remarks>
    public bool Free(MemorySegment segment)
    {
        if (_pinnedMemoryBlocks == null)
        {
            ThrowHelper.ObjectDisposed(null, "Memory Manager is disposed, can't free a block");
        }

        var address = segment.Address;
        if (address == null) return false;

        for (var i = 0; i < _pinnedMemoryBlocks.Count; i++)
        {
            if (address >= _pinnedMemoryBlocks[i].SegmentAddress)
            {
                return _pinnedMemoryBlocks[i].FreeBlock(address);
            }
        }

        return false;
    }

    /// <summary>
    /// Release all the allocated segments, free the memory allocated through .net.
    /// </summary>
    public void Clear()
    {
        foreach (var segment in _pinnedMemoryBlocks!)
        {
            segment.Clear();
        }
        _pinnedMemoryBlocks.Clear();
    }

    private void UpdatePinnedMemoryBlockAddressMap() => _pinnedMemoryBlocks!.Sort((x, y) => (int)((long)y.SegmentAddress - (long)x.SegmentAddress));

    private readonly int _pinnedMemoryBlockSize;
    private List<PinnedMemoryBlock> _pinnedMemoryBlocks;

    [DebuggerDisplay("Address: {SegmentAddress}, Biggest Free Segment: {BiggestFreeSegment}, Total Free: {TotalFree}")]
    internal class PinnedMemoryBlock : IDisposable
    {
        public PinnedMemoryBlock(int size)
        {
            _data = GC.AllocateUninitializedArray<byte>(size + 63, true);
            var baseAddress = (byte*)Marshal.UnsafeAddrOfPinnedArrayElement(_data, 0).ToPointer();

            _segments = new List<Segment>(256);

            var offsetToAlign64 = (int)((((long)baseAddress + 63L) & -64L) - (long)baseAddress);
            _alignedAddress = baseAddress + offsetToAlign64;
            _segments.Add(new Segment(0, size - offsetToAlign64, true));
        }

        public void Dispose()
        {
            _data = null;
            _segments.Clear();
        }

        public bool AllocateSegment(int size, out byte* address)
        {
            size = (size + 63) & -64;

            for (var i = 0; i < _segments.Count; i++)
            {
                var segment = _segments[i];
                var length = segment.Length;
                if (segment.IsFree == false || size > length) continue;

                // Update the current segment with its new size and status
                segment.IsFree = false;
                segment.Length = size;
                _segments[i] = segment;

                // Check if the segment was bigger than the requested size, allocate another one with the remaining space
                var remainingLength = length - size;
                if (remainingLength > 0)
                {
                    var ns = new Segment(segment.Offset + size, remainingLength, true);
                    _segments.Insert(i + 1, ns);
                }

                address = _alignedAddress + segment.Offset;
                return true;
            }

            address = default;
            return false;
        }

        public void Clear()
        {
            _data = null;
            _segments.Clear();
        }

        private class SegmentComparer : IComparer<Segment>
        {
            public int Compare(Segment x, Segment y)
            {
                return x.Offset - y.Offset;
            }
        }

        public bool FreeBlock(byte* address)
        {
            var index = _segments.BinarySearch(new Segment((int)(address - _alignedAddress), 0, false), SegmentComparerInstance);
            if (index < 0)
            {
                return false;
            }

            var curSegment = _segments[index];
            if (curSegment.IsFree)
            {
                return false;
            }

            curSegment.IsFree = true;

            // Merge with adjacent segments if they are free
            var nextSegmentIndex = index + 1;
            if ((nextSegmentIndex < _segments.Count) && _segments[nextSegmentIndex].IsFree)
            {
                curSegment.Length += _segments[nextSegmentIndex].Length;
                _segments.RemoveAt(nextSegmentIndex);
            }

            // Merge with free segments before
            var prevSegmentIndex = index - 1;
            if ((prevSegmentIndex >= 0) && _segments[prevSegmentIndex].IsFree)
            {
                curSegment.Offset = _segments[prevSegmentIndex].Offset;
                curSegment.Length += _segments[prevSegmentIndex].Length;
                _segments.RemoveAt(index);
                --index;
            }

            _segments[index] = curSegment;

            return true;
        }

        public byte* SegmentAddress => _alignedAddress;

        public int BiggestFreeSegment
        {
            get
            {
                var b = 0;
                foreach (var t in _segments)
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
                foreach (var t in _segments)
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
        private readonly List<Segment> _segments;
        private static readonly SegmentComparer SegmentComparerInstance = new SegmentComparer();

        [DebuggerDisplay("Offset: {Offset}, Length: {Length}, IsFree: {IsFree}")]
        struct Segment
        {
            public Segment(int offset, int length, bool isFree)
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