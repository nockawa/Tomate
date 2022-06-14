using System.Data.SqlTypes;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tomate;

/// <summary>
/// A very simple, not efficient dictionary, with concurrent operations made through a lock
/// </summary>
/// <remarks>
/// Designed to be simple, accesses are O(n), a key of value default(TKey) is not permitted as it's used to detect free entries.
/// Read-only access operations are made through a shared lock, read-write through an exclusive one.
/// There can be multiple concurrent RO operations, but any RW operation will be exclusive and block any other RO/RW operations.
/// You can enumerate the dictionary content, but only to evaluate it. Any call to ro/rw operations would lead to a deadlock.
/// </remarks>
public unsafe struct BlockingSimpleDictionary<TKey, TValue> where TKey : unmanaged where TValue : unmanaged
{
    private readonly Header* _header;
    private KeyValuePair* _items;
    private readonly EqualityComparer<TKey> _comparer;

    private static readonly TKey _defaultKey = default;

    /// <summary>
    /// Number of KVP items the dictionary can hold
    /// </summary>
    public int Capacity => _header->Capacity;
    
    /// <summary>
    /// Actual count of items stored
    /// </summary>
    public int Count => _header->Count;

    /// <summary>
    /// Store an item
    /// </summary>
    public struct KeyValuePair
    {
        public KeyValuePair(TKey k, TValue v)
        {
            Key = k;
            Value = v;
        }

        /// <summary>
        /// Key, can't be default(TKey).
        /// </summary>
        public TKey Key;

        /// <summary>
        /// Value associated to the key
        /// </summary>
        public TValue Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public AccessControl AccessControl;
        public int Capacity;
        public int Count;
    }

    /// <summary>
    /// Compute the memory size taken to store a given amount of items
    /// </summary>
    /// <param name="itemCount">Item count to compute the storage size from</param>
    /// <returns>The required size, a <see cref="MemorySegment"/> of this size would store the request amount of items</returns>
    public static int ComputeStorageSize(int itemCount) => sizeof(KeyValuePair) * itemCount + sizeof(Header);

    /// <summary>
    /// Compute the item capacity from a given storage size
    /// </summary>
    /// <param name="storageSize">The storage size to compute the capacity from</param>
    /// <returns>The item capacity</returns>
    public static int ComputeItemCapacity(int storageSize) => (storageSize - sizeof(Header)) / sizeof(KeyValuePair);

    public static BlockingSimpleDictionary<TKey, TValue> Create(MemorySegment segment) => new(segment, true);
    public static BlockingSimpleDictionary<TKey, TValue> Map(MemorySegment segment) => new(segment, false);

    /// <summary>
    /// Construct the dictionary over the given memory segment
    /// </summary>
    /// <param name="segment">The memory area used to store the dictionary</param>
    /// <remarks>
    /// <para>
    /// The memory segment can be a shared memory (through the use of a <see cref="MemoryManagerOverMMF"/> instance) area for the dictionary to be shared among
    /// multiple processes.
    /// </para>
    /// <para>
    /// You can call <see cref="ComputeStorageSize"/> to compute the size required for a given item capacity, or conversely call <see cref="ComputeItemCapacity"/>
    /// from a given memory size to know how many items would fit.
    /// </para>
    /// </remarks>
    private BlockingSimpleDictionary(MemorySegment segment, bool create)
    {
        _header = segment.Cast<Header>().Address;
        _items = (KeyValuePair*)(_header + 1);
        _comparer = EqualityComparer<TKey>.Default;
        if (create)
        {
            var remainingSize = segment.Length - sizeof(Header);
            _header->Capacity = remainingSize / sizeof(KeyValuePair);
            _header->Count = 0;
            _header->AccessControl.Reset();
            Clear();
        }
    }

