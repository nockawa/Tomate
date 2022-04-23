using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Tomate;

// Beware this structure is mapped in the MMF and accessed by C++ so definition and alignment must match its C++ counterpart
[StructLayout(LayoutKind.Sequential)]
public struct RootHeader
{
    public int PageSize;
    public int PageCapacity;
    public int OffsetPageBitfield;
    public int PageBitfieldSize;
    public int OffsetPageDirectory;
    public int PageDirectorySize;
    public int OffsetUserData;
    public int UserDataSize;
};

public class MemoryManagerOverMMFCreateException : Exception
{
    public MemoryManagerOverMMFCreateException(string msg, Exception innerException) : base(msg, innerException) { }
}

public unsafe class MemoryManagerOverMMF : IPageAllocator, IDisposable
{
    public string MMFFilePathName => _mmfFilePathName;
    public string MMFName => _mmfName;
    public long MMFSize => _mmfSize;
    public byte* BaseAddress { get; }
    public int PageSize => _pageSize;

    public int AllocatedPageCount => _allocatedPageCount;

    public MemorySegment UserDataArea => _rootAddr == null ? MemorySegment.Empty : new MemorySegment(_rootAddr + _header.AsRef().OffsetUserData, _header.AsRef().UserDataSize);

    private MemoryMappedFile _mmf;
    private readonly string _mmfFilePathName;
    private readonly string _mmfName;
    private readonly long _mmfSize;
    private readonly int _pageSize;
    private MemoryMappedViewAccessor _view;

    private byte* _rootAddr;
    private readonly MemorySegment<RootHeader> _header;
    private readonly MemorySegment<ulong> _pageBitfield;
    private readonly MemorySegment<uint> _pageDirectory;
    private int _allocatedPageCount;

    public bool IsDisposed => _mmf == null;

    public static MemoryManagerOverMMF Create(string mmfFilePathName, string mmfName, long mmfSize, int pageSize)
    {
        try
        {
            return new MemoryManagerOverMMF(mmfFilePathName, mmfName, mmfSize, FileMode.CreateNew, pageSize);
        }
        catch (MemoryManagerOverMMFCreateException)
        {
            return null;
        }
    }

    public static MemoryManagerOverMMF Open(string mmfFilePathName, string mmfName)
    {
        try
        {
            return new MemoryManagerOverMMF(mmfFilePathName, mmfName, 0, FileMode.Open, 0);
        }
        catch (MemoryManagerOverMMFCreateException)
        {
            return null;
        }
    }

    public static MemoryManagerOverMMF OpenExisting(string mmfName)
    {
        try
        {
            return new(null, mmfName, 0, FileMode.Open, 0);
        }
        catch (MemoryManagerOverMMFCreateException)
        {
            return null;
        }
    }

    private MemoryManagerOverMMF(string mmfFilePathName, string mmfName, long mmfSize, FileMode fileMode, int pageSize)
    {
        if ((mmfFilePathName == null) && (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) == false))
        {
            throw new MemoryManagerOverMMFCreateException(
                "Memory-only Memory Mapped File is only supposed on Windows OS. This platform doesn't support it, use a file-backed Memory Mapped File instead.",
                null);
        }

        _mmfFilePathName = mmfFilePathName;
        _mmfName = mmfName;
        _pageSize = pageSize;                   // Can be 0 at this point if we open an existing MMF
        try
        {
            // File-backed
            if (mmfFilePathName != null)
            {
                _mmf = MemoryMappedFile.CreateFromFile(_mmfFilePathName, fileMode, _mmfName, mmfSize);
            }

            // Memory-only
            else
            {
#pragma warning disable CA1416 // Validate platform compatibility
                if (mmfSize != 0)
                {
                    _mmf = MemoryMappedFile.CreateOrOpen(_mmfName, mmfSize);
                }
                else
                {
                    _mmf = MemoryMappedFile.OpenExisting(_mmfName);
                }
#pragma warning restore CA1416 // Validate platform compatibility
            }
        }
        catch (Exception e)
        {
            throw new MemoryManagerOverMMFCreateException($"Couldn't create/open the memory-mapped-file named '{_mmfName}' stored at '{_mmfFilePathName}'", e);
        }

        _view = _mmf.CreateViewAccessor(0, mmfSize, MemoryMappedFileAccess.ReadWrite);
        _mmfSize = (long)_view.SafeMemoryMappedViewHandle.ByteLength;       // Set the size from the view because that's the only way if we open an existing MMF

        int pageCapacity;
        int pageBitfieldSize;

