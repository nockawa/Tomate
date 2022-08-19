using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tomate;

/// <summary>
/// Concurrent First-In, First-Out queue to store variable-length chunks
/// </summary>
/// <remarks>
/// This type allows to enqueue and dequeue chunks of data. Each chunk has a type (from 1 to 16383) and a size (up to 32767).
/// Multiple threads can enqueue/dequeue concurrently, everything is thread safe.
/// </remarks>
public unsafe struct ConcurrentChunkQueue
{
    /// <summary>
    /// For debug/info purpose only, number of time we iterate the wait loop
    /// </summary>
    public int TotalWaitedCount { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public long WriteOffset;
        private readonly long _padding0;

        public long ReadOffset;
        private readonly long _padding1;
    }

    private readonly Header* _header;
    private readonly byte* _dataStart;
    private readonly int _bufferSize;

    public double Occupancy => (_header->WriteOffset - _header->ReadOffset) / (double)_bufferSize;

    /// <summary>
    /// Create a new Queue using the given memory segment to store its data
    /// </summary>
    /// <param name="memorySegment">The memory segment to use to store the queue data</param>
    /// <returns>The queue instance</returns>
    public static ConcurrentChunkQueue Create(MemorySegment memorySegment) => new(memorySegment, true);

    /// <summary>
    /// Create a new instance over an existing queue
    /// </summary>
    /// <param name="memorySegment">The memory segment storing the queue</param>
    /// <returns>The instance</returns>
    public static ConcurrentChunkQueue Map(MemorySegment memorySegment) => new(memorySegment, false);

    private ConcurrentChunkQueue(MemorySegment segment, bool create)
    {
        _header = segment.Cast<Header>().Address;
        _dataStart = (byte*)(_header + 1);
        var dataEnd = segment.End - 4;        // - 4 is the size of the chunk header, we need to be able to overflow at least a header size. Well header size -1 in fact
        _bufferSize = (int)(dataEnd - _dataStart);

        if (create)
        {
            segment.ToSpan<byte>().Clear();
            _header->ReadOffset = 0;
            _header->WriteOffset = 0;
        }

        TotalWaitedCount = 0;
    }

    /// <summary>
    /// Enqueue handle used to set the queued chunk's data
    /// </summary>
    /// <typeparam name="T">The type of data to store</typeparam>
    /// <remarks>
    /// DON'T FORGET TO CALL <see cref="Dispose"/> when you've stored the data, otherwise the queue will likely fails at some point to enqueue more items
    /// </remarks>
    public readonly struct EnqueueHandle<T> : IDisposable where T : unmanaged
    {
        private readonly MemorySegment<T> _memorySegment;

        internal EnqueueHandle(MemorySegment<T> memorySegment)
        {
            _memorySegment = memorySegment;
        }

        /// <summary>
        /// Dispose the handle, make the item ready for dequeue
        /// </summary>
        public void Dispose()
        {
            if (_memorySegment.IsDefault == false)
            {
                var header = (ushort*)_memorySegment.Address - 2;
                header[0] |= 0x4000;
            }
        }

        /// <summary>
        /// The memory segment spanning the chunk's data
        /// </summary>
        public MemorySegment<T> MemorySegment => _memorySegment;
        
        /// <summary>
        /// First item of the chunk
        /// </summary>
        public ref T Value => ref _memorySegment.AsRef();

        /// <summary>
        /// Will be <c>true</c> if the chunk failed to be enqueued
        /// </summary>
        public bool IsDefault => _memorySegment.IsDefault;

        /// <summary>
        /// Indexer accessor to the chunk's data
        /// </summary>
        public ref T this[int i] => ref _memorySegment[i];
    }

    /// <summary>
    /// Dequeue handle used to access the dequeued chunk's type and data
    /// </summary>
    public readonly struct DequeueHandle : IDisposable
    {
        private readonly MemorySegment _memorySegment;
        private readonly long _newRead;
        private readonly long* _readOffsetAddr;
        
        /// <summary>
        /// Chunk's id
        /// </summary>
        public ushort ChunkId { get; }

        /// <summary>
        /// If there was no chunk to dequeue, will be <c>true</c>
        /// </summary>
        public bool IsDefault => ChunkId == 0;

        /// <summary>
        /// Memory Segment spanning the chunk's data
        /// </summary>
        public MemorySegment MemorySegment => _memorySegment;

        internal DequeueHandle(ushort chunkId, MemorySegment memorySegment, long newRead, long* readOffsetAddr)
        {
            _memorySegment = memorySegment;
            _newRead = newRead;
            _readOffsetAddr = readOffsetAddr;
            ChunkId = chunkId;
        }

        /// <summary>
        /// Dispose the handle, make the chunk no longer accessible
        /// </summary>
        public void Dispose()
        {
            if (_memorySegment.IsDefault == false)
            {
                var header = (ushort*)_memorySegment.Address - 2;
                header[0] &= 0x3FFF;

                *_readOffsetAddr = _newRead;
            }
        }
    }
    
