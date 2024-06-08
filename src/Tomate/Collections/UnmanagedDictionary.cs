using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;
#pragma warning disable CS9087 // This returns a parameter by reference but it is not a ref parameter
#pragma warning disable CS9084 // Struct member returns 'this' or other instance members by reference

namespace Tomate;

/// <summary>
/// An unmanaged implementation of a dictionary
/// </summary>
/// <typeparam name="TKey">Type of the key</typeparam>
/// <typeparam name="TValue">Type of the value</typeparam>
/// <remarks>
/// This implementation is a big copy/paste of the .net Dictionary{TKey, TValue} class and adapted to this usage.
/// This type is MemoryMappedFile friendly, meaning you can allocate instances of this type with an <see cref="MemoryManagerOverMMF"/> as memory manager.
/// This comes with a limitation where this type can't store addresses, because a MMF is a cross process object and each process has its own address space.
/// The result is a degraded performance, you can alleviate this by relying on the <see cref="Accessor"/> type when you have multiple operations to perform.
/// Another limitation is this type can't store custom comparer, some APIs will use the default one (like the Subscript operator), other will take one
///  as an argument (like the <see cref="TryGetValue"/> method). You are supposed to use the same comparer for all operations, to make things easier, you
///  should rely on <see cref="Accessor"/> which will take the comparer at construction.
/// </remarks>
[PublicAPI]
public unsafe struct UnmanagedDictionary<TKey, TValue> : IDisposable where TKey : unmanaged where TValue : unmanaged
{
    #region Constants

    private const int StartOfFreeList = -3;

    #endregion

    #region Public APIs

    #region Properties

    /// <summary>
    /// Get/Set the value for the given key
    /// </summary>
    /// <param name="key">The key to look for or set</param>
    /// <remarks>
    /// If the key doesn't exist, the getter will throw an exception, the setter will add a new entry.
    /// This API is not the fastest, if you have multiple operations to perform, you should rely on the <see cref="Accessor"/> type.
    /// It also will rely on the default comparer, if you need to use a custom one, you should use the <see cref="Accessor"/> type.
    /// </remarks>
    public TValue this[TKey key]
    {
        get
        {
            var value = new Accessor(ref this, null).FindValue(key, out var res);
            if (res)
            {
                return value;
            }

            ThrowHelper.KeyNotFound(key);
            return default;
        }
        set
        {
            new Accessor(ref this, null).TryInsert(key, value, InsertionBehavior.OverwriteExisting, out var modified);
            Debug.Assert(modified);
        }
    }

    /// <summary>
    /// Get the item count
    /// </summary>
    /// <returns>
    /// The number of items in the list or -1 if the instance is invalid.
    /// </returns>
    public int Count => _header!=null ? (_header->Count - _header->FreeCount) : -1;

    /// <summary>
    /// Check if the instance is the default (not constructed) one
    /// </summary>
    /// <remarks>
    /// A default instance can't be used, some APIs will assert in debug mode and crash in release mode, others will throw an exception or return -1.
    /// There is no distinction between a default instance and a disposed one.
    /// </remarks>
    public bool IsDefault => _memoryBlock.IsDefault;

    /// <summary>
    /// Check if the instance is disposed
    /// </summary>
    /// <remarks>
    /// A disposed instance can't be used, some APIs will assert in debug mode and crash in release mode, others will throw an exception or return -1.
    /// There is no distinction between a default instance and a disposed one.
    /// </remarks>
    public bool IsDisposed => _memoryBlock.IsDefault;

    #endregion

    #region Methods

    /// <summary>
    /// Create a new instance
    /// </summary>
    /// <param name="memoryManager">
    /// The memory manager to use, if <c>null</c>, the global one will be used (see <see cref="DefaultMemoryManager.GlobalInstance"/>)
    /// </param>
    /// <param name="capacity">The initial capacity</param>
    /// <returns>The created instance</returns>
    public static UnmanagedDictionary<TKey, TValue> Create(IMemoryManager memoryManager=null, int capacity = 11) => new(memoryManager, capacity, true);

