using System.Runtime.CompilerServices;

namespace Tomate;

public unsafe struct AppendCollection<T> : IDisposable where T : unmanaged
{
    private readonly IPageAllocator _allocator;
    private readonly Header* _header;

    private readonly int _entriesPerPage;
    private readonly int _entriesRootPage;
    private readonly long* _pageDirectory;
    private readonly byte* _baseAddress;
    private readonly int _pageSize;
    private readonly int _rootPageOffsetToData;
    private T* _curAddress;
    private T* _endAddress;

    public int RootPageId { get; }

    public static AppendCollection<T> Create(IPageAllocator allocator, int pageCapacity) => new(allocator, pageCapacity, true);
    public static AppendCollection<T> Map(IPageAllocator allocator, int rootPageId) => new(allocator, rootPageId, false);

    private struct Header
    {
        public int PageCapacity;
        public int AllocatedPageCount;
        public int CurOffset;
    }

    private AppendCollection(IPageAllocator allocator, int pageCapacityOrRootId, bool create)
    {
        _allocator = allocator;
        _baseAddress = _allocator.BaseAddress;
        _pageSize = _allocator.PageSize;
        MemorySegment rootPage;
        int pageCapacity;
        if (create)
        {
            pageCapacity = pageCapacityOrRootId;
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
        }
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

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void GetBoundariesFromOffset(int offset, out T* curAddress, out T* endAddress)
    {
        var res = GetLocation(offset);

        var off = res.pageIndex == 0 ? _rootPageOffsetToData : 0;
        var pageAddress = _baseAddress + _pageDirectory[res.pageIndex];
        curAddress = (T*)(pageAddress + off + res.offsetInPage*sizeof(T));
        endAddress = (T*)(pageAddress + _pageSize);
    }

    public MemorySegment<T> Reserve(int length, out int id)
    {
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

        id = _header->CurOffset;
        _header->CurOffset += length;

        var res = new MemorySegment<T>(_curAddress, length);
        _curAddress += length;
        return res;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public MemorySegment<T> Get(int id, int length)
    {
        var res = GetLocation(id);
        var off = res.pageIndex == 0 ? _rootPageOffsetToData : 0;
        return new MemorySegment<T>(_baseAddress + _pageDirectory[res.pageIndex] + off + res.offsetInPage * sizeof(T), length);
    }

    public ref TN Reserve<TN>(out int id) where TN : unmanaged
    {
        var sizeTN = sizeof(TN);
        var sizeT = sizeof(T);
        var l = (sizeTN + sizeT - 1) / sizeT;
        return ref Reserve(l, out id).Cast<TN>().AsRef();
    }

    public ref TN Get<TN>(int nodeId) where TN : unmanaged
    {
        var res = GetLocation(nodeId);
        var off = res.pageIndex == 0 ? _rootPageOffsetToData : 0;
        return ref Unsafe.AsRef<TN>(_baseAddress + _pageDirectory[res.pageIndex] + off + res.offsetInPage * sizeof(T));
    }

    public void Dispose()
    {
        
    }
}