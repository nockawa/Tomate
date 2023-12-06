using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Tomate;

// Beware this structure is mapped in the MMF and accessed by C++ so definition and alignment must match its C++ counterpart
[StructLayout(LayoutKind.Sequential)]
internal struct RootHeader
{
    public int PageSize;
    public int PageCapacity;
    public int MaxConcurrencyCount;
    public int OffsetSessionInfo;
    public int MaxSessionCount;
    public int SessionInfoLock;
    public int SessionCount;
    public int OffsetPageBitfield;
    public int PageBitfieldSize;
    public int OffsetPageDirectory;
    public int PageDirectorySize;
    public int OffsetBlockAllocators;
    public int BlockAllocatorsSize;
    public int OffsetUserData;
    public int UserDataSize;
    public int AllocatorRobinCounter;
};

[PublicAPI]
public unsafe partial class MemoryManagerOverMMF : IMemoryManager, IPageAllocator, IDisposable
{
    #region Constants

    public static readonly int MinSegmentSize = 16;

    #endregion

    #region Public APIs

    #region Properties

    public int AllocatedPageCount => _allocatedPageCount;

    public byte* BaseAddress { get; }
    public byte* EndAddress { get; }

    public bool IsDisposed => _mmf == null;
    public int MaxAllocationLength { get; }
    public int MemoryManagerId { get; }

    public string MMFFilePathName { get; }

    public string MMFName { get; }

    public long MMFSize { get; }
    public int PageAllocatorId { get; }
    public int PageSize { get; }

    public MemorySegment UserDataArea => _rootAddr == null ? MemorySegment.Empty : new MemorySegment(_rootAddr + _header.AsRef().OffsetUserData, _header.AsRef().UserDataSize);
    public DefaultMemoryManager.DebugMemoryInit MemoryBlockContentCleanup { get; set; }

    public DefaultMemoryManager.DebugMemoryInit MemoryBlockContentInitialization { get; set; }

    #endregion

    #region Methods

