using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Tomate;

[PublicAPI]
public unsafe struct MappedConcurrentChunkBasedCircularBuffer
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public long WriteOffset;
        private readonly int _padding0;
        public int WriteCounter;

        public long ReadOffset;
        private readonly int _padding1;
        public int ReadCounter;
    }

    private readonly Header* _header;
    private readonly byte* _dataStart;
    private readonly byte* _dataEnd;
    private readonly int _bufferSize;
    private int _sizeToNext;

    public static MappedConcurrentChunkBasedCircularBuffer Create(MemorySegment memorySegment) => new(memorySegment, true);
    public static MappedConcurrentChunkBasedCircularBuffer Map(MemorySegment memorySegment) => new(memorySegment, false);

    private MappedConcurrentChunkBasedCircularBuffer(MemorySegment segment, bool create)
    {
        _header = segment.Cast<Header>().Address;
        _dataStart = (byte*)(_header + 1);
        _dataEnd = segment.End;
        _bufferSize = (int)(_dataEnd - _dataStart);
        _sizeToNext = 0;
        TotalWaitedCount = 0;

        if (create)
        {
            _header->ReadOffset = 0;
            _header->WriteOffset = 0;
            _header->ReadCounter = 0;
            _header->WriteCounter = 0;
        }
    }

    public double Occupancy => (_header->WriteOffset - _header->ReadOffset) / (double)_bufferSize;

    /// For analysis purpose, gives the total number of times this instance SpinWait due to being full.
    public int TotalWaitedCount { get; private set; }

    public MemorySegment<T> ConcurrentReserve<T>(short chunkid, bool waitIfFull) where T : unmanaged =>
        ConcurrentReserve(chunkid, (short)sizeof(T), waitIfFull).Cast<T>();

    public MemorySegment ConcurrentReserve(short chunkId, short size, bool waitIfFull)
    {
        var bufferSize = _bufferSize;
        var totalSize = size + 4;
        Debug.Assert(totalSize > 0);

        while (true)
        {
            var isBufferFull = false;

            // Check if the segment can't be reserved because it's overlapping the read
            var curWrite = _header->WriteOffset;
            var curRead = _header->ReadOffset;

            var curReadOff = curRead % bufferSize;
            var curWriteOff = (curWrite % bufferSize);
            var curWriteEndOff = ((curWrite + totalSize) % bufferSize);

            if (curWriteOff > curReadOff)
            {
                if (curWriteEndOff > curReadOff && curWriteEndOff < curWriteOff) isBufferFull = true;
            }
            else if (curWriteOff < curReadOff)
            {
                if (curWriteEndOff > curReadOff || curWriteEndOff < curWriteOff) isBufferFull = true;
            }
            // Equal
            else
            {
                if (curWrite != curRead) isBufferFull = true;
            }

            if (isBufferFull == false)
            {
                break;
            }

            ++TotalWaitedCount;
            Thread.SpinWait(0);
        }

        var newOffset = Interlocked.Add(ref Unsafe.AsRef<long>(&_header->WriteOffset), totalSize);

        var start = (newOffset - totalSize) % bufferSize;
        var end = newOffset % bufferSize;
        Debug.Assert(start >= 0);
        Debug.Assert(end <= bufferSize);

        // Check if the whole segment is wrapping around the circular buffer
        if (end != 0 && end < start)
        {
            if (start + 4 > bufferSize)
            {
                Interlocked.Add(ref Unsafe.AsRef<long>(&_header->WriteOffset), bufferSize - start);
            }
            else
            {
                // Insert padding and recurse
                var chunk = (short*)(_dataStart + start);
                chunk[0] = -1;
                chunk[1] = size;
            }

            return ConcurrentReserve(chunkId, size, waitIfFull);
        }
        else
        {
            var chunk = (short*)(_dataStart + start);
            chunk[0] = chunkId;
            chunk[1] = size;

            return new MemorySegment((byte*)(chunk + 2), size);
        }
    }

    public void ConcurrentReserveNext() => Interlocked.Increment(ref Unsafe.AsRef<int>(&_header->WriteCounter));

    public MemorySegment SingleThreadedPeek(out short chunkId)
    {
        var bufferSize = _bufferSize;

        // No data to read
        var read = _header->ReadCounter;
        var write = _header->WriteCounter;

        Debug.Assert(read <= write,
            "It appears that SingleThreadedPeek/Next was incorrectly called. There must be a SingledThreadedNext after each successful SingleThreadedPeek");

        if (read == write)
        {
            chunkId = -1;
            return MemorySegment.Empty;
        }

        var curRead = _header->ReadOffset;

        var startOffset = curRead % bufferSize;
        // Check if we are near the end of the buffer and there's not at least the size to store a chunk header, if it's the case we loop to the beginning of the buffer
        if (startOffset + 4 > bufferSize)
        {
            _header->ReadOffset += bufferSize - startOffset;
            startOffset = 0;
        }

        var chunk = (short*)(_dataStart + startOffset);
        chunkId = chunk[0];
        var segmentSize = chunk[1];
        var addr = (byte*)(chunk + 2);
        _sizeToNext = segmentSize + 4;

        // If it's a padding segment, skip it
        if (chunkId == -1)
        {
            SingleThreadedNext();
            return SingleThreadedPeek(out chunkId);
        }

        return new MemorySegment(addr, segmentSize);
    }

    public void SingleThreadedNext()
    {
        Debug.Assert(_sizeToNext > 0, "You must call SingleThreadedNext after a successful SingleThreadedPeek");
        _header->ReadOffset += _sizeToNext;
        ++_header->ReadCounter;
        _sizeToNext = 0;
    }

}
