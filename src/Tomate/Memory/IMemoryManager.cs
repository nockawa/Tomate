using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using static Tomate.DefaultMemoryManager.SmallBlockAllocator.TwoWaysLinkedList;
using static Tomate.MemoryManager;

namespace Tomate;

/// <summary>
/// This is the interface to implement a Memory Manager (a.k.a. Allocator).
/// </summary>
/// <remarks>
/// Anyone can implement an Allocator, few rules must be followed and it's best to take a look at <see cref="DefaultMemoryManager"/>.
/// Each Allocator instance has a unique Id <see cref="MemoryManagerId"/> that allows anyone to retrieve it back using <see cref="GetMemoryManager"/>.
/// It also allows to reference a given instance inside an unmanaged type, which can't be with an instance of <see cref="IMemoryManager"/>, see <see cref="UnmanagedList{T}"/>.
/// </remarks>
public interface IMemoryManager
{
    /// <summary>
    /// Check if the instance is disposed or not.
    /// </summary>
    bool IsDisposed { get; }

    int MaxAllocationLength { get; }
    int MemoryManagerId { get; }

    static IMemoryManager()
    {
        _memoryManagerById = new ConcurrentDictionary<int, IMemoryManager>();
        _curMemoryManagerId = 0;
    }

    private static ConcurrentDictionary<int, IMemoryManager> _memoryManagerById;
    private static int _curMemoryManagerId;

    public static int AllocMemoryManagerId(IMemoryManager memoryManager)
    {
        var id = Interlocked.Increment(ref _curMemoryManagerId);
        _memoryManagerById.TryAdd(id, memoryManager);
        return id;
    }

    public static IMemoryManager GetMemoryManager(int id)
    {
        _memoryManagerById.TryGetValue(id, out var memoryManager);
        return memoryManager;
    }

    /// <summary>
    /// Allocate a Memory Block
    /// </summary>
    /// <param name="size">Length of the block to allocate.</param>
    /// <returns>The block or an exception will be fired if we couldn't allocate one.</returns>
    /// <exception cref="ObjectDisposedException">Can't allocate because the object is disposed.</exception>
    /// <exception cref="OutOfMemoryException">The requested size is too big.</exception>
    /// <remarks>
    /// The block's address will always be aligned on at least 16 bytes.
    /// The block's address is fixed.
    /// </remarks>
#if DEBUGALLOC
     MemoryBlock Allocate(int size, [CallerFilePath] string sourceFile = "", [CallerLineNumber] int lineNb = 0);
#else
    MemoryBlock Allocate(int size);
#endif

    /// <summary>
    /// Allocate a Memory Block
    /// </summary>
    /// <typeparam name="T">The type of each item of the segment assigned to the block.</typeparam>
    /// <param name="size">Length (in {T}) of the segment to allocate.</param>
    /// <returns>The segment or an exception will be fired if we couldn't allocate one.</returns>
    /// <exception cref="ObjectDisposedException">Can't allocate because the object is disposed.</exception>
    /// <exception cref="OutOfMemoryException">The requested size is too big.</exception>
    /// <remarks>
    /// The segment's address will always be aligned on 16 bytes, its size will also be padded on 16 bytes.
    /// The segment's address is fixed.
    /// </remarks>
#if DEBUGALLOC
    MemoryBlock<T> Allocate<T>(int size, [CallerFilePath] string sourceFile = "", [CallerLineNumber] int lineNb = 0) where T : unmanaged;
#else
    MemoryBlock<T> Allocate<T>(int size) where T : unmanaged;
#endif

    /// <summary>
    /// Free a previously allocated block
    /// </summary>
    /// <param name="block">The memory block to free</param>
    /// <returns><c>true</c> if the block was successfully released, <c>false</c> otherwise.</returns>
    /// <exception cref="ObjectDisposedException">Can't free if the instance is disposed, all blocks have been released anyway.</exception>
    bool Free(MemoryBlock block);
    bool Free<T>(MemoryBlock<T> block) where T : unmanaged;

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

public interface IBlockAllocator : IDisposable
{
    int BlockIndex { get; }
    bool Free(MemoryBlock memoryBlock);
}
