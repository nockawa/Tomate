using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

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
        _dataSegment = AllocateHeapPinnedArray(pageSize * pageCount);
        _bitmapSegment = AllocateHeapPinnedArray(ConcurrentBitmapL4.ComputeRequiredSize(pageCount));
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

        FreeHeapPinnedArray(_bitmapSegment);
        FreeHeapPinnedArray(_dataSegment);

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

    private static MemorySegment<byte> AllocateHeapPinnedArray(int length)
    {
        var a = GC.AllocateUninitializedArray<byte>(length, true);
        var addr = Marshal.UnsafeAddrOfPinnedArrayElement(a, 0).ToPointer();

        // We need to keep a reference on the array, otherwise it will be GCed and the address we have will corrupt things
        _allocatedArrays.TryAdd(new IntPtr(addr), a);

        return new(addr, length);
    }

    public static bool FreeHeapPinnedArray(MemorySegment<byte> segment) => _allocatedArrays.TryRemove(new IntPtr(segment.Address), out _);

    private static readonly ConcurrentDictionary<IntPtr, byte[]> _allocatedArrays  = new();
}