using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Tomate;

/// <summary>
/// Append-only collection on a Page Allocator
/// </summary>
/// <typeparam name="T">Type of each item of the collection</typeparam>
/// <remarks>
/// <para>
/// Usage:
///  This is a pretty simple type: the user creates an instance by passing an allocator and a capacity (the maximum number of pages that
///  will be allocated). Each page can stores up to x items (<c>x = <seealso cref="IPageAllocator.PageSize"/> / sizeof(T)</c>).
/// The user then allocates a set of x items by calling <seealso cref="Reserve"/> an ID is returned and used for access to the <seealso cref="MemorySegment"/>
///  addressing these items later by calling <seealso cref="Get"/>.
/// Allocated items can't be freed: it's an append-only collection.
/// </para>
/// <para>
/// Implementation:
///  The collection stores a Page Directory inside the first page, this directory contains for each page  its offset from the base address of the allocator.
///  The ID returned by <seealso cref="Reserve"/> is a linear offset into the collection and we use the Page Directory to determine in which page is stored
///   the given set of items.
///  You can't allocate a set of item that exceed the number of items a given page can hold because each set is a contiguous space of data. If when calling
/// <seealso cref="Reserve"/> the current page can't hold the requested number of items, the remaining space in this page is simply wasted and another one
///  is allocated. Which means you could end up with a big fragmentation and poor usage ratio if you always allocate sets of items that are bigger than half
///  of a <seealso cref="IPageAllocator.PageSize"/>, so choose the Page Size of the allocator accordingly !
/// This type can address a very big space of data, way above 4GiB.
///  The limit is <c><seealso cref="IPageAllocator.PageSize"/> * <seealso cref="MappedAppendCollection{T}.Capacity"/> * sizeof(T)</c>
/// </para>
/// </remarks>
[PublicAPI]
public unsafe struct MappedAppendCollection<T> : IDisposable where T : unmanaged
{
    #region Public APIs

    #region Properties

    public int AllocatedPageCount => _header->AllocatedPageCount;

    public (long totalAllocatedByte, float efficiency) AllocationStats
    {
        get
        {
            var totalPagedSize = _header->AllocatedPageCount * _allocator.PageSize;
            return (_totalAllocated, _totalAllocated / (float)totalPagedSize);
        }
    }

    public int Capacity => _header->PageCapacity;
    public int MaxItemCountPerPage => _entriesPerPage;
    public int PageSize => _allocator.PageSize;

    public int RootPageId { get; }

    #endregion

    #region Methods

    public static MappedAppendCollection<T> Create(IPageAllocator allocator, int pageCapacity) => new(allocator, pageCapacity, true);
    public static MappedAppendCollection<T> Map(IPageAllocator allocator, int rootPageId) => new(allocator, rootPageId, false);

