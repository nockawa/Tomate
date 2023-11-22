using JetBrains.Annotations;

namespace Tomate;

[PublicAPI]
public interface IPageAllocator
{
    /// <summary>
    /// Return the base address of the linear memory block that old all the pages.
    /// </summary>
    unsafe byte* BaseAddress { get; }
    
    /// <summary>
    /// Return the size one one given page
    /// </summary>
    int PageSize { get; }

    /// <summary>
    /// Allocate one or many consecutive pages
    /// </summary>
    /// <param name="length">Must be 1 to 64.</param>
    /// <returns>
    /// The returned memory segment will be <see cref="PageSize"/>*<paramref name="length"/>.
    /// Return default if the allocation failed.
    /// </returns>
    MemorySegment AllocatePages(int length);
    
    /// <summary>
    /// Free the pages previously allocated corresponding to the given segment.
    /// </summary>
    /// <param name="pages">The memory segment spanning all the pages to free.</param>
    /// <returns><c>true</c> if the operation succeeded, <c>false</c> otherwise.</returns>
    bool FreePages(MemorySegment pages);
    
    /// <summary>
    /// Convert the given segment's start address to the corresponding BlockId
    /// </summary>
    /// <param name="segment">The segment to get the BlockId from</param>
    /// <returns>The BlockId, an integer that contains the index of the block and its length encoded as two shorts.</returns>
    int ToBlockId(MemorySegment segment);
    
    /// <summary>
    /// Convert a BlockId to its corresponding MemorySegment
    /// </summary>
    /// <param name="blockId">The BlockId previously retrieved with <see cref="ToBlockId"/>.</param>
    /// <returns>The correponding memory segment or default if it failed.</returns>
    MemorySegment FromBlockId(int blockId);
}