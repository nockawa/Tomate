using System.Diagnostics;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Tomate;

[PublicAPI]
public unsafe struct MappedConcurrentChunkBasedQueue
{
    #region Constants

    private const ushort ChunkAcquiredForDequeue   = 0x8000;
    private const ushort ChunkDequeuedAndProcessed = 0x2000;
    private const ushort ChunkIdMask               = 0x1FFF;
    private const ushort ChunkReadyForDequeue      = 0x4000;
    public const ushort MaxChunkId                 = 0x1FFE;
    private const ushort PaddingChunkId            = 0x1FFF;

    #endregion

    #region Public APIs

    #region Properties

    public int BufferSize => _bufferSize;

    public long ReadOffset => _header[0].StartReadOffset;
    public int RelativeReadOffset => (int)(_header[0].StartReadOffset % _bufferSize);
    public int RelativeWriteOffset => (int)(_header[0].CurrentWriteOffset % _bufferSize);
    public long WriteOffset => _header[0].CurrentWriteOffset;

    #endregion

    #region Methods

    public static MappedConcurrentChunkBasedQueue Create(MemorySegment memorySegment) => new(memorySegment, true);

    /// <summary>
    /// Create a new instance over an existing queue
    /// </summary>
    /// <param name="memorySegment">The memory segment storing the queue</param>
    /// <returns>The instance</returns>
    public static MappedConcurrentChunkBasedQueue Map(MemorySegment memorySegment) => new(memorySegment, false);

    public EnqueueHandle<T> Enqueue<T>(ushort chunkId, ushort size = 1, TimeSpan? waitTime = null, CancellationToken token = default) where T : unmanaged
    {
        var bufferSize = _bufferSize;
        var chunkDataSize = size * sizeof(T);
        var chunkTotalSize = chunkDataSize + 4;
        ref var header = ref _header[0];

        Debug.Assert(chunkId is > 0 and <= MaxChunkId, $"Chunk ID {chunkId:X} is too big or 0, max is {MaxChunkId:X}");
        Debug.Assert(chunkTotalSize > 0);
        Debug.Assert(chunkTotalSize < (bufferSize / 2), $"Can't queue this chunk, it's too big, you should create the queue with a bigger buffer. {chunkTotalSize} > {bufferSize / 2}");

        var waitUntil = waitTime!=null ? (DateTime.UtcNow + waitTime.Value) : DateTime.MaxValue;
        long endWrite = -1;
        
        while (DateTime.UtcNow < waitUntil && token.IsCancellationRequested == false)
        {
            // The goal is to acquire a segment starting at writeOffset, but another thread could beat us at taking it, so to make sure it's not the case
            //  we rely on a CAS operation
            var writeOffset = header.CurrentWriteOffset;
            endWrite = writeOffset + chunkTotalSize;

            // Loop and wait until the segment is not overlapping the read segment, wait for the given time or until the cancellation token is raised
            var isBufferFull = true;
            while (DateTime.UtcNow < waitUntil && token.IsCancellationRequested == false)
            {
                // Detect if the segment we try to acquire is overlapping the read segment
                if ((endWrite - header.StartReadOffset) <= bufferSize)
                {
                    isBufferFull = false;
                    break;
                }
                Thread.SpinWait(0);
            }

            // Check if we escaped the loop because the buffer is full or the token was raised
            if (token.IsCancellationRequested || isBufferFull)
            {
                return default;
            }
            
            // Try to acquire the segment using Compare And Swap operation
            if (Interlocked.CompareExchange(ref header.CurrentWriteOffset, endWrite, writeOffset) == writeOffset)
            {
                break;
            }

            endWrite = -1;
        }
        
        // If we couldn't acquire the segment, return an empty handle
        if (endWrite == -1)
        {
            return default;
        }

        var logicalEndWrite = (int)(endWrite % bufferSize);
        var logicalStartWrite = (int)((endWrite - chunkTotalSize) % bufferSize);

        // Chunk Header address
        var chunk = _buffer.Slice(logicalStartWrite, 4).Cast<ushort>().ToSpan();
        
        // Check if the whole segment is wrapping around the circular buffer
        if (logicalEndWrite != 0 && logicalEndWrite < logicalStartWrite)
        {
            // Insert a padding chunk because the logical data area is not physically linear and recurse to take the next spot
            chunk[0] = PaddingChunkId | ChunkReadyForDequeue;
            chunk[1] = (ushort)chunkDataSize;

            return Enqueue<T>(chunkId, size, waitTime);
        }
        else
        {
            chunk[0] = chunkId;
            chunk[1] = (ushort)chunkDataSize;
            
            return new EnqueueHandle<T>(_buffer.Slice(logicalStartWrite, 4).Cast<ushort>(), _buffer.Slice(logicalStartWrite + 4, chunkDataSize).Cast<T>());
        }
    }