    /// <summary>
    /// Enqueue a new chunk
    /// </summary>
    /// <typeparam name="T">The chunk's data type</typeparam>
    /// <param name="chunkId">Id of the chunk, must be within the range 1-16383</param>
    /// <param name="size">Size of the chunk in {T}. The size*sizeof(T) must be less than 65536, that is: the chunk's data length (in bytes) is stored in a ushort.</param>
    /// <param name="waitTime">If <c>null</c> the operation will wait until there's enough space to enqueue the chunk.
    /// Otherwise and empty handle will be return if the chunk couldn't be enqueued in the given time.
    /// </param>
    /// <returns>The handle to set the chunk's data if it was successfully enqueued, or an empty handle</returns>
    /// <remarks>
    /// Don't forget to call <see cref="EnqueueHandle{T}.Dispose"/> on the handle when all the chunk's data has been set.
    /// 
    /// </remarks>
    public EnqueueHandle<T> Enqueue<T>(ushort chunkId, ushort size=1, TimeSpan? waitTime = null) where T : unmanaged
    {
        var bufferSize = _bufferSize;
        var byteSize = size * sizeof(T);
        var totalSize = byteSize + 4;
        Debug.Assert(chunkId <= 0x3FFF);
        Debug.Assert(byteSize <= ushort.MaxValue, $"The maximum allowed size is 65536 bytes, '{size}*{sizeof(T)}' is more ({byteSize})");

        long newOffset = -1;
        
        // If there's no timeout we can reserve the chunk's data area immediately, the whole path is simpler than with a timeout
        if (waitTime == null)
        {
            newOffset = Interlocked.Add(ref Unsafe.AsRef<long>(&_header->WriteOffset), totalSize);
            while (true)
            {
                // Check if the segment can't be reserved because it's overlapping the read
                var curRead = _header->ReadOffset;

                if (newOffset <= (curRead + bufferSize))
                {
                    break;
                }

                ++TotalWaitedCount;
                Thread.SpinWait(0);
            }
        }
        else
        {
            // With a timeout we need to check if we can store the chunk at the current location, but actually reserve it if there's enough space.
            // But another may have beaten us and taken this spot, so we rely on a CompareExchange to reserve and loop if we were beaten.
            var waitUntil = DateTime.UtcNow + waitTime.Value;
            while (DateTime.UtcNow < waitUntil)
            {
                var writeOffset = _header->WriteOffset;
                newOffset = writeOffset + totalSize;

                var isBufferFull = true;
                while (DateTime.UtcNow < waitUntil)
                {
                    // Check if the segment can't be reserved because it's overlapping the read
                    var curRead = _header->ReadOffset;

                    if (newOffset <= (curRead + bufferSize))
                    {
                        isBufferFull = false;
                        break;
                    }

                    ++TotalWaitedCount;
                    Thread.SpinWait(0);
                }

                if (isBufferFull)
                {
                    return default;
                }

                if (Interlocked.CompareExchange(ref Unsafe.AsRef<long>(&_header->WriteOffset), newOffset, writeOffset) == writeOffset)
                {
                    break;
                }

                newOffset = -1;
            }
        }

        if (newOffset == -1)
        {
            return default;
        }

        var start = (newOffset - totalSize) % bufferSize;
        var end = newOffset % bufferSize;
        Debug.Assert(start >= 0);
        Debug.Assert(end <= bufferSize);

        // Check if the whole segment is wrapping around the circular buffer
        if (end != 0 && end < start)
        {
            // Insert a padding chunk because the logical data area is not physically linear and recurse to take the next spot
            var chunk = (ushort*)(_dataStart + start);
            chunk[0] = 0x7FFF;
            chunk[1] = (ushort)byteSize;

            return Enqueue<T>(chunkId, size, waitTime);
        }
        else
        {
            var chunk = (ushort*)(_dataStart + start);
            chunk[0] = chunkId;
            chunk[1] = (ushort)byteSize;

            return new EnqueueHandle<T>(new MemorySegment<T>((byte*)(chunk + 2), size));
        }
    }

    /// <summary>
    /// Try to dequeue a chunk
    /// </summary>
    /// <returns>An empty handle if there was no chunk to dequeue or a valid one if there was one.</returns>
    /// <remarks>Don't forget to call <see cref="DequeueHandle.Dispose"/> on the handle when the chunk is no longer needed.</remarks>
    public DequeueHandle TryDequeue()
    {
        var bufferSize = _bufferSize;

        // No data to read
        var readOffset = _header->ReadOffset;
        var writeOffset = _header->WriteOffset;

        if (readOffset >= writeOffset)
        {
            return default;
        }

        var startOffset = readOffset % bufferSize;

        var chunk = (ushort*)(_dataStart + startOffset);
        var chunkId = chunk[0];
        
        if (chunkId is < 0x4000 or >= 0x8000) 
        {
            return default;       // The chunk is not ready if the 15th bit is not set or already taken if 16th bit is set
        }

        var segmentSize = chunk[1];
        uint value = default;
        value.Pack(segmentSize, (ushort)(chunkId | 0x8000));
        uint comp = default;
        comp.Pack(segmentSize, chunkId);

        var newRead = readOffset + segmentSize + 4;

        // Try to acquire this chunk
        if (Interlocked.CompareExchange(ref Unsafe.AsRef<uint>(chunk), value, comp) != comp)
        {
            return TryDequeue();
        }

        var h = new DequeueHandle((ushort)(chunkId & 0x3FFF), new MemorySegment((byte*)(chunk + 2), segmentSize), newRead, &_header->ReadOffset);
        if ((h.ChunkId & 0x3FFF) == 0x3FFF)
        {
            h.Dispose();
            return TryDequeue();
        }

        return h;
    }
}