    public static MemoryManagerOverMMF Create(CreateSettings settings)
    {
        try
        {
            return new MemoryManagerOverMMF(settings, FileMode.CreateNew);
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
            return new MemoryManagerOverMMF(new CreateSettings(mmfFilePathName, mmfName), default);
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
            return new(new CreateSettings(null, mmfName), default);
        }
        catch (MemoryManagerOverMMFCreateException)
        {
            return null;
        }
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

    public MemorySegment AllocatePages(int blockSize)
    {
        Debug.Assert(blockSize <= 64, "Maximum block size is 64");

        var bitIndex = _pageBitfield.ToSpan().FindFreeBitsConcurrent(blockSize);
        if (bitIndex == -1) return MemorySegment.Empty;

        Interlocked.Add(ref _allocatedPageCount, blockSize);
        _pageDirectory[bitIndex].Pack((short)blockSize, 1);
        return new MemorySegment(_rootAddr + (bitIndex * (long)PageSize), blockSize * PageSize);
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IMemoryManager.UnregisterMemoryManager(MemoryManagerId);
        IPageAllocator.UnregisterPageAllocator(PageAllocatorId);

        Trace.Assert(UnregisterSession(IProcessProvider.Singleton.CurrentProcessId));

        long maxSize = 0;
        if (_compactOnFinalClose)
        {
            var bitIndex = _pageBitfield.ToSpan().FindMaxBitSet();
            maxSize = (long)PageSize * (bitIndex + 1);
        }
        if (_rootAddr != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _rootAddr = null;
        }
        _view.Flush();
        this.DisposeAndNull(ref _view);
        this.DisposeAndNull(ref _mmf);

        if (_compactOnFinalClose)
        {
            using var fs = File.Open(MMFFilePathName, FileMode.Open, FileAccess.ReadWrite);
            fs.SetLength(maxSize);
        }

        MMFRangeByMMF.Remove(this);
    }

    public bool FreePages(MemorySegment segment)
    {
        var blockId = (int)(((long)segment.Address - (long)_rootAddr) / PageSize);
        if (blockId >= _pageDirectory.Length) return false;
        if ((_pageDirectory[blockId] & 0xFFFF) == 0) return false;

        var val = Interlocked.Decrement(ref _pageDirectory[blockId]);
        if ((val & 0xFFFF) == 0)
        {
            if (RandomizeContentOnFree)
            {
                var start = (int*)segment.Address;
                var end = (int*)segment.End;

                while (start + 4 <= end)
                {
                    start[0] = QuickRand.Next();
                    start[1] = QuickRand.Next();
                    start[2] = QuickRand.Next();
                    start[3] = QuickRand.Next();
                    start += 4;
                }
                while (start + 1 <= end)
                {
                    *start++ = QuickRand.Next();
                }
            }
            var pageSize = val.HighS();
            Interlocked.Add(ref _allocatedPageCount, -pageSize);
            _pageBitfield.ToSpan().ClearBitsConcurrent(blockId, pageSize);
            _pageDirectory[blockId] = 0;
        }

        return true;
    }

    public MemorySegment FromBlockId(int blockId) =>
        blockId < 0 || blockId >= _pageDirectory.Length
            ? MemorySegment.Empty
            : new MemorySegment(_rootAddr + (PageSize * (long)blockId), _pageDirectory[blockId].HighS() * PageSize);

    public int ToBlockId(MemorySegment segment) => segment.IsDefault ? -1 : (int)((segment.Address - _rootAddr) / PageSize);

    #endregion

    #endregion

    #region Fields

    internal static Dictionary<MemoryManagerOverMMF, ValueTuple<long, long>> MMFRangeByMMF = new();

    // Each thread of each process will be assigned one of the allocator referenced in this array, in order to reduce contention
    //  (each allocator has its own locking mechanism)
    private readonly MemorySegment<int> _allocators;
    private readonly ThreadLocal<int> _assignedAllocator;
    private readonly bool _compactOnFinalClose;
    private readonly MemorySegment<RootHeader> _header;
    private readonly MemorySegment<ulong> _pageBitfield;
    private readonly MemorySegment<uint> _pageDirectory;
    private readonly MemorySegment<int> _sessions;
    private int _allocatedPageCount;

    private MemoryMappedFile _mmf;

    private byte* _rootAddr;
    private MemoryMappedViewAccessor _view;

    internal DebugData DebugInfo;

    public bool RandomizeContentOnFree;

    #endregion

    #region Constructors

    private MemoryManagerOverMMF(CreateSettings settings, FileMode fileMode)
    {
        if ((settings.FilePathName == null) && (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) == false))
        {
            throw new MemoryManagerOverMMFCreateException(
                "Memory-only Memory Mapped File is only supposed on Windows OS. This platform doesn't support it, use a file-backed Memory Mapped File instead.",
                null);
        }

        MMFFilePathName = settings.FilePathName;
        MMFName = settings.Name;
        PageSize = settings.PageSize;                   // Can be 0 at this point if we open an existing MMF
        var fileSize = settings.FileSize;
        try
        {
            // File-backed
            if (MMFFilePathName != null)
            {
                _mmf = MemoryMappedFile.CreateFromFile(MMFFilePathName, fileMode, MMFName, fileSize);
                _compactOnFinalClose = settings.CompactOnFinalClose;
            }

            // Memory-only
            else
            {
#pragma warning disable CA1416 // Validate platform compatibility
                _mmf = fileSize != 0 ? MemoryMappedFile.CreateOrOpen(MMFName, fileSize) : MemoryMappedFile.OpenExisting(MMFName);
#pragma warning restore CA1416 // Validate platform compatibility
            }
        }
        catch (Exception e)
        {
            throw new MemoryManagerOverMMFCreateException($"Couldn't create/open the memory-mapped-file named '{MMFName}' stored at '{MMFFilePathName}'", e);
        }

        _view = _mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.ReadWrite);
        MMFSize = (long)_view.SafeMemoryMappedViewHandle.ByteLength;       // Set the size from the view because that's the only way if we open an existing MMF

        int pageCapacity;
        int pageBitfieldSize;

        var viewHandle = _view.SafeMemoryMappedViewHandle;
        viewHandle.AcquirePointer(ref _rootAddr);
        _header = new MemorySegment<RootHeader>(_rootAddr, 1);

        // If the MMF is new PageCapacity is uninitialized, otherwise the MMF was already created by another process
        ref var h = ref _header.AsRef();
        Debug.Assert(settings.IsCreate == (h.PageCapacity==0), 
            settings.IsCreate ? "Error, the MMF File already exists, can't create it" : "Error, can't open the MMF file, it doesn't exist");
        
        // New MMF
        if (h.PageCapacity == 0)
        {
            pageCapacity = (int)(MMFSize / PageSize);
            pageBitfieldSize = ((pageCapacity + 63) / 64);
            var pageDirectorySize = pageCapacity * sizeof(int);

            new Span<byte>(_rootAddr, PageSize).Clear();
            h.PageSize = PageSize;
            h.PageCapacity = pageCapacity;
            h.OffsetSessionInfo = 512;
            h.MaxSessionCount = settings.MaxSessionCount;
            h.MaxConcurrencyCount = settings.MaxConcurrencyCount;

            h.OffsetPageBitfield = h.OffsetSessionInfo + sizeof(int) * h.MaxSessionCount;
            h.PageBitfieldSize = pageBitfieldSize * sizeof(ulong);
            
            h.OffsetPageDirectory = h.OffsetPageBitfield + h.PageBitfieldSize;
            h.PageDirectorySize = pageDirectorySize;

            h.OffsetBlockAllocators = h.OffsetPageDirectory + h.PageDirectorySize;
            h.BlockAllocatorsSize = sizeof(int) * h.MaxConcurrencyCount;
            
            h.OffsetUserData = h.OffsetBlockAllocators + h.BlockAllocatorsSize;
            Debug.Assert(h.OffsetUserData < PageSize, $"With this configuration PageSize must be at least {h.OffsetUserData} bytes");
            h.UserDataSize = PageSize - h.OffsetUserData;
        }

