using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Tomate;

/// <summary>
/// The UnmanagedDataStore allows to store Unmanaged collections through an undetermined period of time and/or shared users. 
/// </summary>
/// <remarks>
/// <para>
/// Read the overview documentation to get a better understanding of how fits the store in the whole picture. Each <see cref="IMemoryManager"/> implementation
///  has a store, you may also want to create your own store based on your criteria.
/// </para>
/// <para>
/// Design and implementation notes:
/// The mission of the store is to store the Unmanaged Collection instances in a memory space that allows the user to access them as a ref of T.
/// Each instance is identified by a handle, which is guaranteed to know if the instance it points to is still valid or not.
///
/// Implementation wise:
///  - The handle is composed of 4 parts: the index of the entry, the generation of the entry, the type of the entry and where it is stored.
///  - The type is registered in the <see cref="InternalTypeManager"/> type, each type has an unmanaged size which determine in which level it will be stored.
///  - There are 8 levels of storage, each level has a different size of entry, starting at 16 bytes and incrementing by 16 bytes. Each unmanaged collection
///     use a predetermined level of storage, based on its size, which is given by the <see cref="InternalTypeManager"/>.
///  - Each level has a <see cref="ConcurrentBitmapL4"/> that tracks which entry is used or not, storage of instances is using a page approach, each page
///     being able to store x instances, we add a new page when the current ones are full. There is a limit of pages per level, which is determined at the
///     construction of the store.
///  - It is important to note that different types can use the same storage level, so they share the limit, you are not guaranteed to have a fixed limit of
///     x instance for a given unmanaged collection. 
/// </para>
/// </remarks>

[PublicAPI]
public unsafe struct UnmanagedDataStore : IDisposable
{
    #region Constants

    // Each unmanaged collection is taking x bytes to store, we round this per 16bytes and the first level starts at 32, so 8 levels is 144 bytes for the last one 
    private const int MaxLevelCount = 8;

    #endregion

    #region Public APIs

    #region Properties

    /// <summary>
    /// Returns <c>true</c> if the instance is a default (or disposed, there's no distinction) one, <c>false</c> if it's a valid one.
    /// </summary>
    public bool IsDefault => _allocator == null;

    /// <summary>
    /// Returns <c>true</c> if the instance is disposed (or defaut, there's no distinction), <c>false</c> if it's a valid one.
    /// </summary>
    public bool IsDisposed => _allocator == null;

    #endregion

    #region Methods

    /// <summary>
    /// Compute the size and requirement to create an instance of a store.
    /// </summary>
    /// <param name="allocator">The allocator that will be used to store the store's own internal data. Collections stored in the store can use another one</param>
    /// <param name="maxEntryCountPerLevel">The desired maximum of entries that can be allocated for each store level, see remarks.</param>
    /// <returns>A tuple where the first item is the size of the MemorySegment to use for the store allocation itself, the second item contains the information
    /// to pass during the store's construction.</returns>
    /// <remarks>
    /// The store is used to store different type of Unmanaged Collection, each type (an instance of the struct itself) has a specific size, the store will
    ///  identify which store level to use based on this size. There are 8 predefined levels, each level has a different size of entry, starting at 16 bytes
    ///  and incremented by 16 bytes for the next level. <paramref name="maxEntryCountPerLevel"/> defines the maximum number of entries that can be stored in
    ///  a specific level. Beware that multiple types will have their instance stored in the same level, so this limit is shared between them. 
    /// </remarks>
    public static (int requiredSize, (int PageCount, int EntryCountPerPage)[] pageCountPerLevel) ComputeStorageSize(IPageAllocator allocator, 
        int maxEntryCountPerLevel)
    {
        var pageSize = allocator.PageSize;
        var size = sizeof(StoreHeader) + sizeof(LevelHeader) * MaxLevelCount;
        var pageInfos = new (int PageCount, int EntryCountPerPage)[MaxLevelCount];
        var curEntrySize = 16;
        for (var i = 0; i < MaxLevelCount; i++, curEntrySize += 16)
        {
            var entryCountPerPage = ComputeEntryCountPerPage(pageSize, curEntrySize);
            var pageCount = (int)Math.Ceiling(maxEntryCountPerLevel / (double)(entryCountPerPage));
            size += pageCount * sizeof(PageInfo);
            pageInfos[i].PageCount = pageCount;
            pageInfos[i].EntryCountPerPage = entryCountPerPage;
        }

        return (size, pageInfos);
    }

