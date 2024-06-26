using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;
// #pragma warning disable CS9087 // This returns a parameter by reference but it is not a ref parameter
// #pragma warning disable CS9084 // Struct member returns 'this' or other instance members by reference

namespace Tomate;

/// <summary>
/// An unmanaged implementation of a dictionary
/// </summary>
/// <typeparam name="TKey">Type of the key</typeparam>
/// <typeparam name="TValue">Type of the value</typeparam>
/// <remarks>
/// This implementation is a big copy/paste of the .net Dictionary{TKey, TValue} class and adapted to this usage.
/// This type is MemoryMappedFile friendly, meaning you can allocate instances of this type with an <see cref="MemoryManagerOverMMF"/> as memory manager.
/// This type can't store custom comparer in order to be unmanaged, some APIs will use the default one (like the Subscript operator), other will take one
///  as an argument (like the <see cref="TryGetValue"/> method).
/// </remarks>
[PublicAPI]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct UnmanagedDictionary<TKey, TValue> : IUnmanagedCollection where TKey : unmanaged where TValue : unmanaged
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
    /// It also will rely on the default comparer, if you need to use a custom one, you should use the <see cref="TryGetValue"/> and <see cref="TrySetValue"/>.
    /// </remarks>
    public ref TValue this[TKey key]
    {
        get
        {
            ref var value = ref FindValue(key, null, out var res);
            if (res)
            {
                return ref value;
            }

            ThrowHelper.KeyNotFound(key);
            return ref Unsafe.NullRef<TValue>();
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

    public int RefCounter => _memoryBlock.RefCounter;

    public IMemoryManager MemoryManager => _memoryBlock.MemoryManager;
    public MemoryBlock MemoryBlock => _memoryBlock;

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
    public static UnmanagedDictionary<TKey, TValue> Create(IMemoryManager memoryManager=null, int capacity = 11) => new(memoryManager, capacity);

    public void RefreshFromMMF(MemoryBlock newData)
    {
        _memoryBlock = newData;
        EnsureInternalState(true);
    }

    public int AddRef() => _memoryBlock.AddRef();

    /// <summary>
    /// Dispose the instance, see remarks
    /// </summary>
    /// <remarks>
    /// This call will decrement the reference counter by 1 and the instance will effectively be disposed if it reaches 0, otherwise it will still be usable.
    /// </remarks>
    public void Dispose()
    {
        EnsureInternalState();
        if (IsDefault || IsDisposed)
        {
            return;
        }
        _memoryBlock.Dispose();
        if (_memoryBlock.IsDisposed)
        {
            _memoryBlock = default;
            EnsureInternalState(true);
        }
    }

    /// <summary>
    /// Ensures that the dictionary can hold up to 'capacity' entries without any further expansion of its backing storage
    /// </summary>
    public int EnsureCapacity(int capacity)
    {
        if (capacity < 0)
        {
            ThrowHelper.OutOfRange($"The capacity can't be negative: {capacity}");
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

    /// <summary>
    /// Add a new entry to the dictionary given the key and its corresponding value
    /// </summary>
    /// <param name="key">The key of the entry must be unique, will throw if it already exists</param>
    /// <param name="value"></param>
    /// <param name="comparer"></param>
    /// <exception cref="ArgumentException">If <paramref name="key"/> is already present in the dictionary</exception>
    public void Add(TKey key, TValue value, IEqualityComparer<TKey> comparer = null) => TryInsert(key, value, InsertionBehavior.ThrowOnExisting, comparer, out _);

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
    public ref TValue GetOrAdd(TKey key, out bool found, IEqualityComparer<TKey> comparer = null) => ref TryInsert(key, default, InsertionBehavior.GetExisting, comparer, out found);

    /// <summary>
    /// Remove the entry of the given key from the dictionary
    /// </summary>
    /// <param name="key">The key of the entry to remove</param>
    /// <param name="value">If the entry was successfully removed, will contain the corresponding value, otherwise will contain <c>default</c>.</param>
    /// <param name="comparer">The comparer to use with this operation, <c>null</c> will be the default one</param>
    /// <returns></returns>
    public bool Remove(TKey key, out TValue value, IEqualityComparer<TKey> comparer = null)
    {
        EnsureInternalState();
        if (_buckets.IsDefault)
        {
            value = default;
            return false;
        }

        Debug.Assert(_entries.IsDefault == false, "entries should be allocated");
        uint collisionCount = 0;
        var hashCode = (uint)(comparer?.GetHashCode(key) ?? key.GetHashCode());
        ref var bucket = ref GetBucket(hashCode);
        var entries = _entries.ToSpan();
        var last = -1;
        var i = bucket - 1; // Value in buckets is 1-based
        while (i >= 0)
        {
            ref var entry = ref entries[i];

            if (entry.HashCode == hashCode && (comparer?.Equals(entry.KeyValuePair.Key, key) ?? EqualityComparer<TKey>.Default.Equals(entry.KeyValuePair.Key, key)))
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
                // The chain of entries forms a loop; which means a concurrent update has happened.
                // Break out of the loop and throw, rather than looping forever.
                ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
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
    /// <param name="comparer">The comparer to use with this operation, <c>null</c> will be the default one</param>
    /// <returns>
    /// Returns <c>true</c> is the entry was successfully added, <c>false</c> if we couldn't add it because there's already another with the same key
    /// </returns>
    public bool TryAdd(TKey key, TValue value, IEqualityComparer<TKey> comparer = null)
    {
        TryInsert(key, value, InsertionBehavior.None, comparer, out var res);
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
    public ref TValue TryGetValue(TKey key, out bool found, IEqualityComparer<TKey> comparer = null)
    {
        return ref FindValue(key, comparer, out found);
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
        TryInsert(key, value, InsertionBehavior.OverwriteExisting, comparer, out var modified);
        return modified;
    }

    /// <summary>
    /// Enumerator access for <c>foreach</c>
    /// </summary>
    /// <returns>The enumerator, an instance of the <see cref="Enumerator"/> type</returns>
    /// <remarks>
    /// BEWARE: the enumerator will rely on the default comparer.
    /// </remarks>
    public Enumerator GetEnumerator() => new(this, null);

    #endregion

    #endregion

    #region Constructors

    public UnmanagedDictionary() : this(null, 11)
    {
        
    }

    public UnmanagedDictionary(IMemoryManager owner, int capacity)
    {
        Debug.Assert(capacity >= 3, "Capacity must be at least 3 to be valid.");
        owner ??= DefaultMemoryManager.GlobalInstance;

        var size = PrimeHelpers.GetPrime(capacity);
        _memoryBlock = owner.Allocate(sizeof(Header) + size * (sizeof(int) + sizeof(Entry)));
        _memoryBlock.MemorySegment.ToSpan<byte>().Clear();

        var memoryBlock = _memoryBlock;
        var (h, m) = memoryBlock.MemorySegment.Split(sizeof(Header));
        _header = (Header*)h.Address;
        _header->FreeList = -1;
        _header->Size = size;

        var (b, e) = m.Split(_header->Size * sizeof(int));
        _buckets = b.Cast<int>();
        _entries = e.Cast<Entry>();
    }

    #endregion

    #region Internals

    internal ref TValue FindValue(TKey key, IEqualityComparer<TKey> comparer, out bool found)
    {
        EnsureInternalState();
        ref var entry = ref Unsafe.NullRef<Entry>();
        if (_buckets.IsDefault == false)
        {
            Debug.Assert(_entries.IsDefault == false, "expected entries to be allocated");
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

                // The chain of entries forms a loop; which means a concurrent update has happened.
                // Break out of the loop and throw, rather than looping forever.
                goto ConcurrentOperation;
            }
        }

        goto ReturnNotFound;

ConcurrentOperation:
        ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
ReturnFound:
        found = true;
        return ref entry.KeyValuePair.Value;
ReturnNotFound:
        found = false;
        return ref Unsafe.NullRef<TValue>();
    }

    internal ref TValue TryInsert(TKey key, TValue value, InsertionBehavior behavior, IEqualityComparer<TKey> comparer, out bool result)
    {
        EnsureInternalState();
        var entries = _entries.ToSpan();

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
                        return ref entries[i].KeyValuePair.Value;
                    }

                    if (behavior == InsertionBehavior.GetExisting)
                    {
                        result = true;
                        return ref entries[i].KeyValuePair.Value;
                    }

                    if (behavior == InsertionBehavior.ThrowOnExisting)
                    {
                        ThrowHelper.AddingDuplicateKeyException(key, nameof(key));
                    }

                    result = false;
                    return ref Unsafe.NullRef<TValue>();
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
                        return ref entries[i].KeyValuePair.Value;
                    }

                    if (behavior == InsertionBehavior.GetExisting)
                    {
                        result = true;
                        return ref entries[i].KeyValuePair.Value;
                    }

                    if (behavior == InsertionBehavior.ThrowOnExisting)
                    {
                        ThrowHelper.ThrowAddingDuplicateWithKeyArgumentException(key);
                    }

                    result = false;
                    return ref Unsafe.NullRef<TValue>();
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
        return ref entry.KeyValuePair.Value;
    }

    #endregion

    #region Privates

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void EnsureInternalState(bool force = false)
    {
        // Non MMF means the cached addresses are always valid
        // Note: maybe we could ignore this check in order to allow multiple instances of the same list to be used at the same time
        if (force==false && _memoryBlock.MemorySegment.IsInMMF == false)
        {
            return;
        }

        // Check if the data we've precomputed is still valid
        var header = (Header*)_memoryBlock.MemorySegment.Address;
        if (force==false && _header == header)
        {
            return;
        }
        
        var memoryBlock = _memoryBlock;

        if (memoryBlock.IsDefault)
        {
            _header = null;
            _buckets = default;
            _entries = default;
        }
        else
        {
            var (_, m) = memoryBlock.MemorySegment.Split(sizeof(Header));
            _header = header;
            var (b, e) = m.Split(_header->Size * sizeof(int));
            _buckets = b.Cast<int>();
            _entries = e.Cast<Entry>();
        }
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

    #region Inner types

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

        public KeyValuePair Current => Unsafe.As<KeyValuePairInternal, KeyValuePair>(ref _dictionary._entries[_index - 1].KeyValuePair);

        #endregion

        #region Methods

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool MoveNext()
        {
            while ((uint)_index < (uint)_dictionary._header->Count)
            {
                if (_dictionary._entries[_index++].Next >= -1)
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
            _index = 0;
        }

        #endregion

        #region Fields

        private readonly UnmanagedDictionary<TKey, TValue> _dictionary;
        private int _index;

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

        // ReSharper disable once UnassignedField.Global
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

    ///////////////////////////////////////////////////////////////////////////////////////////////
    // 44 bytes of data
    // DON'T REORDER THIS FIELDS DECLARATION

    // The memory block must ALWAYS be the first field of every UnmanagedCollection types
    private MemoryBlock _memoryBlock;           // Offset  0, length 12
    private MemorySegment<int> _buckets;        // Offset 12, length 12
    private MemorySegment<Entry> _entries;      // Offset 24, length 12
    private Header* _header;                    // Offset 36, length 8

    // 44 bytes of data
    ///////////////////////////////////////////////////////////////////////////////////////////////

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private ref int GetBucket(uint hashCode)
    {
        var buckets = _buckets.ToSpan();
        return ref buckets[(int)(hashCode % (uint)buckets.Length)];
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