    /// <summary>
    /// Add a new entry to the dictionary given the key and its corresponding value
    /// </summary>
    /// <param name="key">The key of the entry must be unique, will throw <exception cref=""></exception></param>
    /// <param name="value"></param>
    /// <param name="comparer"></param>
    public void Add(TKey key, TValue value, IEqualityComparer<TKey> comparer = null) => new Accessor(ref this, comparer).TryInsert(key, value, InsertionBehavior.ThrowOnExisting, out _);

    /// <summary>
    /// Dispose the instance, see remarks
    /// </summary>
    /// <remarks>
    /// This call will decrement the reference counter by 1 and the instance will effectively be disposed if it reaches 0, otherwise it will still be usable.
    /// </remarks>
    public void Dispose()
    {
        if (IsDefault || IsDisposed)
        {
            return;
        }
        _memoryBlock.Dispose();
        _memoryBlock = default;
    }

    /// <summary>
    /// Ensures that the dictionary can hold up to 'capacity' entries without any further expansion of its backing storage
    /// </summary>
    public int EnsureCapacity(int capacity) => new Accessor(ref this, null).EnsureCapacity(capacity);

    /// <summary>
    /// Construct a fast accessor, which is the preferred way if there are multiple operations on the dictionary to be done.
    /// </summary>
    /// <param name="comparer">Custom comparer to use with the dictionary operations, <c>null</c> to rely on the default one.</param>
    /// <returns>The constructed accessor</returns>
    /// <remarks>
    /// The accessor will fetch the addresses to work with, which gives an additional performance boost when multiple operations are to perform.
    /// As it is a ref struct, it can't be stored in a field, so you must use it in the same method where you get it.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public Accessor FastAccessor(IEqualityComparer<TKey> comparer = null) => new(ref this, comparer);

    /// <summary>
    /// Enumerator access for <c>foreach</c>
    /// </summary>
    /// <returns>The enumerator, an instance of the <see cref="Enumerator"/> type</returns>
    /// <remarks>
    /// BEWARE: the enumerator will rely on the default comparer, if you want a custom one, you have to rely on <see cref="Accessor"/> instead.
    /// </remarks>
    public Enumerator GetEnumerator() => new(this, null);

    /// <summary>
    /// Get the value for the given key or add a new entry if it doesn't exist
    /// </summary>
    /// <param name="key">The key of the element to get or add</param>
    /// <param name="found">
    /// If <c>true</c> the element with this key already exists. If <c>false</c> there was no element for the given key and we've added one.
    /// </param>
    /// <param name="comparer">The comparer to use with this operation, <c>null</c> will be the default one</param>
    /// <returns>
    /// The value of the element corresponding to the given key
    /// </returns>
    public TValue GetOrAdd(TKey key, out bool found, IEqualityComparer<TKey> comparer = null) => new Accessor(ref this, comparer).TryInsert(key, default, InsertionBehavior.GetExisting, out found);

    /// <summary>
    /// Remove the entry of the given key from the dictionary
    /// </summary>
    /// <param name="key">The key of the entry to remove</param>
    /// <param name="value">If the entry was successfully removed, will contain the corresponding value, otherwise will contain <c>default</c>.</param>
    /// <param name="comparer">The comparer to use with this operation, <c>null</c> will be the default one</param>
    /// <returns></returns>
    public bool Remove(TKey key, out TValue value, IEqualityComparer<TKey> comparer = null) => new Accessor(ref this, comparer).Remove(key, out value);

    /// <summary>
    /// Try to add a new entry, if the key already exists, the operation will return <c>false</c>
    /// </summary>
    /// <param name="key">The key of the entry to add</param>
    /// <param name="value">The corresponding value</param>
    /// <param name="comparer">The comparer to use with this operation, <c>null</c> will be the default one</param>
    /// <returns>
    /// Returns <c>true</c> is the entry was successfully added, <c>false</c> if we couldn't add it because there's already another with the same key
    /// </returns>
    public bool TryAdd(TKey key, TValue value, IEqualityComparer<TKey> comparer = null)
    {
        new Accessor(ref this, comparer).TryInsert(key, value, InsertionBehavior.None, out var res);
        return res;
    }