    public DequeueHandle TryDequeue()
    {
        var bufferSize = _bufferSize;
        ref var header = ref _header[0];
        
        // No data to read
        var startReadOffset = header.StartReadOffset;
        var currentWriteOffset = header.CurrentWriteOffset;

        // Nothing to dequeue ?
        if (startReadOffset >= currentWriteOffset)
        {
            return default;
        }

        var logicalStartReadOffset = (int)(startReadOffset % bufferSize);

        var chunk = _buffer.Slice(logicalStartReadOffset, 4).Cast<ushort>().ToSpan();
        var chunkId = chunk[0];

        switch (chunkId)
        {
            // If the ChunkReadyForDequeue bit is not set, the chunk can't be dequeued, return an empty handle
            case < ChunkReadyForDequeue:
                return default;
            
            // If the chunk is already acquired for dequeue, return an empty handle and wait for the next one
            // Note: we won't dequeue the next chunk until the current one is totally dequeued (i.e. dequeued and disposed), we could change the behavior
            //  and allow many threads to acquire chunks concurrently
            case >= ChunkAcquiredForDequeue:
                return default;
        }

        uint value = default, comp = default;
        var chunkDataSize = chunk[1];
        value.Pack(chunkDataSize, (ushort)(chunkId | ChunkReadyForDequeue | ChunkAcquiredForDequeue));
        comp.Pack(chunkDataSize, (ushort)(chunkId | ChunkReadyForDequeue));

        // Try to acquire this chunk
        if (Interlocked.CompareExchange(ref chunk.Cast<ushort, uint>()[0], value, comp) != comp)
        {
            // Unlikely, but another thread beat us, recurse to try again
            return TryDequeue();
        }
        
        var newRead = startReadOffset + chunkDataSize + 4;
        
        // Detect if we're dealing with a padding chunk
        if ((chunkId & ChunkIdMask) == PaddingChunkId)
        {
            // Create a handle for the padding chunk and dispose it to skip it, then recurse to dequeue the next chunk
            var h = new DequeueHandle(PaddingChunkId, _buffer.Slice(logicalStartReadOffset + 4, 0), startReadOffset, newRead, _header, _buffer, _bufferSize);
            h.Dispose();
            return TryDequeue();
        }

        // Return the handle to the chunk
        return new DequeueHandle((ushort)(chunkId & ChunkIdMask), _buffer.Slice(logicalStartReadOffset + 4, chunkDataSize), startReadOffset, newRead, 
            _header, _buffer, _bufferSize);
    }

    #endregion

    #endregion

    #region Constructors

    private MappedConcurrentChunkBasedQueue(MemorySegment segment, bool create)
    {
        (_header, _buffer) = segment.Split<Header, byte>(sizeof(Header));
        Debug.WriteLineIf(((long)_header.Address & 0x3F) != 0,
            "The header is not aligned on a cache line boundary, this could lead to cache line sharing, which is bad for performance.");
        
        _bufferSize = _buffer.Length - 4;

        if (create)
        {
            segment.ToSpan<byte>().Clear();
        }
    }

