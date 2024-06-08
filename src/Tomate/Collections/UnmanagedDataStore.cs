using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Tomate;

[PublicAPI]
public unsafe struct UnmanagedDataStore
{
    #region Constants

    private static readonly int EntrySize = sizeof(Entry);
    private static readonly int HeaderSize = sizeof(PageInfoHeader).Pad8();

    #endregion

    #region Public APIs

    #region Properties

    public bool IsDefault => _allocator == null;

    #endregion

    #region Methods

    public static int ComputeStorageSize(int itemCount) => sizeof(Header) + sizeof(PageInfo) * itemCount;

    public static UnmanagedDataStore Create(IPageAllocator pageAllocator, MemorySegment dataStoreRoot)
    {
        return new UnmanagedDataStore(pageAllocator, dataStoreRoot, true);
    }

    public static UnmanagedDataStore Map(IPageAllocator pageAllocator, MemorySegment dataStoreRoot)
    {
        return new UnmanagedDataStore(pageAllocator, dataStoreRoot, false);
    }

    public ref T Get<T>(Handle<T> handle) where T : unmanaged, IUnmanagedFacade
    {
        ref var entry = ref GetEntry(handle.Index);
        Debug.Assert(entry.TypeId == InternalTypeManager.RegisterType<T>());
        Debug.Assert(entry.Generation == handle.Generation);

        return ref Unsafe.As<MemoryBlock, T>(ref entry.MemoryBlock);
    }

    public ref T Get<T>(Handle handle) where T : unmanaged, IUnmanagedFacade
    {
        ref var entry = ref GetEntry(handle.Index);
        Debug.Assert(entry.TypeId == InternalTypeManager.RegisterType<T>());
        Debug.Assert(entry.Generation == handle.Generation);

        return ref Unsafe.As<MemoryBlock, T>(ref entry.MemoryBlock);
    }

    public bool Release(Handle handle)
    {
        ref var entry = ref GetEntry(handle.Index, out var pageIndex, out var indexInPage);
        Debug.Assert(entry.Generation == handle.Generation);

        _pageInfos[pageIndex].Bitmap.FreeBits(indexInPage, 1);
        entry.MemoryBlock.Dispose();
        entry.Generation++;

        return true;
    }

    public Handle<T> Store<T>(T instance) where T : unmanaged, IUnmanagedFacade
    { 
    Retry:
        var pageInfos = _pageInfos;
        var curPageIndex = _header->CurLookupPageIndex;
        ref var pageInfo = ref pageInfos[curPageIndex];
        var pageIndex = pageInfo.Bitmap.AllocateBits(1);
        int fullIndex;

        // Allocation in this page succeed, return the global index
        if (pageIndex != -1)
        {
            fullIndex = pageInfo.BaseIndex + pageIndex;
            goto Epilogue;
        }
        
        // The allocation in this page failed, we retry an allocation from the page after the current, wrapping up to the current
        ++curPageIndex;
        for (int i = 0; i < _header->PageInfoCount - 1; i++)                    // -1 because we start after the current one, so there's one less to test
        {
            var ii = (curPageIndex + i) % _header->PageInfoCount;
            pageIndex = pageInfos[ii].Bitmap.AllocateBits(1);
            if (pageIndex != -1)
            {
                _header->CurLookupPageIndex = ii;
                fullIndex = pageInfos[ii].BaseIndex + pageIndex;
                goto Epilogue;
            }
        }

        // Check if there is no longer free space
        if (_header->PageInfoCount == _header->PageInfoLength)
        {
            ThrowHelper.ItemMaxCapacityReachedException(_header->PageInfoLength * _header->EntryCountPerPageInfo);
        }

        // Creating a new page is a rare event, rely on an interprocess access control
        if (_header->AccessControl.TryTakeControl())
        {
            try
            {
                // We couldn't allocate in any pages we've parsed, add a new page
                var newPageSegment = _allocator.AllocatePages(1);
                if (newPageSegment.IsDefault)
                {
                    throw new OutOfMemoryException();
                }
                newPageSegment.Cast<byte>().ToSpan().Clear();
                var newPageId = _allocator.ToBlockId(newPageSegment);

                // Chain the new page next to the previous one
                ref var lastPageInfo = ref pageInfos[_header->LastPageInfoIndex];
                ref var lastPageHeader = ref PageInfoHeader.GetHeader(lastPageInfo.PageSegment);
                lastPageHeader.NextPageId = newPageId;
                
                // Initialize the new page
                _header->LastPageInfoIndex++;
                InitPage(newPageSegment, 0);
                MapPage(ref _pageInfos[_header->LastPageInfoIndex], newPageSegment, _header->LastPageInfoIndex);
                _header->CurLookupPageIndex = _header->LastPageInfoIndex;
                _header->PageInfoCount++;
            }
            finally
            {
                _header->AccessControl.ReleaseControl();
            }
        }

        // If the lock was already held, wait until it's released and retry
        else
        {
            _header->AccessControl.WaitUntilReleased();
        }
        
        // In any case, retry the allocation
        goto Retry;
        
    Epilogue:
        Debug.Assert(fullIndex != -1);
        ref var entry = ref GetEntry(fullIndex);
        entry.MemoryBlock = instance.MemoryBlock;
        entry.MemoryBlock.AddRef();
        entry.Generation++;
        entry.TypeId = InternalTypeManager.RegisterType<T>();
        
        return new Handle<T>(fullIndex, entry.Generation);
    }