        // Existing MMF
        else
        {
            PageSize = h.PageSize;
            pageCapacity = (int)(MMFSize / PageSize);
            pageBitfieldSize = ((pageCapacity + 63) / 64);
        }

        _assignedAllocator = new ThreadLocal<int>(() =>
        {
            ref var header = ref _header.AsRef();
            var v = Interlocked.Increment(ref header.AllocatorRobinCounter);
            return _allocators[v % header.MaxConcurrencyCount];
        });
        
        MemoryManagerId = IMemoryManager.RegisterMemoryManager(this);
        PageAllocatorId = IPageAllocator.RegisterPageAllocator(this);
        BaseAddress = _rootAddr;
        EndAddress = _rootAddr + MMFSize;
        MMFRangeByMMF.Add(this, ((long)BaseAddress, (long)EndAddress));

        _sessions = new MemorySegment<int>(_rootAddr + h.OffsetSessionInfo, sizeof(int) * h.MaxSessionCount);
        _pageBitfield  = new MemorySegment<ulong>(_rootAddr + h.OffsetPageBitfield, pageBitfieldSize);
        _pageDirectory = new MemorySegment<uint>(_rootAddr + h.OffsetPageDirectory, pageCapacity);
        _allocators = new MemorySegment<int>(_rootAddr + h.OffsetBlockAllocators, h.MaxConcurrencyCount);

        ReservePage(0, 1, -1);                                   // the first page is storing the header and all root level information, so we mark it as reserved
        ReservePage(pageCapacity, (short)(64 - (pageCapacity % 64)), -1); // Make sure we can't allocate pages that are not there but the mask would allow us to take

        Trace.Assert(RegisterSession(IProcessProvider.Singleton.CurrentProcessId));
    }

    #endregion

    #region Internals

    #region Internals methods

    internal static MemoryManagerOverMMF GetMMFFromRange(long address)
    {
        foreach (var kvp in MMFRangeByMMF)
        {
            if ((address >= kvp.Value.Item1) && (address < kvp.Value.Item2))
            {
                return kvp.Key;
            }
        }

        return null;
    }

    #endregion

    #endregion

    #region Private methods

    private void LockSessionInfo(int processId)
    {
        ref var h = ref _header.AsRef();
        if (Interlocked.CompareExchange(ref h.SessionInfoLock, processId, 0) != 0)
        {
            var sw = new SpinWait();
            while (Interlocked.CompareExchange(ref h.SessionInfoLock, processId, 0) != 0)
            {
                sw.SpinOnce();
            }
        }
    }

    private bool RegisterSession(int processId)
    {
        // Acquire lock
        LockSessionInfo(processId);

        ref var h = ref _header.AsRef();
        // Max session count reached
        if (h.SessionCount == h.MaxSessionCount)
        {
            return false;
        }
    
        // Register the session
        ++h.SessionCount;
        
        // Find a free entry
        var sessions = _sessions.Address;
        var registered = false;
        for (var i = 0; i < h.MaxSessionCount; i++)
        {
            if (sessions[i] != 0)
            {
                continue;
            }
                
            sessions[i] = processId;
            registered = true;
            break;
        }
        Debug.Assert(registered, "Shouldn't happen");
        UnlockSessionInfo(processId);
        return true;
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

    private void UnlockSessionInfo(int processId)
    {
        ref var h = ref _header.AsRef();
        Interlocked.CompareExchange(ref h.SessionInfoLock, 0, processId);
    }

    private bool UnregisterSession(int processId)
    {
        // Acquire lock
        LockSessionInfo(processId);

        ref var h = ref _header.AsRef();
        Debug.Assert(h.SessionCount > 0, "Shouldn't happen");
    
        // Unregister the session
        --h.SessionCount;
    
        // Find a free entry
        var sessions = _sessions.Address;
        var unregistered = false;
        for (var i = 0; i < h.MaxSessionCount; i++)
        {
            if (sessions[i] != processId)
            {
                continue;
            }
            sessions[i] = 0;
            unregistered = true;
            break;
        }
        Debug.Assert(unregistered, "Shouldn't happen");

        UnlockSessionInfo(processId);
        return true;
    }

    #endregion
}