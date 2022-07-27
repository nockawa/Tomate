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
    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public bool IsDisposed { get; }
    public int PinnedMemoryBlockSize { get; }
    public MemorySegment Allocate(int size)
    {
        throw new NotImplementedException();
    }

    public MemorySegment<T> Allocate<T>(int size) where T : unmanaged
    {
        throw new NotImplementedException();
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