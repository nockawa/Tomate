using System.Diagnostics;
using JetBrains.Annotations;

namespace Tomate;

[PublicAPI]
public unsafe struct UnmanagedDataStore<T> : IDisposable where T: unmanaged
{
    private static readonly int EntrySize = sizeof(T);
    private static readonly int HeaderSize = sizeof(Header).Pad8();
    
    private IPageAllocator _allocator;
    private int _bitmapSize;

    private struct PageInfo
    {
        public MemorySegment PageSegment;
        public ConcurrentBitmapL4 Bitmap;
        public MemorySegment<T> Data;
        public int BaseIndex;
    }

    private PageInfo[] _pageInfos;
    private int _curPageIndex;
    
    private ref Header GetHeader(MemorySegment page) => ref page.Cast<Header>().AsRef();

    private struct Header
    {
        public int EntryCountPerPage;
        public int NextPageId;
    }
    
    public static UnmanagedDataStore<T> Create(IPageAllocator pageAllocator)
    {
        return new UnmanagedDataStore<T>(pageAllocator);
    }

    public int ItemCountPerPage { get; }

    public int Allocate(int length)
    { 
    Retry:
        Debug.Assert(length <= 64, $"The Data Store supports a length up to 64, {length} is not valid.");
        var pageInfos = _pageInfos;
        var curPageIndex = _curPageIndex;
        ref var pageInfo = ref pageInfos[curPageIndex];
        var index = pageInfo.Bitmap.AllocateBits(length);

        // Allocation in this page succeed, return the global index
        if (index != -1)
        {
            return pageInfo.BaseIndex + index;
        }
        
        // The allocation in this page failed, we retry an allocation from the page after the current, wrapping up to the current
        ++curPageIndex;
        for (int i = 0; i < pageInfos.Length; i++)
        {
            var ii = (curPageIndex + i) % pageInfos.Length;
            index = pageInfos[ii].Bitmap.AllocateBits(length);
            if (index != -1)
            {
                _curPageIndex = ii;
                return pageInfos[ii].BaseIndex + index;
            }
        }

        // We couldn't allocate in any pages we've parsed, add a new page
        var newPageSegment = _allocator.AllocatePages(1);
        if (newPageSegment.IsDefault)
        {
            throw new OutOfMemoryException();
        }
        var newPageId = _allocator.ToBlockId(newPageSegment);

        ref var lastPageInfo = ref pageInfos[^1];
        ref var lastPageHeader = ref GetHeader(lastPageInfo.PageSegment);
        
        // Set the new page, if another thread beat us, dealloc ours and use the other thread's one
        if (Interlocked.CompareExchange(ref lastPageHeader.NextPageId, newPageId, 0) != 0)
        {
            _allocator.FreePages(newPageSegment);
        }
        else
        {
            InitPage(newPageSegment, 0);
        }

        // Rebuild the pageInfos array, starting from the first page
        var pageInfoList = new List<PageInfo>(pageInfos.Length + 1);

        var curBaseIndex = 0;
        var curPageId = _allocator.ToBlockId(pageInfos[0].PageSegment);
        while (curPageId != 0)
        {
            var curPageSegment = _allocator.FromBlockId(curPageId);
            ref var curHeader = ref GetHeader(curPageSegment);
            var curPageInfo = new PageInfo();
            MapPage(ref curPageInfo, curPageSegment, curBaseIndex);
            pageInfoList.Add(curPageInfo);

            curPageId = curHeader.NextPageId;
            curBaseIndex++;
        }

        var newPageInfos = pageInfoList.ToArray();
        _pageInfos = newPageInfos;
        _curPageIndex = newPageInfos.Length - 1;
        goto Retry;
    }

    /// <summary>
    /// Get a previously allocated item
    /// </summary>
    /// <param name="index">Index of the item</param>
    /// <param name="length">Length of the item</param>
    /// <returns>A reference to the first element of the requested item</returns>
    /// <remarks>
    /// This method won't check if the length you provide is valid, it won't check if the index is also and will assert in debug if it goes out of the
    /// boundaries of the allocated pages
    /// </remarks>
    public Span<T> GetItem(int index, int length = 1)
    {
        Debug.Assert(index >= 0, $"Index can't be a negative number, {index} is incorrect.");
        
        // Fast path
        var pageInfos = _pageInfos;
        if (index < ItemCountPerPage)
        {
            return pageInfos[0].Data.Slice(index, length).ToSpan();
        }
        
        var pageIndex = Math.DivRem(index, ItemCountPerPage, out var indexInPage);
        Debug.Assert(pageIndex < pageInfos.Length, $"Requested item at index {index} is out of bounds");
        return pageInfos[pageIndex].Data.Slice(indexInPage, length).ToSpan();
    }
    
    private UnmanagedDataStore(IPageAllocator pageAllocator)
    {
        var pageSize = pageAllocator.PageSize;
        ItemCountPerPage = pageSize / EntrySize;

        // This is incredibly stupid, but fast enough...
        // ReSharper disable once NotAccessedVariable
        var iteration = 0;
        _bitmapSize = ConcurrentBitmapL4.ComputeRequiredSize(ItemCountPerPage);
        while (HeaderSize + _bitmapSize.Pad<T>() + (ItemCountPerPage * EntrySize) > pageSize)
        {
            --ItemCountPerPage;
            _bitmapSize = ConcurrentBitmapL4.ComputeRequiredSize(ItemCountPerPage);
            iteration++;
        }

        _allocator = pageAllocator;
        var firstPage = _allocator.AllocatePages(1);

        _pageInfos = new PageInfo[1];
        _curPageIndex = 0;
        InitPage(firstPage, 0);
        MapPage(ref _pageInfos[0], firstPage, 0);
    }

    private void InitPage(MemorySegment pageSegment, int nextPageId)
    {
        ref var h = ref GetHeader(pageSegment);
        h.EntryCountPerPage = ItemCountPerPage;
        h.NextPageId = nextPageId;
        ConcurrentBitmapL4.Create(ItemCountPerPage, pageSegment.Slice(HeaderSize, _bitmapSize));
    }

    private void MapPage(ref PageInfo pageInfo, MemorySegment pageSegment, int pageIndex)
    {
        pageInfo.PageSegment = pageSegment;
        pageInfo.Bitmap = ConcurrentBitmapL4.Map(ItemCountPerPage, pageSegment.Slice(HeaderSize, _bitmapSize));
        pageInfo.Data = pageSegment.Slice(HeaderSize + _bitmapSize.Pad<T>()).Cast<T>();
        pageInfo.BaseIndex = pageIndex * ItemCountPerPage;
    }

    public bool IsDefault => _allocator == null;
    public bool IsDisposed => _pageInfos == null;
    public void Dispose()
    {
        if (IsDefault || IsDisposed)
        {
            return;
        }

        _pageInfos = null;
    }
}