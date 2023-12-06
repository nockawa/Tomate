using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Tomate;

[PublicAPI]
public unsafe class PageAllocator : IDisposable, IPageAllocator
{
    #region Constants

    private static readonly ConcurrentDictionary<IntPtr, byte[]> _allocatedArrays  = new();

    #endregion

    #region Public APIs

    #region Properties

    public byte* BaseAddress => _dataSegment.Address;
    public int PageAllocatorId { get; }
    public int PageCount { get; }
    public int PageSize { get; }

    /// <summary>
    /// Check if the instance is disposed or not.
    /// </summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Will be incremented every time a new page is allocated
    /// </summary>
    public int PageAllocationEpoch { get; private set; }

    #endregion

    #region Methods

    public static bool FreeHeapPinnedArray(MemorySegment<byte> segment) => _allocatedArrays.TryRemove(new IntPtr(segment.Address), out _);

    public MemorySegment AllocatePages(int pageCount)
    {
        Debug.Assert(pageCount is > 0 and <= 64, "Page count must be within the [1-64] range");
        var pageIndex = _occupancyMap.AllocateBits(pageCount);
        if (pageIndex == -1)
        {
            return default;
        }

        ++PageAllocationEpoch;

        return _dataSegment.Slice(pageIndex*PageSize, pageCount*PageSize);
    }

    public void Clear()
    {
        throw new NotImplementedException();
    }

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

        IPageAllocator.UnregisterPageAllocator(PageAllocatorId);
        IsDisposed = true;
    }

    public bool FreePages(MemorySegment pages)
    {
        var pageIndex = (int)((pages.Address - _dataSegment.Address) / PageSize);
        var pageCount = pages.Length / PageSize;

        _occupancyMap.FreeBits(pageIndex, pageCount);
        return true;
    }

    public MemorySegment FromBlockId(int blockId)
    {
        var index = blockId & 0xFFFF;
        var length = blockId >> 16;
        return _dataSegment.Slice(index * PageSize, length * PageSize);
    }

    public int ToBlockId(MemorySegment segment)
    {
        var index = ((segment.Address - _dataSegment.Address) / PageSize);
        Debug.Assert(index <= ushort.MaxValue);

        var length = segment.Length / PageSize;
        Debug.Assert(length <= ushort.MaxValue);
        return (length << 16) | (int)index;
    }

    #endregion

    #endregion

    #region Fields

    private readonly MemorySegment<byte> _bitmapSegment;
    private readonly MemorySegment<byte> _dataSegment;
    private ConcurrentBitmapL4 _occupancyMap;

    #endregion

    #region Constructors

    public PageAllocator(int pageSize, int pageCount)
    {
        PageSize = pageSize;
        PageCount = pageCount;
        _dataSegment = AllocateHeapPinnedArray(pageSize * pageCount);
        _bitmapSegment = AllocateHeapPinnedArray(ConcurrentBitmapL4.ComputeRequiredSize(pageCount));
        _occupancyMap = ConcurrentBitmapL4.Create(pageCount, _bitmapSegment);
        PageAllocatorId = IPageAllocator.RegisterPageAllocator(this);
    }

    #endregion

    #region Private methods

    private static MemorySegment<byte> AllocateHeapPinnedArray(int length)
    {
        var a = GC.AllocateUninitializedArray<byte>(length, true);
        var addr = Marshal.UnsafeAddrOfPinnedArrayElement(a, 0).ToPointer();

        // We need to keep a reference on the array, otherwise it will be GCed and the address we have will corrupt things
        _allocatedArrays.TryAdd(new IntPtr(addr), a);

        return new(addr, length);
    }

    #endregion
}