    #endregion

    #region Privates

    private readonly int _bufferSize;

    private MemorySegment<byte> _buffer;

    private MemorySegment<Header> _header;

    #endregion

    #region Inner types

    /// <summary>
    /// Dequeue handle used to access the dequeued chunk's type and data
    /// </summary>
    public readonly struct DequeueHandle : IDisposable
    {
        #region Public APIs

        #region Properties

        /// <summary>
        /// Memory Segment spanning the chunk's data
        /// </summary>
        public MemorySegment ChunkData => _chunkData;

        /// <summary>
        /// Chunk's id
        /// </summary>
        public ushort ChunkId { get; }

        /// <summary>
        /// If there was no chunk to dequeue, will be <c>true</c>
        /// </summary>
        public bool IsDefault => _chunkData.IsDefault;

        #endregion

        #region Methods

        /// <summary>
        /// Dispose the handle, make the chunk no longer accessible
        /// </summary>
        public void Dispose()
        {
            if (_chunkData.IsDefault)
            {
                return;
            }
            
            ref var header = ref _header[0];
            var bufferSize = _bufferSize;
            var prevRead = _prevRead;
            var newRead = _newRead;
            var logicalReadOffset = (int)(prevRead % bufferSize);
            var chunkHeader = _buffer.Slice(logicalReadOffset, 4).Cast<ushort>();

            // Thing is, the StartReadOffset must be updated in a pure linear fashion, meaning if you have chunks A, B, C an D. You will dequeue them in this
            //  order, but the call on dispose may be in any order, say C, A, D, B. We can't update the StartReadOffset when disposing C because A and B
            //  are still being processed, and we need to keep their memory segments.
            // So we rely on CompareExchange (CAS operation) to update the StartReadOffset.
            // For our example, here's what will happen when calling dispose on our chunks:
            //  - C: will be flagged as dequeued and processed, but the StartReadOffset won't be updated at all
            //  - A: will be flagged as dequeued and processed, StartReadOffset will move one step (pointing to B)
            //  - D: will be flagged as dequeued and processed, but the StartReadOffset won't be updated at all
            //  - B: will be flagged as dequeued and processed, StartReadOffset will move three times, as B, C and D are flagged as processed
            
            // Flag the chunk as dequeued and processed
            chunkHeader[0] |= ChunkDequeuedAndProcessed;

            // Loop until we break or catch up with the write offset
            while (header.StartReadOffset < header.CurrentWriteOffset)
            {
                // Break if the chunk is not dequeued and processed
                var chunkReadyForDequeue = chunkHeader[0] & (ChunkReadyForDequeue | ChunkAcquiredForDequeue | ChunkDequeuedAndProcessed);
                if (chunkReadyForDequeue != (ChunkReadyForDequeue | ChunkAcquiredForDequeue | ChunkDequeuedAndProcessed))
                {
                    break;
                }

                // Break if the chunk is not the first in line to dispose and update the StartReadOffset
                // Only the thread that acquired a given chunk will dispose its handle, thus update the StartReadOffset, so we don't need atomic operations.
                if (header.StartReadOffset != prevRead)
                {
                    break;
                }
                
                // Past this point we know the chunk is the first to release, so we can and must clear the memory area it occupies, then we can update
                //  the StartReadOffset.
                // We need to clear the memory area because when we acquire a chunk's memory area, it's through a CAS operation and we need the header of this
                //  chunk to be right away in its "clean state" (flags of the first ushort to be all 0), as this structure allows variable sizes, we can't
                //  predict where the header will be, clearing the whole area is our only option if we want to remain lock-free.
                // I don't think it's a big perf hit doing so, either contention is high and everything will be in the cache, or contention is low and we
                //  really care.
                
                var curChunkTotalSize = chunkHeader[1] + 4;
                
                // A "regular" chunk
                if ((chunkHeader[0] & ChunkIdMask) != PaddingChunkId)
                {
                    var chunk = _buffer.Slice(logicalReadOffset, curChunkTotalSize);
                    chunk.ToSpan().Clear();
                }
                // Special case, a padding chunk, it's in two parts as it's wrapping around the circular buffer, we need two clear operations
                else
                {
                    _buffer.Slice(logicalReadOffset).ToSpan().Clear();
                    _buffer.Slice(0, (int)(newRead % bufferSize)).ToSpan().Clear();
                }

                // Update the StartReadOffset to "free" the corresponding memory area, which is clean and ready to be used again
                header.StartReadOffset = newRead;
                
                // Skip to the next chunk and loop
                prevRead = newRead;
                newRead += curChunkTotalSize;
                logicalReadOffset = (int)(prevRead % bufferSize);
                chunkHeader = _buffer.Slice(logicalReadOffset, 4).Cast<ushort>();
            }
        }

        #endregion

        #endregion

        #region Constructors

        internal DequeueHandle(ushort chunkId, MemorySegment chunkData, long prevRead, long newRead, MemorySegment<Header> header, 
            MemorySegment<byte> buffer, int bufferSize)
        {
            _header = header;
            _buffer = buffer;
            _bufferSize = bufferSize;
            _chunkData = chunkData;
            _prevRead = prevRead;
            _newRead = newRead;
            ChunkId = chunkId;
        }

        #endregion

        #region Fields

        private readonly MemorySegment _chunkData;
        private readonly long _prevRead;
        private readonly long _newRead;
        private readonly MemorySegment<Header> _header;
        private readonly MemorySegment<byte> _buffer;
        private readonly int _bufferSize;

        #endregion
    }