    /// <summary>
    /// Try to get the value corresponding to the given key
    /// </summary>
    /// <param name="key">The key of the element to access its value from.</param>
    /// <param name="value">The value corresponding to the key</param>
    /// <param name="comparer">The comparer to use with this operation, <c>null</c> will be the default one</param>
    /// <returns>
    /// Will return <c>true</c> if the element was found, <c>false</c> otherwise.
    /// </returns>
    public bool TryGetValue(TKey key, out TValue value, IEqualityComparer<TKey> comparer = null)
    {
        value = new Accessor(ref this, comparer).FindValue(key, out var res);
        return res;
    }

    /// <summary>
    /// Add/set a entry in the dictionary
    /// </summary>
    /// <param name="key">The key of the entry to add or change</param>
    /// <param name="value">The value to add/set</param>
    /// <param name="comparer">The comparer to use with this operation, <c>null</c> will be the default one</param>
    /// <returns>Returns <c>false</c> if a new entry was added, <c>true</c> if an existing one was modified with the new give value.</returns>
    public bool TrySetValue(TKey key, TValue value, IEqualityComparer<TKey> comparer = null)
    {
        new Accessor(ref this, comparer).TryInsert(key, value, InsertionBehavior.OverwriteExisting, out var modified);
        return modified;
    }

    #endregion

    #endregion

    #region Constructors

    private UnmanagedDictionary(IMemoryManager owner, int capacity, bool create)
    {
        Debug.Assert(capacity >= 3, "Capacity must be at least 3 to be valid.");
        owner ??= DefaultMemoryManager.GlobalInstance;
        // _comparer = default;
        // ReSharper disable once PossibleUnintendedReferenceComparison
        // if (comparer is not null && comparer != EqualityComparer<TKey>.Default) // first check for null to avoid forcing default comparer instantiation unnecessarily
        // {
        //     _comparer = comparer;
        // }

        // _buckets = default;
        // _entries = default;

        var size = PrimeHelpers.GetPrime(capacity);
        _memoryBlock = owner.Allocate(sizeof(Header) + size * (sizeof(int) + sizeof(Entry)));
        _memoryBlock.MemorySegment.ToSpan<byte>().Clear();

        var h = _header;
        h->FreeList = -1;
        h->Size = size;

        // var (h, m) = _memoryBlock.MemorySegment.Split(sizeof(Header));
        // _header = (Header*)h.Address;
        // _header->FreeList = -1;
        // _header->Size = size;
        //
        // var (b, e) = m.Split(size * sizeof(int));
        // _buckets = b.Cast<int>();
        // _entries = e.Cast<Entry>();
    }

    #endregion

    #region Inner types