    /// <summary>
    /// Create a new instance of the store.
    /// </summary>
    /// <param name="pageAllocator">
    /// The allocator that will be used to allocated internal data structures that will hold the instances.
    /// Must be the same allocator used when calling <see cref="ComputeStorageSize"/>.
    /// </param>
    /// <param name="dataStoreRoot">The data segment store the store, its size is the first item of the tuple returned by <see cref="ComputeStorageSize"/></param>
    /// <param name="pageCountPerLevel">The internal specification of each level to allocate, this is the second item of the tuple returned by <see cref="ComputeStorageSize"/></param>
    /// <returns>The store</returns>
    public static UnmanagedDataStore Create(IPageAllocator pageAllocator, MemorySegment dataStoreRoot, (int pageCount, int entryCountPerPage)[] pageCountPerLevel)
    {
        return new UnmanagedDataStore(pageAllocator, dataStoreRoot, pageCountPerLevel, true);
    }

    /// <summary>
    /// Map an existing instance of the store.
    /// </summary>
    /// <param name="pageAllocator">The allocator that is storing the store's internal data</param>
    /// <param name="dataStoreRoot">The memory segment containing the store root data (dataStoreRoot of <see cref="Create"/>).</param>
    /// <returns></returns>
    public static UnmanagedDataStore Map(IPageAllocator pageAllocator, MemorySegment dataStoreRoot)
    {
        return new UnmanagedDataStore(pageAllocator, dataStoreRoot, null, false);
    }

    /// <summary>
    /// Store an instance of an unmanaged collection in the store.
    /// </summary>
    /// <param name="instance">The instance to store, see remarks.</param>
    /// <typeparam name="T">The type of the instance.</typeparam>
    /// <returns>The handle pointing to the stored instance</returns>
    /// <remarks>
    /// It's critical to understand that the struct instance will be copied (duplicated) to the store, you should no longer use <paramref name="instance"/>
    /// after this call. This operation will call <see cref="IRefCounted.AddRef()"/> on the instance to extend the lifetime, a corresponding
    /// <see cref="IDisposable.Dispose()"/> will be called during <see cref="Remove{T}"/>. The lifetime of <paramref name="instance"/> is not
    /// affected by this call.
    /// You can retrieve the instance back by calling <see cref="Get{T}(Tomate.UnmanagedDataStore.Handle{T})"/> with the handle returned by this call.
    /// </remarks>
    public Handle<T> Store<T>(ref T instance) where T : unmanaged, IUnmanagedCollection
    { 
        var typeInfo = InternalTypeManager.RegisterType<T>();
        var pageInfos = _pageInfos;
        ref var header = ref _storeHeader[0];
        ref var levelInfo = ref header.LevelHeaders[typeInfo.StorageIndex];
        var basePageIndex = levelInfo.IndexToFirstPageInfo;
        
        Retry:
        
        var curPageIndex = levelInfo.CurLookupPageIndex;
        if (curPageIndex == -1)
        {
            AllocPage(ref header, ref levelInfo);
            curPageIndex = levelInfo.CurLookupPageIndex;
        }
        
        ref var pageInfo = ref pageInfos[basePageIndex + curPageIndex];
        var pageIndex = pageInfo.Bitmap.AllocateBits(1);
        int fullIndex;
        
        // Allocation in this page succeed, return the global index
        if (pageIndex != -1)
        {
            fullIndex = pageInfo.BaseIndex + pageIndex;
            goto Epilogue;
        }
        
        // Allocation in this page failed, we retry an allocation from the page after the current, wrapping up to the current
        ++curPageIndex;
        for (int i = 0; i < levelInfo.PageInfoCount - 1; i++)                    // -1 because we start after the current one, so there's one less to test
        {
            var ii = (curPageIndex + i) % levelInfo.PageInfoCount;
            pageIndex = pageInfos[basePageIndex + ii].Bitmap.AllocateBits(1);
            if (pageIndex != -1)
            {
                levelInfo.CurLookupPageIndex = ii;
                fullIndex = pageInfos[basePageIndex + ii].BaseIndex + pageIndex;
                goto Epilogue;
            }
        }
        
        // Creating a new page is a rare event, rely on an interprocess access control
        AllocPage(ref header, ref levelInfo);
        
        // In any case, retry the allocation
        goto Retry;
        
        Epilogue:
        Debug.Assert(fullIndex != -1);
        
        var entry = GetEntry(ref levelInfo, fullIndex);
        
        // Take ownership of the memory block
        instance.MemoryBlock.AddRef();
        
        // That's the weird part, as the instance as an unmanaged struct, we can copy its content to another location (inside the store) by doing a plain byte
        //  copy. From now on, the instance will live in this location and users will be able to access it as a reference, through the handle.
        new Span<byte>(Unsafe.AsPointer(ref instance), typeInfo.TypeSelfSize).CopyTo(entry);

        // Update the header of the entry, which is located at the 4 last bytes
        var entryHeader = entry.Slice(-4).Cast<ushort>();
        entryHeader[0] = typeInfo.TypeIndex;
        entryHeader[1]++;
        
        return new Handle<T>(fullIndex, entryHeader[1], typeInfo.TypeIndex, (byte)typeInfo.StorageIndex);
    }

