using System.Collections.Concurrent;
using System.Diagnostics;
using JetBrains.Annotations;
// ReSharper disable once RedundantUsingDirective
using System.Runtime.CompilerServices;

namespace Tomate;

/// <summary>
/// This is the interface to implement a Memory Manager (a.k.a. Allocator).
/// </summary>
/// <remarks>
/// Anyone can implement an Allocator, few rules must be followed and it's best to take a look at <see cref="DefaultMemoryManager"/>.
/// Each Allocator instance has a unique Id <see cref="MemoryManagerId"/> that allows anyone to retrieve it back using <see cref="GetMemoryManager"/>.
/// It also allows to reference a given instance inside an unmanaged type, which can't be with an instance of <see cref="IMemoryManager"/>, see <see cref="UnmanagedList{T}"/>.
/// </remarks>
[PublicAPI]
public interface IMemoryManager
{
    #region Statics

    static IMemoryManager()
    {
        _memoryManagerById = new ConcurrentDictionary<int, IMemoryManager>();
        _curMemoryManagerId = 0;
    }

    private static ConcurrentDictionary<int, IMemoryManager> _memoryManagerById;
    private static int _curMemoryManagerId;

    public static int RegisterMemoryManager(IMemoryManager memoryManager)
    {
        var id = Interlocked.Increment(ref _curMemoryManagerId);
        _memoryManagerById.TryAdd(id, memoryManager);
        return id;
    }

    public static bool UnregisterMemoryManager(int memoryManagerId)
    {
        return _memoryManagerById.TryRemove(memoryManagerId, out _);
    }

    public static IMemoryManager GetMemoryManager(int id)
    {
        _memoryManagerById.TryGetValue(id, out var memoryManager);
        return memoryManager;
    }
    

    #endregion
    /// <summary>
    /// Check if the instance is disposed or not.
    /// </summary>
    bool IsDisposed { get; }

    int MaxAllocationLength { get; }
    int MemoryManagerId { get; }

    /// <summary>
    /// Allocate a Memory Block
    /// </summary>
    /// <param name="length">Length of the block to allocate.</param>
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
    MemoryBlock Allocate(int length);
#endif

    /// <summary>
    /// Allocate a Memory Block
    /// </summary>
    /// <typeparam name="T">The type of each item of the segment assigned to the block.</typeparam>
    /// <param name="length">Length (in {T}) of the segment to allocate.</param>
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
    MemoryBlock<T> Allocate<T>(int length) where T : unmanaged;
#endif

    bool Resize(ref MemoryBlock memoryBlock, int newLength, bool zeroExtra=false)
    {
        if (memoryBlock.MemorySegment.Length == newLength)
        {
            return true;
        }
        
        var newBlock = Allocate(newLength);
        if (newLength > memoryBlock.MemorySegment.Length)
        {
            memoryBlock.MemorySegment.ToSpan<byte>().CopyTo(newBlock.MemorySegment.ToSpan<byte>());
            if (zeroExtra)
            {
                memoryBlock.MemorySegment.ToSpan<byte>()[newLength..].Clear();
            }
        }
        else
        {
            memoryBlock.MemorySegment.ToSpan<byte>()[..newLength].CopyTo(newBlock.MemorySegment.ToSpan<byte>());
        }
        
        memoryBlock.Dispose();
        memoryBlock = newBlock;
        return true;
    }

    unsafe bool Resize<T>(ref MemoryBlock<T> memoryBlock, int newLength, bool zeroExtra=false) where T : unmanaged
    {
        var mb = (MemoryBlock)memoryBlock;
        var res = Resize(ref mb, newLength * sizeof(T));
        if (res == false)
        {
            return false;
        }

        memoryBlock = mb.Cast<T>();
        Debug.Assert(memoryBlock.MemorySegment.Length == newLength);
        return true;
    }

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

[PublicAPI]
public interface IPageAllocator
{
    unsafe byte* BaseAddress { get; }
    int PageSize { get; }

    MemorySegment AllocatePages(int length);
    bool FreePages(MemorySegment pages);
    int ToBlockId(MemorySegment segment);
    MemorySegment FromBlockId(int blockId);
}

[PublicAPI]
public interface IBlockAllocator : IDisposable
{
    IMemoryManager Owner { get; }
    int BlockIndex { get; }
    bool Free(MemoryBlock memoryBlock);
}
