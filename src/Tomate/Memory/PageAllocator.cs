using System.Diagnostics;

namespace Tomate;

public unsafe class PageAllocator : IDisposable, IPageAllocator
{
    private ConcurrentBitmapL4 _occupancyMap;
    private readonly MemorySegment<byte> _bitmapSegment;
    private readonly MemorySegment<byte> _dataSegment;
    
    /// <summary>
    /// Will be incremented every time a new page is allocated
    /// </summary>
    public int PageAllocationEpoch { get; private set; }

    public PageAllocator(int pageSize, int pageCount)
    {
        PageSize = pageSize;
        PageCount = pageCount;
        _dataSegment = MemorySegment<byte>.AllocateHeapPinnedArray(pageSize * pageCount);
        _bitmapSegment = MemorySegment<byte>.AllocateHeapPinnedArray(ConcurrentBitmapL4.ComputeRequiredSize(pageCount));
        _occupancyMap = ConcurrentBitmapL4.Create(pageCount, _bitmapSegment);
    }

    /// <summary>
    /// Check if the instance is disposed or not.
    /// </summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Dispose the instance, free the allocated memory.
    /// </summary>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        MemorySegment<byte>.FreeHeapPinnedArray(_bitmapSegment);
        MemorySegment<byte>.FreeHeapPinnedArray(_dataSegment);

        IsDisposed = true;
    }

    public byte* BaseAddress => _dataSegment.Address;
    public int PageSize { get; }
    public int PageCount { get; }

    public MemorySegment AllocatePages(int pageCount)
    {
        Debug.Assert(pageCount is > 0 and <= 64, "Page count must be within the [1-64] range");
        var pageIndex = _occupancyMap.AllocateBits(pageCount);

        ++PageAllocationEpoch;

        return _dataSegment.Slice(pageIndex*PageSize, pageCount*PageSize);
    }

    public bool FreePages(MemorySegment pages)
    {
        var pageIndex = (int)((pages.Address - _dataSegment.Address) / PageSize);
        var pageCount = pages.Length / PageSize;

        _occupancyMap.FreeBits(pageIndex, pageCount);
        return true;
    }

    public int ToBlockId(MemorySegment segment)
    {
        var index = ((segment.Address - _dataSegment.Address) / PageSize);
        Debug.Assert(index <= ushort.MaxValue);

        var length = segment.Length / PageSize;
        Debug.Assert(length <= ushort.MaxValue);
        return (length << 16) | (int)index;
    }

    public MemorySegment FromBlockId(int blockId)
    {
        var index = blockId & 0xFFFF;
        var length = blockId >> 16;
        return _dataSegment.Slice(index * PageSize, length * PageSize);
    }

    public void Clear()
    {
        throw new NotImplementedException();
    }
}