    /// <summary>
    /// Retrieve and instance from its handle.
    /// </summary>
    /// <param name="handle">The handle of the instance to retrieve</param>
    /// <typeparam name="T">The type of the Unmanaged Collection to retrieve</typeparam>
    /// <returns>A reference to the Unmanaged Collection or a null ref if the handle is no longer valid, use <see cref="Unsafe.IsNullRef{T}(ref readonly T)"/>
    /// to determine it.</returns>
    /// <remarks>
    /// The <see cref="UnmanagedDataStore.Handle{T}"/> version of this method is for convenience, it infers the type of the instance to retrieve from the handle.
    /// Relying on <see cref="Get{T}(Tomate.UnmanagedDataStore.Handle)"/> has the same effect, depending on your needs, it may be more convenient to rely on
    /// the typeless version or generic version of the Handle type.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ref T Get<T>(Handle<T> handle) where T : unmanaged, IUnmanagedCollection
    {
        var index = handle.Index;
        ref var levelInfo = ref _levelInfos[handle.StoreIndex];
        
        var entrySegment = GetEntry(ref levelInfo, index);
        var entryHeader = entrySegment.Slice(-4).Cast<ushort>();
        var entryType = entryHeader[0];
        var entryGeneration = entryHeader[1];

        if (entryGeneration != handle.Generation)
        {
            return ref Unsafe.NullRef<T>();
        }
        Debug.Assert(entryType == handle.TypeId, "Handle has a different type than the entry");

        return ref Unsafe.AsRef<T>(entrySegment.Address);
    }

    /// <summary>
    /// Retrieve and instance from its handle (typeless version).
    /// </summary>
    /// <param name="handle">The typeless version of the handle used to access the instance</param>
    /// <typeparam name="T">The type of the instance to retrive</typeparam>
    /// <returns>A reference to the Unmanaged Collection or a null ref if the handle is no longer valid, use <see cref="Unsafe.IsNullRef{T}(ref readonly T)"/>
    /// to determine it.</returns>
    /// <remarks>
    /// The <see cref="UnmanagedDataStore.Handle"/> version of this method is dealing with a typeless handle, which might be easier in some cases to store
    /// beforehand. 
    /// Relying on <see cref="Get{T}(Tomate.UnmanagedDataStore.Handle{T})"/> has the same effect, depending on your needs, it may be more convenient to rely on
    /// the typeless version or generic version of the Handle type.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public ref T Get<T>(Handle handle) where T : unmanaged, IUnmanagedCollection
    {
        var index = handle.Index;
        ref var levelInfo = ref _levelInfos[handle.StoreIndex];
        
        var entrySegment = GetEntry(ref levelInfo, index);
        var entryHeader = entrySegment.Slice(-4).Cast<ushort>();
        var entryType = entryHeader[0];
        var entryGeneration = entryHeader[1];
        if (entryGeneration != handle.Generation)
        {
            return ref Unsafe.NullRef<T>();
        }
        Debug.Assert(entryType == handle.TypeId, "Handle has a different type than the entry");

        return ref Unsafe.AsRef<T>(entrySegment.Address);
    }