    /// <summary>
    /// Try to get the value from a given key
    /// </summary>
    /// <param name="key">The key to use</param>
    /// <param name="value">The value corresponding</param>
    /// <returns><c>true</c> if the dictionary stores the requested key, <c>false</c> otherwise.</returns>
    /// <remarks>
    /// This operation relies on a shared access.
    /// </remarks>
    public bool TryGet(TKey key, out TValue value)
    {
        try
        {
            _header->AccessControl.EnterSharedAccess();

            var capacity = _header->Capacity;
            var count = _header->Count;
            var curCount = 0;
            var items = _items;
            for (int i = 0; i < capacity && curCount < count; i++, ++items)
            {
                if (_comparer.Equals(items->Key, _defaultKey))
                {
                    continue;
                }

                ++curCount;
                if (_comparer.Equals(items->Key, key))
                {
                    value = items->Value;
                    return true;
                }
            }

            value = default;
            return false;
        }
        finally
        {
            _header->AccessControl.ExitSharedAccess();
        }
    }

    /// <summary>
    /// Check if there's a key of the given value stored in the dictionary
    /// </summary>
    /// <param name="key">The key</param>
    /// <returns><c>true</c> if the dictionary stores the requested key, <c>false</c> otherwise.</returns>
    /// <remarks>
    /// This operation relies on a shared access.
    /// </remarks>
    public bool Contains(TKey key)
    {
        try
        {
            _header->AccessControl.EnterSharedAccess();

            var capacity = _header->Capacity;
            var count = _header->Count;
            var curCount = 0;
            var items = _items;
            for (int i = 0; i < capacity && curCount < count; i++, ++items)
            {
                if (_comparer.Equals(items->Key, _defaultKey))
                {
                    continue;
                }

                ++curCount;
                if (_comparer.Equals(items->Key, key))
                {
                    return true;
                }
            }
            return false;
        }
        finally
        {
            _header->AccessControl.ExitSharedAccess();
        }
    }

    /// <summary>
    /// Try to add a key/value to the dictionary
    /// </summary>
    /// <param name="key">The key, can't be default(TKey).</param>
    /// <param name="value">The value.</param>
    /// <returns><c>true</c> if the dictionary added the requested key/value, <c>false</c> if there were already a key with this value.</returns>
    /// <remarks>
    /// This operation relies on an exclusive access.
    /// </remarks>
    public bool TryAdd(TKey key, TValue value)
    {
        try
        {
            if (_comparer.Equals(key, _defaultKey))
            {
                ThrowHelper.BlockSimpleDicDefKeyNotAllowed();
            }

            _header->AccessControl.EnterExclusiveAccess();

            // Check for capacity limit reached
            var capacity = _header->Capacity;
            var count = _header->Count;
            if (count == capacity)
            {
                return false;
            }

            // First check if there's an entry with a key of the same value and return false if it's the case
            var curCount = 0;
            var items = _items;
            for (int i = 0; i < capacity && curCount < count; i++, ++items)
            {
                if (_comparer.Equals(items->Key, _defaultKey))
                {
                    continue;
                }

                ++curCount;
                if (_comparer.Equals(items->Key, key))
                {
                    return false;
                }
            }

            // Now look for an empty entry and store the KVP
            items = _items;
            for (int i = 0; i < capacity; i++, ++items)
            {
                if (_comparer.Equals(items->Key, _defaultKey))
                {
                    items->Key = key;
                    items->Value = value;
                    ++_header->Count;
                    return true;
                }
            }
            Debug.Assert(false, "We should never get here!");
            return false;
        }
        finally
        {
            _header->AccessControl.ExitExclusiveAccess();
        }
    }

    /// <summary>
    /// Add the given key/value pair or update the value of the given key if it's already present in the dictionary
    /// </summary>
    /// <param name="key">The key</param>
    /// <param name="value">The value</param>
    /// <returns><c>true</c> if the operation succeeded, <c>false</c> if the key/value pair couldn't be added because the dictionary is full</returns>
    public bool AddOrUpdate(TKey key, TValue value)
    {
        try
        {
            if (_comparer.Equals(key, _defaultKey))
            {
                ThrowHelper.BlockSimpleDicDefKeyNotAllowed();
            }

            _header->AccessControl.EnterExclusiveAccess();

            // First check if there's an entry with a key of the same value and update the value if found
            var capacity = _header->Capacity;
            var count = _header->Count;
            var curCount = 0;
            var items = _items;
            for (int i = 0; i < capacity && curCount < count; i++, ++items)
            {
                if (_comparer.Equals(items->Key, _defaultKey))
                {
                    continue;
                }

                ++curCount;
                if (_comparer.Equals(items->Key, key))
                {
                    items->Value = value;
                    return true;
                }
            }

            // Check for capacity limit reached
            if (count == capacity)
            {
                return false;
            }

            // Now look for an empty entry and store the KVP
            items = _items;
            for (int i = 0; i < capacity; i++, ++items)
            {
                if (_comparer.Equals(items->Key, _defaultKey))
                {
                    items->Key = key;
                    items->Value = value;
                    ++_header->Count;
                    return true;
                }
            }
            Debug.Assert(false, "We should never get here!");
            return false;
        }
        finally
        {
            _header->AccessControl.ExitExclusiveAccess();
        }
    }