    /// <summary>
    /// Process-dependent accessor to the dictionary, for faster operations
    /// </summary>
    /// <remarks>
    /// The <see cref="UnmanagedDictionary{TKey,TValue}"/> type is a process independent type, meaning its instances can lie inside a MemoryMappedFile and be
    ///  shared across processes. This possibility comes with a constraint to deal with indices rather than addresses, which has a performance cost.
    /// Creating an instance of <see cref="Accessor"/> will give you a faster way to access the dictionary because its implementation deals with addresses rather
    ///  than indices, but it only brings a performance gain if you have multiple operations to perform.
    /// Also note that APIs don't check for the validity of the instance, it's only done during the construction of the accessor.
    /// WARNING: when using APIs of this type, you must NOT perform any operation on the dictionary instance itself (or other instances of this accessor),
    ///  simply because <see cref="Accessor"/> caches the addresses of the dictionary's content and if a resize occurs outside of this instance, the
    ///  consequences will be catastrophic.
    /// </remarks>
    [PublicAPI]
    public ref struct Accessor
    {
        #region Public APIs

        #region Methods

        /// <summary>
        /// Ensures that the dictionary can hold up to 'capacity' entries without any further expansion of its backing storage
        /// </summary>
        public int EnsureCapacity(int capacity)
        {
            if (capacity < 0)
            {
                //TODO Exception
                //ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.capacity);
            }

            var currentCapacity = _entries.Length;
            if (currentCapacity >= capacity)
            {
                return currentCapacity;
            }

            var newSize = PrimeHelpers.GetPrime(capacity);
            Resize(newSize);
            return newSize;
        }

        public Enumerator GetEnumerator() => new(_owner, _comparer);

        /// <summary>
        /// Get the value for the given key or add a new entry if it doesn't exist
        /// </summary>
        /// <param name="key">The key of the element to get or add</param>
        /// <param name="found">
        /// If <c>true</c> the element with this key already exists. If <c>false</c> there was no element for the given key and we've added one.
        /// </param>
        /// <returns>
        /// The value of the element corresponding to the given key
        /// </returns>
        public TValue GetOrAdd(TKey key, out bool found) => TryInsert(key, default, InsertionBehavior.GetExisting, out found);

        public void Add(TKey key, TValue value) => TryInsert(key, value, InsertionBehavior.ThrowOnExisting, out _);
        
        /// <summary>
        /// Remove the entry of the given key from the dictionary
        /// </summary>
        /// <param name="key">The key of the entry to remove</param>
        /// <param name="value">If the entry was successfully removed, will contain the corresponding value, otherwise will contain <c>default</c>.</param>
        /// <returns></returns>
        public bool Remove(TKey key, out TValue value)
        {
            if (_buckets.IsDefault)
            {
                value = default;
                return false;
            }

            Debug.Assert(_entries.IsDefault == false, "entries should be allocated");
            uint collisionCount = 0;
            var hashCode = (uint)(_comparer?.GetHashCode(key) ?? key.GetHashCode());
            ref var bucket = ref GetBucket(hashCode);
            var entries = _entries.ToSpan();
            var last = -1;
            var i = bucket - 1; // Value in buckets is 1-based
            while (i >= 0)
            {
                ref var entry = ref entries[i];

                if (entry.HashCode == hashCode && (_comparer?.Equals(entry.KeyValuePair.Key, key) ?? EqualityComparer<TKey>.Default.Equals(entry.KeyValuePair.Key, key)))
                {
                    if (last < 0)
                    {
                        bucket = entry.Next + 1; // Value in buckets is 1-based
                    }
                    else
                    {
                        entries[last].Next = entry.Next;
                    }

                    value = entry.KeyValuePair.Value;

                    Debug.Assert((StartOfFreeList - _header->FreeList) < 0, "shouldn't underflow because max hashtable length is MaxPrimeArrayLength = 0x7FEFFFFD(2146435069) Freelist underflow threshold 2147483646");
                    entry.Next = StartOfFreeList - _header->FreeList;

                    _header->FreeList = i;
                    _header->FreeCount++;
                    return true;
                }

                last = i;
                i = entry.Next;

                collisionCount++;
                if (collisionCount > (uint)entries.Length)
                {
                    //TODO Exception
                    // The chain of entries forms a loop; which means a concurrent update has happened.
                    // Break out of the loop and throw, rather than looping forever.
                    //ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
                }
            }

            value = default;
            return false;
        }

        /// <summary>
        /// Try to add a new entry, if the key already exists, the operation will return <c>false</c>
        /// </summary>
        /// <param name="key">The key of the entry to add</param>
        /// <param name="value">The corresponding value</param>
        /// <returns>
        /// Returns <c>true</c> is the entry was successfully added, <c>false</c> if we couldn't add it because there's already another with the same key
        /// </returns>
        public bool TryAdd(TKey key, TValue value)
        {
            TryInsert(key, value, InsertionBehavior.None, out var res);
            return res;
        }

        /// <summary>
        /// Try to get the value corresponding to the given key
        /// </summary>
        /// <param name="key">The key of the element to access its value from.</param>
        /// <param name="value">The value corresponding to the key</param>
        /// <returns>
        /// Will return <c>true</c> if the element was found, <c>false</c> otherwise.
        /// </returns>
        public bool TryGetValue(TKey key, out TValue value)
        {
            value = FindValue(key, out var res);
            return res;
        }

        /// <summary>
        /// Add/set a entry in the dictionary
        /// </summary>
        /// <param name="key">The key of the entry to add or change</param>
        /// <param name="value">The value to add/set</param>
        /// <returns>Returns <c>false</c> if a new entry was added, <c>true</c> if an existing one was modified with the new give value.</returns>
        public bool TrySetValue(TKey key, TValue value)
        {
            TryInsert(key, value, InsertionBehavior.OverwriteExisting, out var modified);
            return modified;
        }

        #endregion

        #endregion

        #region Constructors

        /// <summary>
        /// Construct a new instance of the accessor
        /// </summary>
        /// <param name="owner">The dictionary instance to perform the access operations on</param>
        /// <param name="comparer">A custom comparer to use for the operations, <c>null</c> to rely on the default one.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public Accessor(ref UnmanagedDictionary<TKey, TValue> owner, IEqualityComparer<TKey> comparer)
        {
            _owner = ref owner;
            _memoryBlock = ref owner._memoryBlock;
            
            // ReSharper disable once PossibleUnintendedReferenceComparison
            _comparer = default;
            if (comparer is not null && comparer != EqualityComparer<TKey>.Default) // first check for null to avoid forcing default comparer instantiation unnecessarily
            {
                _comparer = comparer;
            }

            var memoryBlock = _owner._memoryBlock;
            var (h, m) = memoryBlock.MemorySegment.Split(sizeof(Header));
            _header = (Header*)h.Address;

            var (b, e) = m.Split(_header->Size * sizeof(int));
            _buckets = b.Cast<int>();
            _entries = e.Cast<Entry>();
        }

        #endregion

        #region Internals

        internal MemorySegment<Entry> _entries;

        internal TValue FindValue(TKey key, out bool found)
        {
            ref var entry = ref Unsafe.NullRef<Entry>();
            if (_buckets.IsDefault == false)
            {
                Debug.Assert(_entries.IsDefault == false, "expected entries to be allocated");
                var comparer = _comparer;
                if (comparer == null)
                {
                    var hashCode = (uint)key.GetHashCode();
                    var i = GetBucket(hashCode);
                    var entries = _entries.ToSpan();
                    uint collisionCount = 0;
                    if (typeof(TKey).IsValueType)
                    {
                        // ValueType: Devirtualize with EqualityComparer<TValue>.Default intrinsic

                        i--; // Value in _buckets is 1-based; subtract 1 from i. We do it here so it fuses with the following conditional.
                        do
                        {
                            // Should be a while loop https://github.com/dotnet/runtime/issues/9422
                            // Test in if to drop range check for following array access
                            if ((uint)i >= (uint)entries.Length)
                            {
                                goto ReturnNotFound;
                            }

                            entry = ref entries[i];
                            if (entry.HashCode == hashCode && EqualityComparer<TKey>.Default.Equals(entry.KeyValuePair.Key, key))
                            {
                                goto ReturnFound;
                            }

                            i = entry.Next;

                            collisionCount++;
                        } while (collisionCount <= (uint)entries.Length);
                    }
                }
                else
                {
                    var hashCode = (uint)comparer.GetHashCode(key);
                    var i = GetBucket(hashCode);
                    var entries = _entries.ToSpan();
                    uint collisionCount = 0;
                    i--; // Value in _buckets is 1-based; subtract 1 from i. We do it here so it fuses with the following conditional.
                    do
                    {
                        // Should be a while loop https://github.com/dotnet/runtime/issues/9422
                        // Test in if to drop range check for following array access
                        if ((uint)i >= (uint)entries.Length)
                        {
                            goto ReturnNotFound;
                        }

                        entry = ref entries[i];
                        if (entry.HashCode == hashCode && comparer.Equals(entry.KeyValuePair.Key, key))
                        {
                            goto ReturnFound;
                        }

                        i = entry.Next;

                        collisionCount++;
                    } while (collisionCount <= (uint)entries.Length);
                }
            }

            goto ReturnNotFound;

    ReturnFound:
            found = true;
            return entry.KeyValuePair.Value;
    ReturnNotFound:
            found = false;
            return default;
        }

        internal TValue TryInsert(TKey key, TValue value, InsertionBehavior behavior, out bool result)
        {
            var entries = _entries.ToSpan();

            var comparer = _comparer;
            var hashCode = (uint)(comparer?.GetHashCode(key) ?? key.GetHashCode());

            uint collisionCount = 0;
            ref var bucket = ref GetBucket(hashCode);
            var i = bucket - 1; // Value in _buckets is 1-based

            if (comparer == null)
            {
                // ValueType: Devirtualize with EqualityComparer<TValue>.Default intrinsic
                while (true)
                {
                    // Should be a while loop https://github.com/dotnet/runtime/issues/9422
                    // Test uint in if rather than loop condition to drop range check for following array access
                    if ((uint)i >= (uint)entries.Length)
                    {
                        break;
                    }

                    if (entries[i].HashCode == hashCode && EqualityComparer<TKey>.Default.Equals(entries[i].KeyValuePair.Key, key))
                    {
                        if (behavior == InsertionBehavior.OverwriteExisting)
                        {
                            entries[i].KeyValuePair.Value = value;
                            result = true;
                            return entries[i].KeyValuePair.Value;
                        }

                        if (behavior == InsertionBehavior.GetExisting)
                        {
                            result = true;
                            return entries[i].KeyValuePair.Value;
                        }

                        if (behavior == InsertionBehavior.ThrowOnExisting)
                        {
                            ThrowHelper.AddingDuplicateKeyException(key, nameof(key));
                        }

                        result = false;
                        return default;
                    }

                    i = entries[i].Next;

                    collisionCount++;
                    if (collisionCount > (uint)entries.Length)
                    {
                        // The chain of entries forms a loop; which means a concurrent update has happened.
                        // Break out of the loop and throw, rather than looping forever.
                        ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
                    }
                }
            }
            else
            {
                while (true)
                {
                    // Should be a while loop https://github.com/dotnet/runtime/issues/9422
                    // Test uint in if rather than loop condition to drop range check for following array access
                    if ((uint)i >= (uint)entries.Length)
                    {
                        break;
                    }

                    if (entries[i].HashCode == hashCode && comparer.Equals(entries[i].KeyValuePair.Key, key))
                    {
                        if (behavior == InsertionBehavior.OverwriteExisting)
                        {
                            entries[i].KeyValuePair.Value = value;
                            result = true;
                            return entries[i].KeyValuePair.Value;
                        }

                        if (behavior == InsertionBehavior.GetExisting)
                        {
                            result = true;
                            return entries[i].KeyValuePair.Value;
                        }

                        if (behavior == InsertionBehavior.ThrowOnExisting)
                        {
                            //TODO Exception
                            //ThrowHelper.ThrowAddingDuplicateWithKeyArgumentException(key);
                        }

                        result = false;
                        return default;
                    }

                    i = entries[i].Next;

                    collisionCount++;
                    if (collisionCount > (uint)entries.Length)
                    {
                        //TODO Exception
                        // The chain of entries forms a loop; which means a concurrent update has happened.
                        // Break out of the loop and throw, rather than looping forever.
                        //ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
                    }
                }
            }

            int index;
            if (_header->FreeCount > 0)
            {
                index = _header->FreeList;
                _header->FreeList = StartOfFreeList - entries[_header->FreeList].Next;
                _header->FreeCount--;
            }
            else
            {
                var count = _header->Count;
                if (count == entries.Length)
                {
                    Resize();
                    bucket = ref GetBucket(hashCode);
                }
                index = count;
                _header->Count = count + 1;
                entries = _entries.ToSpan();
            }

            ref var entry = ref entries[index];
            entry.HashCode = hashCode;
            entry.Next = bucket - 1; // Value in _buckets is 1-based
            entry.KeyValuePair.Key = key;
            entry.KeyValuePair.Value = value;
            bucket = index + 1; // Value in _buckets is 1-based

            result = behavior != InsertionBehavior.GetExisting;
            return entry.KeyValuePair.Value;
        }

        #endregion

        #region Privates

        private readonly ref UnmanagedDictionary<TKey,TValue> _owner;
        private MemorySegment<int> _buckets;

        private IEqualityComparer<TKey> _comparer;
        private Header* _header;
        private ref MemoryBlock _memoryBlock;

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private ref int GetBucket(uint hashCode)
        {
            var buckets = _buckets.ToSpan();
            return ref buckets[(int)(hashCode % (uint)buckets.Length)];
        }

        private void Resize() => Resize(PrimeHelpers.ExpandPrime(_header->Count));

        // Must execute under exclusive lock
        private void Resize(int newSize)
        {
            // Value types never rehash
            Debug.Assert(_entries.IsDefault == false, "_entries should be allocated");
            Debug.Assert(newSize >= _entries.Length);

            // Allocate the data buffer for the new size, this buffer contains space for the header, buckets and entries
            var newDataBlock = _memoryBlock.MemoryManager.Allocate(sizeof(Header) + newSize * (sizeof(int) + sizeof(Entry)));
        
            // Copy the header to the new header and replace the old by the new
            var (h, m) = newDataBlock.MemorySegment.Split(sizeof(Header));
            new Span<Header>(_header, 1).CopyTo(h.ToSpan<Header>());
            _header = (Header*)h.Address;
            _header->Size = newSize;

            var (bms, ems) = m.Split(newSize * sizeof(int));
            var entries = ems.Cast<Entry>();

            var count = _header->Count;
            _entries.ToSpan().CopyTo(entries.ToSpan());
            entries.ToSpan()[count..].Clear();

            _buckets = bms.Cast<int>();
            _buckets.ToSpan().Clear();

            var e = entries.ToSpan();
            for (var i = 0; i < count; i++)
            {
                if (e[i].Next >= -1)
                {
                    ref var bucket = ref GetBucket(e[i].HashCode);
                    e[i].Next = bucket - 1; // Value in _buckets is 1-based
                    bucket = i + 1;
                }
            }

            _entries = entries;
            _memoryBlock.Dispose();
            _memoryBlock = newDataBlock;
        }

        #endregion
    }