        var viewHandle = _view.SafeMemoryMappedViewHandle;
        viewHandle.AcquirePointer(ref _rootAddr);
        _header = new MemorySegment<RootHeader>(_rootAddr, 1);

        // If the MMF is new PageCapacity is uninitialized, otherwise the MMF was already created by another process
        ref var h = ref _header.AsRef();
        if (h.PageCapacity == 0)
        {
            pageCapacity = (int)(_mmfSize / _pageSize);
            pageBitfieldSize = ((pageCapacity + 63) / 64);
            var pageDirectorySize = pageCapacity * sizeof(int);

            new Span<byte>(_rootAddr, _pageSize).Clear();
            h.PageSize = pageSize;
            h.PageCapacity = pageCapacity;
            h.OffsetPageBitfield = 512;
            h.PageBitfieldSize = pageBitfieldSize * sizeof(ulong);
            h.OffsetPageDirectory = h.OffsetPageBitfield + h.PageBitfieldSize;
            h.PageDirectorySize = pageDirectorySize;
            h.OffsetUserData = h.OffsetPageDirectory + h.PageDirectorySize;
            h.UserDataSize = _pageSize - h.OffsetUserData;
        }

        // Existing MMF
        else
        {
            _pageSize = h.PageSize;
            pageCapacity = (int)(_mmfSize / _pageSize);
            pageBitfieldSize = ((pageCapacity + 63) / 64);
        }

        BaseAddress = _rootAddr;

        _pageBitfield  = new MemorySegment<ulong>(_rootAddr + h.OffsetPageBitfield, pageBitfieldSize);
        _pageDirectory = new MemorySegment<uint>(_rootAddr + h.OffsetPageDirectory, pageCapacity);

        ReservePage(0, 1, -1);                                  // the first page is storing the header and all root level information, so we mark it as reserved
        ReservePage(pageCapacity, (short)(64 - (pageCapacity% 64)), -1); // Make sure we can't allocate pages that are not there but the mask would allow us to take
    }

    private bool ReservePage(int pageIndex, short pageSize, short initialCounter)
    {
        if (pageIndex < 0 || pageIndex >= _pageDirectory.Length)
        {
            return false;
        }

        if (_pageBitfield.ToSpan().SetBitsConcurrent(pageIndex, pageSize) == false)
        {
            return false;
        }
        _pageDirectory[pageIndex].Pack(pageSize, initialCounter);

        return true;
    }

    public MemorySegment AllocatePages(int blockSize)
    {
        Debug.Assert(blockSize <= 64, "Maximum block size is 64");

        var bitIndex = _pageBitfield.ToSpan().FindFreeBitsConcurrent(blockSize);
        if (bitIndex == -1) return MemorySegment.Empty;

        Interlocked.Add(ref _allocatedPageCount, blockSize);
        _pageDirectory[bitIndex].Pack((short)blockSize, 1);
        return new MemorySegment(_rootAddr + (bitIndex * (long)_pageSize), blockSize * _pageSize);
    }

    public bool FreePages(MemorySegment segment)
    {
        int blockId = (int)(((long)segment.Address - (long)_rootAddr) / _pageSize);
        if (blockId >= _pageDirectory.Length) return false;
        if ((_pageDirectory[blockId] & 0xFFFF) == 0) return false;

        var val = Interlocked.Decrement(ref _pageDirectory[blockId]);
        if ((val & 0xFFFF) == 0)
        {
            var pageSize = val.HighS();
            Interlocked.Add(ref _allocatedPageCount, -pageSize);
            _pageBitfield.ToSpan().ClearBitsConcurrent(blockId, pageSize);
            _pageDirectory[blockId] = 0;
        }

        return true;
    }

    public int AddRef(MemorySegment memorySegment)
    {
        var blockId = ToBlockId(memorySegment);
        if (blockId == -1)
        {
            return -1;
        }

        return Interlocked.Increment(ref _pageDirectory[blockId]).Low();
    }

    public int ToBlockId(MemorySegment segment) => segment.IsEmpty ? -1 : (int)((segment.Address - _rootAddr) / _pageSize);

    public MemorySegment FromBlockId(int blockId) =>
        blockId < 0 || blockId >= _pageDirectory.Length
            ? MemorySegment.Empty
            : new MemorySegment(_rootAddr + (_pageSize * (long)blockId), _pageDirectory[blockId].HighS() * _pageSize);

    public void Clear()
    {
        throw new NotImplementedException();
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        if (_rootAddr != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _rootAddr = null;
        }
        _view.Flush();
        this.DisposeAndNull(ref _view);
        this.DisposeAndNull(ref _mmf);
    }
}