    #endregion

    #endregion

    #region Constructors

    private UnmanagedDataStore(IPageAllocator pageAllocator, MemorySegment memorySegment, bool create)
    {
        var pageSize = pageAllocator.PageSize;
        var entryCountPerPage = pageSize / EntrySize;

        if (create)
        {
            // Compute how many items can fit in one page size
            // Algo is incredibly stupid, but fast enough...
            // ReSharper disable once NotAccessedVariable
            var iteration = 0;
            var bitmapSize = ConcurrentBitmapL4.ComputeRequiredSize(entryCountPerPage);
            while (HeaderSize + bitmapSize.Pad<Entry>() + (entryCountPerPage * EntrySize) > pageSize)
            {
                --entryCountPerPage;
                bitmapSize = ConcurrentBitmapL4.ComputeRequiredSize(entryCountPerPage);
                iteration++;
            }

            // The given segment is split into two parts: the header and storing all the PageInfo we can on the remaining part
            var (headerSegment, pageInfosSegment) = memorySegment.Split<Header, PageInfo>(sizeof(Header));
            _header = headerSegment.Address;
            _pageInfos = pageInfosSegment.Address;

            // Setup header
            _pageAllocatorId = pageAllocator.PageAllocatorId;
            _header->CurLookupPageIndex = 0;
            _header->LastPageInfoIndex = 0;
            _header->PageInfoCount = 1;
            _header->PageInfoLength = pageInfosSegment.Length;
            _header->PageInfoBitmapSize = bitmapSize;
            _header->EntryCountPerPageInfo = entryCountPerPage;
            _header->AccessControl = new MappedExclusiveAccessControl();
        
            // Allocate a page that will store the content of the first PageInfo
            var firstPage = _allocator.AllocatePages(1);
            firstPage.Cast<byte>().ToSpan().Clear();

            // Initialize the first PageInfo
            InitPage(firstPage, 0);
            MapPage(ref _pageInfos[0], firstPage, 0);
        }
        else
        {
            // The given segment is split into two parts: the header and storing all the PageInfo we can on the remaining part
            var (headerSegment, pageInfosSegment) = memorySegment.Split<Header, PageInfo>(sizeof(Header));
            _header = headerSegment.Address;
            _pageInfos = pageInfosSegment.Address;
            _pageAllocatorId = pageAllocator.PageAllocatorId;
        }
    }

    #endregion

    #region Internals

    internal int EntryCountPerPage => _header->EntryCountPerPageInfo;

    #endregion

    #region Privates

    private IPageAllocator _allocator => IPageAllocator.GetPageAllocator(_pageAllocatorId);

    private Header* _header;
    private PageInfo* _pageInfos;
    private readonly int _pageAllocatorId;

    private ref Entry GetEntry(int index) => ref GetEntry(index, out _, out _);

    private ref Entry GetEntry(int index, out int pageIndex, out int indexInPage)
    {
        Debug.Assert(index >= 0, $"Index can't be a negative number, {index} is incorrect.");
        
        // Fast path
        var pageInfos = _pageInfos;
        if (index < _header->EntryCountPerPageInfo)
        {
            pageIndex = 0;
            indexInPage = index;
            return ref pageInfos[0].Data.Slice(index, 1).AsRef();
        }

        {
            pageIndex = Math.DivRem(index, _header->EntryCountPerPageInfo, out indexInPage);
            Debug.Assert(pageIndex < _header->PageInfoCount, $"Requested item at index {index} is out of bounds");
            return ref pageInfos[pageIndex].Data.Slice(indexInPage, 1).AsRef();
        }
    }