    internal struct Entry
    {
        #region Fields

        public uint HashCode;

        // ReSharper disable once MemberHidesStaticFromOuterClass
        public KeyValuePairInternal KeyValuePair;

        /// <summary>
        /// 0-based index of next entry in chain: -1 means end of chain
        /// also encodes whether this entry _itself_ is part of the free list by changing sign and subtracting 3,
        /// so -2 means end of free list, -3 means index 0 but on free list, -4 means index 1 but on free list, etc.
        /// </summary>
        public int Next;

        #endregion
    }

    [PublicAPI]
    public ref struct Enumerator
    {
        #region Public APIs

        #region Properties

        public KeyValuePair Current => Unsafe.As<KeyValuePairInternal, KeyValuePair>(ref _accessor._entries[_index - 1].KeyValuePair);

        #endregion

        #region Methods

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool MoveNext()
        {
            while ((uint)_index < (uint)_dictionary._header->Count)
            {
                if (_accessor._entries[_index++].Next >= -1)
                {
                    return true;
                }
            }

            return false;
        }

        #endregion

        #endregion

        #region Constructors

        internal Enumerator(UnmanagedDictionary<TKey, TValue> dictionary, IEqualityComparer<TKey> comparer)
        {
            _dictionary = dictionary;
            _accessor = new Accessor(ref dictionary, comparer);
            _index = 0;
        }

        #endregion

        #region Fields

        private readonly UnmanagedDictionary<TKey, TValue> _dictionary;
        private int _index;
        private readonly Accessor _accessor;

        #endregion
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public int Count;
        public int FreeCount;
        public int FreeList;
        public int Size;
    }