    /// <summary>
    /// Enqueue handle used to set the queued chunk's data
    /// </summary>
    /// <typeparam name="T">The type of data to store</typeparam>
    /// <remarks>
    /// DON'T FORGET TO CALL <see cref="Dispose"/> when you've stored the data, otherwise the queue will likely fail at some point to enqueue more items
    /// </remarks>
    [PublicAPI]
    public readonly struct EnqueueHandle<T> : IDisposable where T : unmanaged
    {
        #region Public APIs

        #region Properties

        /// <summary>
        /// Indexer accessor to the chunk's data
        /// </summary>
        public ref T this[int i] => ref _chunkData[i];

        /// <summary>
        /// The memory segment spanning the chunk's data
        /// </summary>
        public MemorySegment<T> ChunkData => _chunkData;

        /// <summary>
        /// Will be <c>true</c> if the chunk failed to be enqueued
        /// </summary>
        public bool IsDefault => _chunkHeader.IsDefault;

        /// <summary>
        /// First item of the chunk
        /// </summary>
        public ref T Value => ref _chunkData.AsRef();

        #endregion

        #region Methods

        /// <summary>
        /// Dispose the handle, make the item ready for dequeue
        /// </summary>
        public void Dispose()
        {
            if (_chunkHeader.IsDefault == false)
            {
                _chunkHeader[0] |= ChunkReadyForDequeue;
            }
        }

        #endregion

        #endregion

        #region Constructors

        internal EnqueueHandle(MemorySegment<ushort> chunkHeader, MemorySegment<T> chunkData)
        {
            _chunkHeader = chunkHeader;
            _chunkData = chunkData;
        }

        #endregion

        #region Fields

        private readonly MemorySegment<ushort> _chunkHeader;
        private readonly MemorySegment<T> _chunkData;

        #endregion
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Header
    {
        // Put both fields on separate cache lines to avoid false sharing
        public long StartReadOffset;
        private fixed byte _padding0[56];
        
        public long CurrentWriteOffset;
        private fixed byte _padding1[56];
    }

    #endregion
}