    private void InitPage(MemorySegment pageSegment, int nextPageId)
    {
        ref var h = ref PageInfoHeader.GetHeader(pageSegment);
        h.NextPageId = nextPageId;
        ConcurrentBitmapL4.Create(_header->EntryCountPerPageInfo, pageSegment.Slice(HeaderSize, _header->PageInfoBitmapSize));
    }

    private void MapPage(ref PageInfo pageInfo, MemorySegment pageSegment, int pageIndex)
    {
        pageInfo.PageSegment = pageSegment;
        pageInfo.Bitmap = ConcurrentBitmapL4.Map(_header->EntryCountPerPageInfo, pageSegment.Slice(HeaderSize, _header->PageInfoBitmapSize));
        pageInfo.Data = pageSegment.Slice(HeaderSize + _header->PageInfoBitmapSize.Pad<Entry>()).Cast<Entry>();
        pageInfo.BaseIndex = pageIndex * _header->EntryCountPerPageInfo;
    }

    #endregion

    #region Inner types

    [StructLayout(LayoutKind.Sequential)]
    private struct Entry
    {
        #region Fields

        public MemoryBlock MemoryBlock;
        public ushort Generation;           // Even == entry empty, Odd == entry taken
        public ushort TypeId;

        #endregion
    }

    [PublicAPI]
    public readonly struct Handle(int index, int generation)
    {
        #region Public APIs

        #region Properties

        public bool IsDefault => Generation == 0 && Index == 0;

        #endregion

        #endregion

        #region Internals

        internal int Generation { get; } = generation;

        internal int Index { get; } = index;

        #endregion
    }

    [PublicAPI]
    public readonly struct Handle<T>(int index, int generation) where T : unmanaged, IUnmanagedFacade
    {
        #region Public APIs

        #region Properties

        public bool IsDefault => Generation == 0 && Index == 0;

        #endregion

        #region Methods

        public static implicit operator Handle<T>(Handle h)
        {
            return new Handle<T>(h.Index, h.Generation);
        }

        public static implicit operator Handle(Handle<T> h)
        {
            return new Handle(h.Index, h.Generation);
        }

        #endregion

        #endregion

        #region Internals

        internal int Generation { get; } = generation;

        internal int Index { get; } = index;

        #endregion
    }

    private struct Header
    {
        #region Fields

        public MappedExclusiveAccessControl AccessControl;
        public int CurLookupPageIndex;
        public int EntryCountPerPageInfo;
        public int LastPageInfoIndex;
        public int PageInfoBitmapSize;
        public int PageInfoCount;
        public int PageInfoLength;

        #endregion
    }

    private struct PageInfo
    {
        #region Fields

        public int BaseIndex;
        public ConcurrentBitmapL4 Bitmap;
        public MemorySegment<Entry> Data;
        public MemorySegment PageSegment;

        #endregion
    }

    private struct PageInfoHeader
    {
        #region Public APIs

        #region Methods

        public static ref PageInfoHeader GetHeader(MemorySegment page) => ref page.Cast<PageInfoHeader>().AsRef();

        #endregion

        #endregion

        #region Fields

        public int NextPageId;

        #endregion
    }

    #endregion
}

internal static class InternalTypeManager
{
    #region Constants

    private static readonly ConcurrentDictionary<Type, int> IndexByType = new();
    private static readonly ConcurrentDictionary<int, Type> TypeByIndex = new();

    #endregion

    #region Internals

    [CanBeNull]
    internal static Type GetType(ushort typeId)
    {
        TypeByIndex.TryGetValue(typeId, out var type);
        return type;
    }

    internal static ushort RegisterType<T>()
    {
        Debug.Assert(_curIndex < ushort.MaxValue, $"there are too many (more than {ushort.MaxValue}) type registered");
        return (ushort)IndexByType.GetOrAdd(typeof(T), type =>
        {
            var index =  Interlocked.Increment(ref _curIndex);
            TypeByIndex.TryAdd(index, type);
            return index;
        });
    }

    #endregion

    #region Privates

    private static int _curIndex = -1;

    #endregion
}