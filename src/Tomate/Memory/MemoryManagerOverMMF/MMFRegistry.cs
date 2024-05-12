using System.IO.MemoryMappedFiles;
using Serilog;

namespace Tomate;

// MemoryMappedFileOverMMF allows multiple executables to share the same MMF for inter-process communication purpose.
// For a given MMF, each executable has its own base address, if we want to store structured data inside an MMF that is shared among multiple processes, we
//  can't use addresses, so we will rely on offsets, relative to the start of the MMF file.
// The MemorySegment type either store an address for non MMF or an ID identifying the MMF and the offset.
//
// So we need a centralized resource, OS wide, that acts as a registry of all the MMF we're managing, each one with a unique ID (which has to be an index for
//  fast access)
// The purpose of this class is to centralize, through a MMF file on its own (called MMFRegistry), these MMF files being registered.
// When an MMF file will be created, we'll use the registry to register it and get its ID, and we'll remove it from the registry upon deletion.
//
// Structure of the registry
//  - A bitmap that tracks free ID (these are indices after all).
//  - For each index, we'll have a corresponding string that will contain the file pathname of the corresponding MMF file, we'll use that for occasional
//     sweeping operations to check if some file were not incorrectly released (if an entry is taken and the corresponding file does not exist anymore, we can
//     release the entry.
//
// The file that is storing the MMF Registry is stored in a operating-system wide folder and will persist through time, 1 MiB should not hurt that much.

internal unsafe class MMFRegistry : IDisposable
{
    #region Constants

    private const int BitmapSizeInByte = BitmapSizeInLong * sizeof(ulong);
    private const int BitmapSizeInLong = (MMFEntryCapacity + 7) / sizeof(ulong);

    private static readonly int FileHeaderSize = sizeof(FileHeader).Pad16();

    private const uint FileMagic = 0x524D4D54;
    private static readonly object Lock = new ();

    private const int MMFEntryCapacity = 1024;
    private const string MMFName = "Tomate.MMF.Registry";
    private static readonly int StringTableSize = MMFEntryCapacity * sizeof(String256);

    #endregion

    #region Public APIs

    #region Methods

    public void Dispose()
    {
        lock (Lock)
        {
            if (--_refCounter > 0)
            {
                return;
            }
            
            try
            {
                _logger?.Verbose($"Disposing the MMF registry");
                if (_rootAddr != null)
                {
                    _view.SafeMemoryMappedViewHandle.ReleasePointer();
                    _rootAddr = null;
                }
                
                _view.Dispose();
                _mmf.Dispose();
                _view = null;
                _mmf = null;
                _singleton = null;
                _logger?.Verbose($"MMF registry disposed");
            }
            catch (Exception e)
            {
                _logger?.Fatal(e, "Unexpected exception occured");
                throw;
            }
        }
    }

    public static MMFRegistry GetMMFRegistry()
    {
        lock (Lock)
        {
            if (_singleton != null)
            {
                _singleton.AddRef();
                return _singleton;
            }

            _singleton = new MMFRegistry();
            return _singleton;
        }
    }

    #endregion

    #endregion

    #region Constructors

    private MMFRegistry()
    {
        _logger = Log.ForContext(typeof(MMFRegistry));
        try
        {
            // Get/create the MMF file that will store session information and addresses of the MMFs used by all executables
            var filePathName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), $"{MMFName}.bin");
            _logger?.Verbose("Using MMF registry file stored to : {FilePathName}", filePathName);
            var fileSize = ComputeFileSize();
            _mmf = MemoryMappedFile.CreateFromFile(filePathName, FileMode.OpenOrCreate, MMFName, fileSize);
            _view = _mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.ReadWrite);

            // Get addresses of the registry MMF
            var viewHandle = _view.SafeMemoryMappedViewHandle;
            byte* addr = null;
            viewHandle.AcquirePointer(ref addr);
            _header = (FileHeader*)addr;

            // Initialize the file if new
            if (_header->Magic == 0)
            {
                new Span<byte>(addr, fileSize).Clear();
                _header->Magic = FileMagic;
                _header->EntryCount = MMFEntryCapacity;
                _header->OffsetToBitmap = FileHeaderSize;
                _header->OffsetToStringTable = _header->OffsetToBitmap + BitmapSizeInByte;
            }

            _bitmapAddress = (ulong*)(addr + _header->OffsetToBitmap);
            _stringTable = (String256*)(addr + _header->OffsetToStringTable);
            _rootAddr = addr;

            // Allocate the table that will contain the MMF base address for each entry of the registry, we want this memory block to have a fixed address and
            //  the widest lifetime, so we rely on the GlobalInstance of the Memory Manager
            var block = DefaultMemoryManager.GlobalInstance.Allocate(sizeof(void*) * _header->EntryCount);
            MMFAddressTable = (void**)block.MemorySegment.Address;

            // We close the MMF when the process exits
            AppDomain.CurrentDomain.ProcessExit += (_,_) => Dispose();

            _refCounter = 1;
            _logger?.Verbose($"Successfully opened the MMF registry file");
        }
        catch (Exception e)
        {
            _logger?.Fatal(e, "Unexpected exception occured");
            throw;
        }
    }

    #endregion

    #region Internals

    internal static void** MMFAddressTable;

    internal int RegisterMMF(string filePathName, void* mmfBaseAddress)
    {
        lock (Lock)
        {
            // Disposed?
            if (_rootAddr == null)
            {
                return -1;
            }

            var bitmap = new Span<ulong>(_bitmapAddress, BitmapSizeInLong);
            var id = bitmap.FindFreeBitConcurrent();
            if (id == -1)
            {
                _logger?.Error("No more free ID available in the MMF registry");
                return -1;
            }

            _logger?.Verbose("Registering MMF file {FilePath} with ID {ID}", filePathName, id);
            String256.Map(filePathName, &_stringTable[id]);
            MMFAddressTable[id] = mmfBaseAddress;
            return id;
        }
    }

    #endregion

    #region Privates

    private static MMFRegistry _singleton;

    private static int ComputeFileSize()
    {
        return FileHeaderSize + BitmapSizeInByte + StringTableSize;
    }

    private readonly ulong* _bitmapAddress;
    private readonly FileHeader* _header;

    private readonly ILogger _logger;
    private readonly String256* _stringTable;
    private MemoryMappedFile _mmf;
    private int _refCounter;

    private byte* _rootAddr;
    private MemoryMappedViewAccessor _view;

    private void AddRef()
    {
        _refCounter++;
    }

    #endregion

    #region Inner types

    private struct FileHeader
    {
        public uint Magic;
        public MappedExclusiveAccessControl AccessControl;
        public int EntryCount;
        public int OffsetToBitmap;
        public int OffsetToStringTable;
    }

    #endregion
}