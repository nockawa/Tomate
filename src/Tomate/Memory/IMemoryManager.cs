namespace Tomate;

public interface IMemoryManager
{
    /// <summary>
    /// Check if the instance is disposed or not.
    /// </summary>
    bool IsDisposed { get; }

    int PinnedMemoryBlockSize { get; }

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
    MemorySegment Allocate(int size);

    /// <summary>
    /// Allocate a Memory Segment
    /// </summary>
    /// <typeparam name="T">The type of each item of the segment.</typeparam>
    /// <param name="size">Length (in {T}) of the segment to allocate.</param>
    /// <returns>The segment or an exception will be fired if we couldn't allocate one.</returns>
    /// <exception cref="ObjectDisposedException">Can't allocate because the object is disposed.</exception>
    /// <exception cref="OutOfMemoryException">The requested size is too big.</exception>
    /// <remarks>
    /// The segment's address will always be aligned on 64 bytes, its size will also be padded on 64 bytes.
    /// The segment's address is fixed, you can store it with the lifetime that suits you, it doesn't matter as the segment is part of a
    /// Pinned Memory Block that is a pinned allocation (using <see cref="GC.AllocateUninitializedArray{U}"/> with pinned set to true).
    /// </remarks>
    MemorySegment<T> Allocate<T>(int size) where T : unmanaged;

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
    bool Free(MemorySegment segment);
    bool Free<T>(MemorySegment<T> segment) where T : unmanaged;

    /// <summary>
    /// Release all the allocated segments, free the memory allocated through .net.
    /// </summary>
    void Clear();
}

public interface IPageAllocator
{
    unsafe byte* BaseAddress { get; }
    int PageSize { get; }

    MemorySegment AllocatePages(int length);
    bool FreePages(MemorySegment pages);
    unsafe int ToBlockId(MemorySegment segment);
    unsafe MemorySegment FromBlockId(int blockId);
}