    /// <summary>
    /// Change the value associated with a given key
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="newValue">The new value to set</param>
    /// <returns><c>true</c> if the value has been set, <c>false</c> if there were no key of the given value.</returns>
    /// <remarks>
    /// This operation relies on an exclusive access.
    /// </remarks>
    public bool TryUpdateValue(TKey key, TValue newValue)
    {
        try
        {
            if (_comparer.Equals(key, _defaultKey))
            {
                ThrowHelper.BlockSimpleDicDefKeyNotAllowed();
            }

            _header->AccessControl.EnterExclusiveAccess();

            // First check if there's an entry with a key of the same value and return false if it's the case
            var capacity = _header->Capacity;
            var count = _header->Count;
            var curCount = 0;
            var items = _items;
            for (int i = 0; i < capacity && curCount < count; i++, ++items)
            {
                if (_comparer.Equals(items->Key, _defaultKey))
                {
                    continue;
                }

                ++curCount;
                if (_comparer.Equals(items->Key, key))
                {
                    items->Value = newValue;
                    return true;
                }
            }
            return false;
        }
        finally
        {
            _header->AccessControl.ExitExclusiveAccess();
        }
    }

    /// <summary>
    /// Remove a key/value from the dictionary
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value, is <c>default</c> if the call is unsuccessful.</param>
    /// <returns><c>true</c> if the key/value were removed, <c>false</c> if there were no key of the given value.</returns>
    /// <remarks>
    /// This operation relies on an exclusive access.
    /// </remarks>
    public bool TryRemove(TKey key, out TValue value)
    {
        try
        {
            _header->AccessControl.EnterExclusiveAccess();

            // Look for the key
            var capacity = _header->Capacity;
            var count = _header->Count;
            var curCount = 0;
            var items = _items;
            for (int i = 0; i < capacity && curCount < count; i++, ++items)
            {
                if (_comparer.Equals(items->Key, _defaultKey))
                {
                    continue;
                }

                ++curCount;
                if (_comparer.Equals(items->Key, key))
                {
                    items->Key = default;
                    value = items->Value;
                    --_header->Count;
                    return true;
                }
            }

            value = default;
            return false;
        }
        finally
        {
            _header->AccessControl.ExitExclusiveAccess();
        }
    }

    /// <summary>
    /// Project all the items of the dictionary to a KeyValuePair array
    /// </summary>
    /// <returns>The array containing the items</returns>
    /// <remarks>
    /// This operation relies on a shared access.
    /// You would typically use this method over content enumeration if you want to hold the shared lock the shortest time possible.
    /// </remarks>
    public KeyValuePair[] ToArray()
    {
        try
        {
            _header->AccessControl.EnterExclusiveAccess();

            var res = new KeyValuePair[Count];
            var capacity = _header->Capacity;
            var count = _header->Count;
            var curCount = 0;
            var items = _items;
            for (int i = 0; i < capacity && curCount < count; i++, ++items)
            {
                if (_comparer.Equals(items->Key, _defaultKey))
                {
                    continue;
                }

                res[curCount++] = new KeyValuePair(items->Key, items->Value);
            }

            return res;
        }
        finally
        {
            _header->AccessControl.ExitExclusiveAccess();
        }
    }
    