    [DebuggerDisplay("Key {Key}, Value {Value}")]
    public struct KeyValuePair
    {
        #region Fields

        // ReSharper disable once UnassignedReadonlyField
        public readonly TKey Key;
        public TValue Value;

        #endregion
    }

    [DebuggerDisplay("Key {Key}, Value {Value}")]
    internal struct KeyValuePairInternal
    {
        #region Fields

        public TKey Key;
        public TValue Value;

        #endregion
    }

    #endregion

    #region Fields

    // private readonly IEqualityComparer<TKey> _comparer;
    // private MemorySegment<int> _buckets;
    // private MemorySegment<Entry> _entries;
    // private Header* _header;

    private MemoryBlock _memoryBlock;

    // Unfortunately this access is not as fast as we'd like to. We can't store the address of the memory block because it must remain process independent.
    // This is why we have a FastAccessor property that will give you a ref struct to work with and a faster access.
    private Header* _header
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        get => (Header*)_memoryBlock.MemorySegment.Address;
    }

    #endregion

    internal enum InsertionBehavior : byte
    {
        /// <summary>
        /// The default insertion behavior. If there's already a item with the given Key, do nothing return false
        /// </summary>
        None = 0,

        /// <summary>
        /// Specifies that an existing entry with the same key should be overwritten if encountered.
        /// </summary>
        OverwriteExisting = 1,

        /// <summary>
        /// Specifies that if an existing entry with the same key is encountered, an exception should be thrown.
        /// </summary>
        ThrowOnExisting = 2,

        /// <summary>
        /// If there's already an item with the given key, return the ref of the value
        /// </summary>
        GetExisting = 3
    }
}