    public void Dispose()
    {
        
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public MemorySegment<T> Get(int id, int length)
    {
        var res = GetLocation(id);
        var off = res.pageIndex == 0 ? _rootPageOffsetToData : 0;
        return new MemorySegment<T>(_baseAddress + _pageDirectory[res.pageIndex] + off + res.offsetInPage * sizeof(T), length);
    }

    public ref TN Get<TN>(int nodeId) where TN : unmanaged
    {
        var res = GetLocation(nodeId);
        var off = res.pageIndex == 0 ? _rootPageOffsetToData : 0;
        return ref Unsafe.AsRef<TN>(_baseAddress + _pageDirectory[res.pageIndex] + off + res.offsetInPage * sizeof(T));
    }

    public MemorySegment<T> Reserve(int length, out int id)
    {
        if (length > _entriesPerPage)
        {
            ThrowHelper.AppendCollectionItemSetTooBig(length, _entriesPerPage);
        }
        if (_curAddress + length > _endAddress)
        {
            if (_header->AllocatedPageCount == _header->PageCapacity)
            {
                id = -1;
                return MemorySegment<T>.Empty;
            }

            _header->CurOffset += (int)(_endAddress - _curAddress);
            var newPage = _allocator.AllocatePages(1);
            _pageDirectory[_header->AllocatedPageCount++] = newPage.Address - _baseAddress;
            GetBoundariesFromOffset(_header->CurOffset, out _curAddress, out _endAddress);
        }

        _totalAllocated += length * sizeof(T);

        id = _header->CurOffset;
        _header->CurOffset += length;

        var res = new MemorySegment<T>(_curAddress, length);
        _curAddress += length;
        return res;
    }

    public ref TN Reserve<TN>(out int id) where TN : unmanaged
    {
        var sizeTN = sizeof(TN);
        var sizeT = sizeof(T);
        var l = (sizeTN + sizeT - 1) / sizeT;
        return ref Reserve(l, out id).Cast<TN>().AsRef();
    }

    #endregion

    #endregion

    #region Fields

    private readonly IPageAllocator _allocator;
    private readonly byte* _baseAddress;

    private readonly int _entriesPerPage;
    private readonly int _entriesRootPage;
    private readonly Header* _header;
    private readonly long* _pageDirectory;
    private readonly int _pageSize;
    private readonly int _rootPageOffsetToData;
    private T* _curAddress;
    private T* _endAddress;

    private long _totalAllocated;

    #endregion

    #region Constructors

    private MappedAppendCollection(IPageAllocator allocator, int pageCapacityOrRootId, bool create)
    {
        _allocator = allocator;
        _baseAddress = _allocator.BaseAddress;
        _pageSize = _allocator.PageSize;
        MemorySegment rootPage;
        int pageCapacity;
        if (create)
        {
            pageCapacity = pageCapacityOrRootId;

            if (sizeof(Header) + (pageCapacity * sizeof(long)) > _pageSize)
            {
                ThrowHelper.AppendCollectionCapacityTooBig(pageCapacity, (_pageSize-sizeof(Header) / sizeof(long)));
            }

            rootPage = allocator.AllocatePages(1);
            RootPageId = allocator.ToBlockId(rootPage);
            _header = (Header*)rootPage.Address;
            _header->PageCapacity = pageCapacity;
            _header->AllocatedPageCount = 1;
            _header->CurOffset = 0;
            _entriesPerPage = _pageSize / sizeof(T);
            _rootPageOffsetToData = (sizeof(Header) + pageCapacity * sizeof(long)).Pad<T>();
            _entriesRootPage = (_pageSize - _rootPageOffsetToData) / sizeof(T);
            _pageDirectory = (long*)(_header + 1);
            new Span<int>(_pageDirectory, pageCapacity).Clear();
            _pageDirectory[0] = rootPage.Address - _baseAddress;
            _curAddress = (T*)(rootPage.Address + _rootPageOffsetToData);
            _endAddress = (T*)(rootPage.Address + _pageSize);
        }
        else
        {
            RootPageId = pageCapacityOrRootId;
            rootPage = allocator.FromBlockId(RootPageId);
            _header = (Header*)rootPage.Address;
            pageCapacity = _header->PageCapacity;
            _entriesPerPage = _pageSize / sizeof(T);
            _rootPageOffsetToData = (sizeof(Header) + pageCapacity * sizeof(long)).Pad<T>();
            _entriesRootPage = (_pageSize - _rootPageOffsetToData) / sizeof(T);
            _pageDirectory = (long*)(_header + 1);
            _curAddress = _endAddress = null;
            GetBoundariesFromOffset(_header->CurOffset, out _curAddress, out _endAddress);

            _totalAllocated = _header->AllocatedPageCount * _allocator.PageSize;
        }
    }

    #endregion

    #region Private methods

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void GetBoundariesFromOffset(int offset, out T* curAddress, out T* endAddress)
    {
        var res = GetLocation(offset);

        var off = res.pageIndex == 0 ? _rootPageOffsetToData : 0;
        var pageAddress = _baseAddress + _pageDirectory[res.pageIndex];
        curAddress = (T*)(pageAddress + off + res.offsetInPage*sizeof(T));
        endAddress = (T*)(pageAddress + _pageSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining|MethodImplOptions.AggressiveOptimization)]
    private (int pageIndex, int offsetInPage) GetLocation(int offset)
    {
        if (offset < _entriesRootPage)
        {
            return (0, offset);
        }
        else
        {
            var res = Math.DivRem(offset - _entriesRootPage, _entriesPerPage);
            return (res.Quotient + 1, res.Remainder);
        }
    }

    #endregion

    #region Inner types

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public int PageCapacity;
        public int AllocatedPageCount;
        public int CurOffset;
        private readonly int _padding0;
    }

    #endregion
}