    /// <summary>
    /// Remove an instance from the store.
    /// </summary>
    /// <param name="handle">The handle of the instance to remove.</param>
    /// <typeparam name="T">The type of the instance to remove</typeparam>
    /// <returns>Returns <c>true</c> if the instance was removed successfully and also disposed. Will return <c>false</c> is the instance was successfully
    /// removed from the store but still lives (because its ReferenceCounter was not 1).</returns>
    /// <exception cref="InvalidHandleException">is thrown if the handle no longer points to a valid instance in the store.</exception>
    public bool Remove<T>(Handle<T> handle) where T : unmanaged, IUnmanagedCollection
    {
        // Get the type and the level where the instance is stored
        var typeInfo = InternalTypeManager.GetType(handle.TypeId);
        ref var levelInfo = ref _levelInfos[typeInfo.StorageIndex];
        
        // Get the instance's entry
        var entry = GetEntry(ref levelInfo, handle.Index, out var pageIndex, out var indexInPage);
        var entryHeader = entry.Slice(-4).Cast<ushort>();
        var generation = entryHeader[1];
        
        // Check if the handle is valid
        if (generation != handle.Generation)
        {
            ThrowHelper.InvalidHandle();
        }
    
        // Dispose the instance
        ref var instance = ref Unsafe.AsRef<T>(entry.Address);
        var refCounter = instance.RefCounter;
        instance.Dispose();

        // Clear the memory area where the instance as well as its type was stored, it's not mandatory (well except for the type), but cleaner this way
        entry.Slice(0, -2).ToSpan<byte>().Clear();

        // Increment the generation field, handles still existing will become invalid
        entryHeader[1]++;

        // Free the slot
        _pageInfos[levelInfo.IndexToFirstPageInfo + pageIndex].Bitmap.FreeBits(indexInPage, 1);

        // Notify the caller if the instance was freed or not
        return refCounter == 1;
    }
    
    /// <summary>
    /// Retrieve the maximum instance count that can be stored for the given type
    /// </summary>
    /// <typeparam name="T">The type</typeparam>
    /// <returns>The maximum number, see remarks.</returns>
    /// <remarks>
    /// Several type (generic and specified or not) may share the same storage level, so the limit returned is shared between them.
    /// </remarks>
    public int GetMaxEntryCount<T>() where T : unmanaged, IUnmanagedCollection
    {
        var typeInfo = InternalTypeManager.RegisterType<T>();
        ref var levelInfo = ref _levelInfos[typeInfo.StorageIndex];
        return levelInfo.PageInfoLength * levelInfo.EntryCountPerPageInfo;
    }

    /// <summary>
    /// Dispose the store and free all the resources.
    /// </summary>
    /// <remarks>
    /// The instances stored are disposed and will be freed if there's no other reference to them.
    /// </remarks>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        var allocator = _allocator;
        foreach (ref var pageInfo in _pageInfos)
        {
            if (pageInfo.PageId == 0)
            {
                continue;
            }

            // Ok, so this is nasty, but we don't have much more of a choice.
            // To make this working EVERY instance stored in the store must have a MemoryBlock as its first field.
            var view = new MemoryView<byte>(pageInfo.Entries.Cast<byte>());
            var skipSize = pageInfo.EntrySize - sizeof(MemoryBlock);
            while (view.IsEndReached == false)
            {
                ref var memoryBlock = ref view.Fetch<MemoryBlock>();
                if (memoryBlock.IsDefault == false)
                {
                    memoryBlock.Dispose();
                }

                view.Skip(skipSize);
            }
            
            var pageSegment = allocator.FromBlockId(pageInfo.PageId);
            allocator.FreePages(pageSegment);
            pageInfo.PageId = 0;
        }
        
