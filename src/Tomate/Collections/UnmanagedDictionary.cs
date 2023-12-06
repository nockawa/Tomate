using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Tomate;

/// <summary>
/// A dictionary implementation for MemoryMappedFile, thread-safe but not concurrent friendly
/// </summary>
/// <typeparam name="TKey"></typeparam>
/// <typeparam name="TValue"></typeparam>
/// <remarks>
/// This implementation is a big copy/paste of the .net Dictionary{TKey, TValue} class and adapted to this usage.
/// Methods returning the value will return it as a reference for you to have a direct access of the data. It is your choice to mutate the value or not.
/// The enumerator also returns reference to the actual data and you're free to mutate the value if needed.
/// </remarks>
[PublicAPI]
public unsafe struct UnmanagedDictionary<TKey, TValue> : IDisposable where TKey : unmanaged where TValue : unmanaged
{
    #region Constants

    private const int StartOfFreeList = -3;

    #endregion

    #region Public APIs

    #region Properties

    public int Count => _header!=null ? (_header->Count - _header->FreeCount) : 0;

    public bool IsDefault => _memoryBlock.IsDefault;
    public bool IsDisposed => _memoryBlock.IsDefault;

    public TValue this[TKey key]
    {
        get
        {
            var value = FindValue(key, out var res);
            if (res)
            {
                return value;
            }

            ThrowHelper.KeyNotFound(key);
            return default;
        }
    }

    #endregion

    #region Methods

    public static UnmanagedDictionary<TKey, TValue> Create(IMemoryManager owner, int capacity = 8, IEqualityComparer<TKey> comparer = null) => new(owner, capacity, comparer, true);

    public void Add(TKey key, TValue value) => TryInsert(key, value, InsertionBehavior.ThrowOnExisting, out _);

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

    public Enumerator GetEnumerator() => new(this);

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

    #endregion

    #endregion

    #region Fields

    private readonly IEqualityComparer<TKey> _comparer;
    private MemorySegment<int> _buckets;
    private MemorySegment<Entry> _entries;
    private Header* _header;

    private MemoryBlock _memoryBlock;

    #endregion

    #region Constructors

    //public static MappedBlockingDictionary<TKey, TValue> Map(IPageAllocator allocator, int rootPageId) => new(allocator, rootPageId, false);
    private UnmanagedDictionary(IMemoryManager owner, int capacity, IEqualityComparer<TKey> comparer, bool create)
    {
        Debug.Assert(capacity >= 3, "Capacity must be at least 3 to be valid.");
        owner ??= DefaultMemoryManager.GlobalInstance;
        _comparer = default;
        // ReSharper disable once PossibleUnintendedReferenceComparison
        if (comparer is not null && comparer != EqualityComparer<TKey>.Default) // first check for null to avoid forcing default comparer instantiation unnecessarily
        {
            _comparer = comparer;
        }

        _buckets = default;
        _entries = default;

        var size = PrimeHelpers.GetPrime(capacity);
        _memoryBlock = owner.Allocate(sizeof(Header) + size * (sizeof(int) + sizeof(Entry)));
        _memoryBlock.MemorySegment.ToSpan<byte>().Clear();
        var (h, m) = _memoryBlock.MemorySegment.Split(sizeof(Header));
        _header = (Header*)h.Address;
        _header->FreeList = -1;

        var (b, e) = m.Split(size * sizeof(int));
        _buckets = b.Cast<int>();
        _entries = e.Cast<Entry>();
    }

    #endregion

    #region Internals

    #region Internals methods

    // Must execute under shared lock
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

    #endregion

    #endregion

    #region Private methods

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

    private TValue TryInsert(TKey key, TValue value, InsertionBehavior behavior, out bool result)
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

    #region Inner types

    private struct Entry
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
    public struct Enumerator
    {
        #region Public APIs

        #region Properties

        public ref KeyValuePair Current => ref *(KeyValuePair*)&_entries[_index - 1].KeyValuePair;

        #endregion

        #region Methods

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool MoveNext()
        {
            while ((uint)_index < (uint)_dictionary._header->Count)
            {
                if (_entries[_index++].Next >= -1)
                {
                    return true;
                }
            }

            return false;
        }

        #endregion

        #endregion

        #region Fields

        private readonly UnmanagedDictionary<TKey, TValue> _dictionary;
        private readonly Entry* _entries;
        private int _index;

        #endregion

        #region Constructors

        internal Enumerator(UnmanagedDictionary<TKey, TValue> dictionary)
        {
            _dictionary = dictionary;
            _index = 0;
            _entries = _dictionary._entries.Address;
        }

        #endregion
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        #region Fields

        public int Count;
        public int FreeCount;
        public int FreeList;

        #endregion
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
    private struct KeyValuePairInternal
    {
        #region Fields

        public TKey Key;
        public TValue Value;

        #endregion
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