    /// <summary>
    /// Get a value associated with the given key or add a new key/value pair.
    /// </summary>
    /// <param name="key">The key</param>
    /// <param name="valueFactory">A factory that will be called to generated the value if the key was not present in the dictionary</param>
    /// <param name="added"><c>true</c> if the key/value pair was added, <c>false</c> if the key/value pair already exists in the dictionary</param>
    /// <param name="success"><c>true</c> if the operation succeeded, <c>false</c>if the dictionary is full and the key/value pair couldn't be added</param>
    /// <returns>The value associated with the given key</returns>
    /// <remarks>
    /// This operation relies on an exclusive access.
    /// </remarks>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory, out bool added, out bool success)
    {
        try
        {
            if (_comparer.Equals(key, _defaultKey))
            {
                ThrowHelper.BlockSimpleDicDefKeyNotAllowed();
            }

            _header->AccessControl.EnterExclusiveAccess();

            // First check if there's an entry with a key of the same value and return it
            var capacity = _header->Capacity;
            var count = _header->Count;
            var curCount = 0;
            var items = _items;
            for (int i = 0; i < capacity && curCount < count; i++, ++items)
            {
                if (_comparer.Equals(items->Key, _defaultKey))
                {
                    continue;
                }

                ++curCount;
                if (_comparer.Equals(items->Key, key))
                {
                    added = false;
                    success = true;
                    return items->Value;
                }
            }

            if (capacity == count)
            {
                added = false;
                success = false;
                return default;
            }

            // Now look for an empty entry and store the KVP
            items = _items;
            for (int i = 0; i < capacity; i++, ++items)
            {
                if (_comparer.Equals(items->Key, _defaultKey))
                {
                    items->Key = key;
                    items->Value = valueFactory(key);
                    ++_header->Count;
                    added = success = true;
                    return items->Value;
                }
            }
            Debug.Assert(false, "We should never get here!");
            added = success = false;
            return default;
        }
        finally
        {
            _header->AccessControl.ExitExclusiveAccess();
        }
    }

    /// <summary>
    /// Get or add/update the value associated with the given key
    /// </summary>
    /// <param name="key">The key</param>
    /// <returns>The value associated with the key or default(TValue) if the key doesn't exist in the dictionary</returns>
    /// <remarks>
    /// The getter does a <see cref="TryGet"/>. The setter does a <see cref="AddOrUpdate"/>.
    /// </remarks>
    public TValue this[TKey key]
    {
        get
        {
            if (TryGet(key, out var val))
            {
                return val;
            }

            return default;
        }
        set => AddOrUpdate(key, value);
    }

    /// <summary>
    /// Clear the whole dictionary
    /// </summary>
    public void Clear()
    {
        _header->Count = 0;
        new Span<KeyValuePair>(_items, Capacity).Clear();
    }

    /// <summary>Gets an enumerator for this dictionary</summary>
    /// <remarks>
    /// A shared lock will be created before enumeration and released at the end. 
    /// The enumeration is not meant to change the content of the dictionary.
    /// Don't call any operation on the dictionary that leads to a shared/exclusive access, it would lead to a dead-lock as locks are not re-entrant on this type.
    /// </remarks>
    public Enumerator GetEnumerator() => new(this);

    /// <summary>Enumerates the elements of the dictionary.</summary>
    public struct Enumerator : IDisposable
    {
        /// <summary>The segment being enumerated.</summary>
        private readonly BlockingSimpleDictionary<TKey, TValue> _dic;

        private int _curCount;
        private readonly int _count;
        private KeyValuePair* _curItem;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Enumerator(BlockingSimpleDictionary<TKey, TValue> dic)
        {
            _dic = dic;
            _curCount = 0;
            _count = dic.Count;
            _curItem = dic._items - 1;
            _dic._header->AccessControl.EnterSharedAccess();
        }

        /// <summary>Advances the enumerator to the next element of the segment.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            if (++_curCount > _count)
            {
                return false;
            }

            ++_curItem;
            while (_dic._comparer.Equals(_curItem->Key, _defaultKey))
            {
                ++_curItem;
            }

            return true;
        }

        /// <summary>Gets the element at the current position of the enumerator.</summary>
        public KeyValuePair Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => *_curItem;
        }

        public void Dispose()
        {
            _dic._header->AccessControl.ExitSharedAccess();
        }
    }
}