        _pageAllocatorId = 0;
    }

    #endregion

    #endregion

    #region Constructors

    private UnmanagedDataStore(IPageAllocator pageAllocator, MemorySegment memorySegment, (int PageCount, int EntryCountPerPage)[] levelsInfo, bool create)
    {
        var pageSize = pageAllocator.PageSize;
        var isMMF = pageAllocator is MemoryManagerOverMMF;
        
        if (create)
        {
            // The given segment is split into two parts: the header and storing all the PageInfo we can on the remaining part
            var (headerSegment, restSegment) = memorySegment.Split<StoreHeader, byte>(sizeof(StoreHeader));
            var (levelHeadersSegment, pageInfosSegment) = restSegment.Split<LevelHeader, PageInfo>(sizeof(LevelHeader) * MaxLevelCount);
            
            Debug.Assert(levelsInfo.Sum(l => l.PageCount) <= pageInfosSegment.Length);

            _pageAllocatorId = pageAllocator.PageAllocatorId;
            
            ref var header = ref headerSegment[0];
            header.LevelHeaders = levelHeadersSegment;
            header.PageInfos = pageInfosSegment;

            var indexToFirstPageInfo = 0;
            var levelEntrySize = 16;
            for (int i = 0; i < MaxLevelCount; i++, levelEntrySize += 16)
            {
                ref var levelHeader = ref levelHeadersSegment[i];
                var levelInfo = levelsInfo[i];

                // Setup header
                levelHeader.CurLookupPageIndex = -1;
                levelHeader.LastPageInfoIndex = -1;
                levelHeader.PageInfoCount = 0;
                levelHeader.PageInfoLength = levelInfo.PageCount;
                levelHeader.PageInfoBitmapSize = ConcurrentBitmapL4.ComputeRequiredSize(levelInfo.EntryCountPerPage);
                levelHeader.EntrySize = levelEntrySize;
                levelHeader.EntryCountPerPageInfo = levelInfo.EntryCountPerPage;
                levelHeader.AccessControl = new MappedExclusiveAccessControl();
                levelHeader.IndexToFirstPageInfo = indexToFirstPageInfo;
                
                indexToFirstPageInfo += levelInfo.PageCount;
            }

            _storeHeader = headerSegment;
            _pageInfos = pageInfosSegment;
            _levelInfos = levelHeadersSegment;
        }
        else
        {
            // // The given segment is split into two parts: the header and storing all the PageInfo we can on the remaining part
            // var (headerSegment, pageInfosSegment) = memorySegment.Split<Header, PageInfo>(sizeof(Header));
            // _header = headerSegment.Address;
            // _pageInfos = pageInfosSegment.Address;
            // _pageAllocatorId = pageAllocator.PageAllocatorId;
        }
    }

    #endregion

    #region Privates

    private static int ComputeEntryCountPerPage(int pageSize, int entrySize)
    {
        // Compute how many items can fit in one page size
        // Algo is incredibly stupid, but fast enough...
        // ReSharper disable once NotAccessedVariable
        var iteration = 0;
        var entryCountPerPage = pageSize / entrySize;
        var bitmapSize = ConcurrentBitmapL4.ComputeRequiredSize(entryCountPerPage);
        while (bitmapSize.Pad(entrySize) + (entryCountPerPage * entrySize) > pageSize)
        {
            --entryCountPerPage;
            bitmapSize = ConcurrentBitmapL4.ComputeRequiredSize(entryCountPerPage);
            iteration++;
        }

        return entryCountPerPage;
    }

    private int _pageAllocatorId;
    private IPageAllocator _allocator => IPageAllocator.GetPageAllocator(_pageAllocatorId);
    private readonly MemorySegment<StoreHeader> _storeHeader;
    private readonly MemorySegment<LevelHeader> _levelInfos;
    private readonly MemorySegment<PageInfo> _pageInfos;

    private void AllocPage(ref StoreHeader header, ref LevelHeader levelInfo)
    {
        var curPageInfoCount = levelInfo.PageInfoCount;
        levelInfo.AccessControl.TakeControl();
        try
        {
            // Race condition detected, another thread already allocated a new page, just quit
            if (levelInfo.PageInfoCount != curPageInfoCount)
            {
                return;
            }
            if (levelInfo.PageInfoCount == levelInfo.PageInfoLength)
            {
                ThrowHelper.ItemMaxCapacityReachedException(levelInfo.PageInfoLength * levelInfo.EntryCountPerPageInfo);
            }
            var newPageSegment = _allocator.AllocatePages(1);
            if (newPageSegment.IsDefault)
            {
                throw new OutOfMemoryException();
            }
            newPageSegment.Cast<byte>().ToSpan().Clear();

            var curPageIndex = levelInfo.PageInfoCount;
            ref var pageInfo = ref header.PageInfos[levelInfo.IndexToFirstPageInfo + curPageIndex];
            pageInfo.PageId = _allocator.ToBlockId(newPageSegment);
            pageInfo.EntrySize = levelInfo.EntrySize;
            pageInfo.Bitmap = ConcurrentBitmapL4.Create(levelInfo.EntryCountPerPageInfo, newPageSegment.Slice(0, levelInfo.PageInfoBitmapSize));
            pageInfo.BaseIndex = curPageIndex * levelInfo.EntryCountPerPageInfo;
            pageInfo.Entries = newPageSegment.Slice(levelInfo.PageInfoBitmapSize.Pad(levelInfo.EntrySize));
            pageInfo.BaseIndex = levelInfo.EntryCountPerPageInfo * curPageIndex;
            Debug.Assert(pageInfo.Entries.Length >= levelInfo.EntryCountPerPageInfo * levelInfo.EntrySize, 
                $"Can't store {levelInfo.EntryCountPerPageInfo} entries of size {levelInfo.EntrySize} in a page of size {newPageSegment.Length}, Check you use the appropriate allocator.");
            
            levelInfo.PageInfoCount++;
            levelInfo.LastPageInfoIndex++;
            levelInfo.CurLookupPageIndex = levelInfo.LastPageInfoIndex;
        }
        finally
        {
            levelInfo.AccessControl.ReleaseControl();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private MemorySegment GetEntry(ref LevelHeader levelInfo, int index) => GetEntry(ref levelInfo, index, out _, out _);

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private MemorySegment GetEntry(ref LevelHeader levelInfo, int index, out int pageIndex, out int indexInPage)
    {
        Debug.Assert(index >= 0, $"Index can't be a negative number, {index} is incorrect.");

        var entrySize = levelInfo.EntrySize;
        
        // Fast path
        var pageInfos = _pageInfos;
        if (index < levelInfo.EntryCountPerPageInfo)
        {
            pageIndex = 0;
            indexInPage = index;
            return pageInfos[levelInfo.IndexToFirstPageInfo].Entries.Slice(index * entrySize, entrySize);
        }
    
        {
            pageIndex = Math.DivRem(index, levelInfo.EntryCountPerPageInfo, out indexInPage);
            Debug.Assert(pageIndex < levelInfo.PageInfoCount, $"Requested item at index {index} is out of bounds");
            return pageInfos[levelInfo.IndexToFirstPageInfo + pageIndex].Entries.Slice(indexInPage * entrySize, entrySize);
        }
    }

    #endregion

    #region Inner types

    /// <summary>
    /// Handle to an instance stored in the store
    /// </summary>
    /// <remarks>
    /// This is the non-generic version of <see cref="UnmanagedDataStore.Handle{T}"/>, there is no difference between the two, the generic version allows
    ///  type inference when calling <see cref="Get{T}(Tomate.UnmanagedDataStore.Handle{T})"/>, which is more convenient.
    /// But if you need to store many handles of different types in a collection, you may want to use this version.
    /// </remarks>
    [PublicAPI]
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct Handle
    {
        internal Handle(int index, ushort generation, ushort typeId, byte storeIndex)
        {
            Debug.Assert(storeIndex < 8, "StoreIndex can't be greater than 7");
            Debug.Assert(index <= 1<<29);
            
            _indexAndStoreIndex = (uint)(index<<3 | storeIndex);
            _generation = generation;
            _typeId = typeId;

        }
        #region Public APIs

        #region Properties

        public bool IsDefault => Generation == 0 && Index == 0;

        #endregion

        #endregion

        #region Internals

        internal int Index => (int)(_indexAndStoreIndex >> 3);
        internal ushort Generation => _generation;
        internal ushort TypeId => _typeId;
        internal byte StoreIndex => (byte)(_indexAndStoreIndex & 0x7);
        
        #endregion

        private readonly uint _indexAndStoreIndex;
        private readonly ushort _generation;
        private readonly ushort _typeId;

    }

    /// <summary>
    /// Handle to an instance stored in the store
    /// </summary>
    /// <remarks>
    /// This is the generic version of <see cref="UnmanagedDataStore.Handle"/>, there is no difference between the two, the generic version allows
    ///  type inference when calling <see cref="Get{T}(Tomate.UnmanagedDataStore.Handle{T})"/>, which is more convenient.
    /// But if you need to store many handles of different types in a collection, you may want to use <see cref="UnmanagedDataStore.Handle"/>.
    /// </remarks>
    [PublicAPI]
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct Handle<T> where T : unmanaged, IUnmanagedCollection
    {
        #region Public APIs

        #region Properties

        public bool IsDefault => Generation == 0 && Index == 0;

        #endregion

        #region Methods

        public static implicit operator Handle<T>(Handle h)
        {
            return new Handle<T>(h.Index, h.Generation, h.TypeId, h.StoreIndex);
        }

        public static implicit operator Handle(Handle<T> h)
        {
            return new Handle(h.Index, h.Generation, h.TypeId, h.StoreIndex);
        }

        #endregion

        #endregion

        #region Constructors

        internal Handle(int index, ushort generation, ushort typeId, byte storeIndex)
        {
            Debug.Assert(storeIndex < 8, "StoreIndex can't be greater than 7");
            Debug.Assert(index <= 1<<29);
            
            _indexAndStoreIndex = (uint)(index<<3 | storeIndex);
            _generation = generation;
            _typeId = typeId;

        }

        #endregion

        #region Internals

        internal int Index => (int)(_indexAndStoreIndex >> 3);
        internal ushort Generation => _generation;
        internal ushort TypeId => _typeId;
        internal byte StoreIndex => (byte)(_indexAndStoreIndex & 0x7);

        #endregion

        #region Privates

        private readonly uint _indexAndStoreIndex;
        private readonly ushort _generation;
        private readonly ushort _typeId;

        #endregion
    }

    private struct StoreHeader
    {
        public MemorySegment<LevelHeader> LevelHeaders;
        public MemorySegment<PageInfo> PageInfos;
    }

    private struct LevelHeader
    {
        #region Fields

        public MappedExclusiveAccessControl AccessControl;
        public int CurLookupPageIndex;
        public int EntrySize;
        public int EntryCountPerPageInfo;
        public int LastPageInfoIndex;
        public int PageInfoBitmapSize;
        public int PageInfoCount;
        public int PageInfoLength;
        public int IndexToFirstPageInfo;

        #endregion
    }

    private struct PageInfo
    {
        #region Fields

        public int BaseIndex;
        public int PageId;
        public ConcurrentBitmapL4 Bitmap;
        public MemorySegment Entries;
        public int EntrySize;

        #endregion
    }

    #endregion
}

internal static class InternalTypeManager
{
    #region Constants

    private static readonly ConcurrentDictionary<Type, TypeInfo> IndexByType = new();
    private static readonly ConcurrentDictionary<int, TypeInfo> TypeByIndex = new();

    #endregion

    #region Internals

    internal static TypeInfo GetType(ushort typeId)
    {
        TypeByIndex.TryGetValue(typeId, out var type);
        return type;
    }

    internal static TypeInfo RegisterType<T>()
    {
        Debug.Assert(_curIndex < ushort.MaxValue, $"there are too many (more than {ushort.MaxValue}) type registered");
        return IndexByType.GetOrAdd(typeof(T), _ =>
        {
            var index =  (ushort)Interlocked.Increment(ref _curIndex);

            // 4 is the size of the header for each entry (ushort generation, ushort typeId) 
            var typeSelfSize = Unsafe.SizeOf<T>();
            var typeSize = (typeSelfSize + 4).Pad16();
            var storageIndex = (ushort)((typeSize / 16) - 1);

            var typeInfo = new TypeInfo(index, storageIndex, (ushort)typeSelfSize);
            TypeByIndex.TryAdd(index, typeInfo);

            return typeInfo;
        });
    }

    #endregion

    #region Privates

    private static int _curIndex = -1;

    #endregion

    #region Inner types

    internal readonly struct TypeInfo(ushort index, ushort storageIndex, ushort typeSelfSize)
    {
        public readonly ushort TypeIndex = index;
        public readonly ushort StorageIndex = storageIndex;
        public readonly ushort TypeSelfSize = typeSelfSize;
    }

    #endregion
}
