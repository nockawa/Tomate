using System.Diagnostics;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

namespace Tomate;

/// <summary>
/// An unmanaged Dictionary implementation.
/// </summary>
/// <typeparam name="TKey"></typeparam>
/// <typeparam name="TValue"></typeparam>
/// <remarks>
/// This implementation is a big copy/paste of the .net Dictionary{TKey, TValue} class and adapted to for unmanaged.
/// Methods return the value will return it as a reference for you to have a direct access of the data. It is your choice to mutate the value or not.
/// The enumerator also returns reference to the actual data and you're free to mutate the value if needed.
/// </remarks>
[PublicAPI]
public struct UnmanagedDictionary<TKey, TValue> : IDisposable where TKey : unmanaged where TValue : unmanaged
{
    [DebuggerDisplay("Key {Key}, Value {Value}")]
    private struct KeyValuePairInternal
    {
        public TKey Key;
        public TValue Value;
    }

    [DebuggerDisplay("Key {Key}, Value {Value}")]
    public struct KeyValuePair
    {
        // ReSharper disable once UnassignedReadonlyField
        public readonly TKey Key;
        public TValue Value;
    }

    private const int StartOfFreeList = -3;

    private struct Entry
    {
        public uint HashCode;
        /// <summary>
        /// 0-based index of next entry in chain: -1 means end of chain
        /// also encodes whether this entry _itself_ is part of the free list by changing sign and subtracting 3,
        /// so -2 means end of free list, -3 means index 0 but on free list, -4 means index 1 but on free list, etc.
        /// </summary>
        public int Next;
        // ReSharper disable once MemberHidesStaticFromOuterClass
        public KeyValuePairInternal KeyValuePair;
    }

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

    public int Count => _count - _freeCount;
    public bool IsDisposed => _allocator == null;

    public ref TValue this[TKey key]
    {
        get
        {
            ref TValue value = ref FindValue(key);
            if (!Unsafe.IsNullRef(ref value))
            {
                return ref value;
            }

            //ThrowHelper.ThrowKeyNotFoundException(key);
            //return default;
            //TODO Exception
            throw new KeyNotFoundException();
        }
    }
    
    public UnmanagedDictionary(IMemoryManager allocator, int capacity = 0, IEqualityComparer<TKey> comparer = null)
    {
        _allocator = allocator;
        
        _comparer = default;
        if (comparer is not null && comparer != EqualityComparer<TKey>.Default) // first check for null to avoid forcing default comparer instantiation unnecessarily
        {
            _comparer = comparer;
        }

        _buckets = default;
        _entries = default;
        _freeList = -1;
        _count = 0;
        _freeCount = 0;

        if (capacity > 0)
        {
            Initialize(capacity);
        }
    }


    public void Dispose()
    {
        _buckets.Dispose();
        _entries.Dispose();
        _allocator = null;
    }

    public Enumerator GetEnumerator() => new(this);

    /// <summary>
    /// Get a reference to the value for the given up or add a new entry if it doesn't exist
    /// </summary>
    /// <param name="key">The key of the element to get or add</param>
    /// <param name="found">
    /// If <c>true</c> the element with this key already exists. If <c>false</c> there was no element for the given key and we've added one.
    /// </param>
    /// <returns>
    /// A reference to the value of the element corresponding to the given key
    /// </returns>
    /// <remarks>
    /// Many things can be done with this method: you can get, add or update the value corresponding to the given key.
    /// You must NOT keep and use the returned reference after other mutable calls to this dictionary instance are made.
    /// Any further mutable call may end up reallocate this element to a different place and make this reference invalid.
    /// Accessing/setting would like to access violation/memory corruption.
    /// </remarks>
    public ref TValue GetOrAdd(TKey key, out bool found) => ref TryInsert(key, default, InsertionBehavior.GetExisting, out found);

    public void Add(TKey key, TValue value) => TryInsert(key, value, InsertionBehavior.ThrowOnExisting, out _);

    public bool TryAdd(TKey key, TValue value)
    {
        TryInsert(key, value, InsertionBehavior.None, out var res);
        return res;
    }

    /// <summary>
    /// Try to get the value corresponding to the given key
    /// </summary>
    /// <param name="key">The key of the element to access its value from.</param>
    /// <param name="found">Will return <c>true</c> if the element was found, <c>false</c> otherwise.</param>
    /// <returns>
    /// A reference to the value if the call succeed or a null reference otherwise.
    /// You must NOT keep and use the returned reference after other mutable calls to this dictionary instance are made.
    /// Any further mutable call may end up reallocate this element to a different place and make this reference invalid.
    /// Accessing/setting would like to access violation/memory corruption.
    /// </returns>
    public ref TValue TryGetValue(TKey key, out bool found)
    {
        ref TValue valRef = ref FindValue(key);
        if (!Unsafe.IsNullRef(ref valRef))
        {
            found = true;
            return ref valRef;
        }

        found = false;
        return ref Unsafe.NullRef<TValue>();
    }

    public ref TValue TryGetValue(TKey key)
    {
        return ref FindValue(key);
    }

    public bool Remove(TKey key, out TValue value)
    {
        if (_buckets.IsDefault == false)
        {
            Debug.Assert(_entries.IsDefault == false, "entries should be allocated");
            uint collisionCount = 0;
            var hashCode = (uint)(_comparer?.GetHashCode(key) ?? key.GetHashCode());
            ref var bucket = ref GetBucket(hashCode);
            var entries = _entries.MemorySegment.ToSpan();
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

                    Debug.Assert((StartOfFreeList - _freeList) < 0, "shouldn't underflow because max hashtable length is MaxPrimeArrayLength = 0x7FEFFFFD(2146435069) _freelist underflow threshold 2147483646");
                    entry.Next = StartOfFreeList - _freeList;

                    _freeList = i;
                    _freeCount++;
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
        }

        value = default;
        return false;
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

        int currentCapacity = _entries.MemorySegment.Length;
        if (currentCapacity >= capacity)
        {
            return currentCapacity;
        }

        if (_buckets.IsDefault)
        {
            return Initialize(capacity);
        }

        var newSize = PrimeHelpers.GetPrime(capacity);
        Resize(newSize);
        return newSize;
    }


    private int Initialize(int capacity)
    {
        _buckets.Dispose();
        _entries.Dispose();
        var size = PrimeHelpers.GetPrime(capacity);
        _buckets = _allocator.Allocate<int>(size);
        _entries = _allocator.Allocate<Entry>(size);
        _freeList = -1;

        _buckets.MemorySegment.ToSpan().Clear();
        _entries.MemorySegment.ToSpan().Clear();

        return size;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref int GetBucket(uint hashCode)
    {
        var buckets = _buckets.MemorySegment.ToSpan();
        return ref buckets[(int)(hashCode % (uint)buckets.Length)];
    }

    private ref TValue TryInsert(TKey key, TValue value, InsertionBehavior behavior, out bool result)
    {
        if (_buckets.IsDefault)
        {
            Initialize(0);
        }
        
        var entries = _entries.MemorySegment.ToSpan();

        IEqualityComparer<TKey> comparer = _comparer;
        uint hashCode = (uint)(comparer?.GetHashCode(key) ?? key.GetHashCode());

        uint collisionCount = 0;
        ref int bucket = ref GetBucket(hashCode);
        int i = bucket - 1; // Value in _buckets is 1-based

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
                        //TODO Exception
                        //ThrowHelper.ThrowAddingDuplicateWithKeyArgumentException(key);
                    }

                    result = false;
                    return ref Unsafe.NullRef<TValue>();
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
                        return ref entries[i].KeyValuePair.Value;
                    }

                    if (behavior == InsertionBehavior.GetExisting)
                    {
                        result = true;
                        return ref entries[i].KeyValuePair.Value;
                    }

                    if (behavior == InsertionBehavior.ThrowOnExisting)
                    {
                        //TODO Exception
                        //ThrowHelper.ThrowAddingDuplicateWithKeyArgumentException(key);
                    }

                    result = false;
                    return ref Unsafe.NullRef<TValue>();
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
        if (_freeCount > 0)
        {
            index = _freeList;
            _freeList = StartOfFreeList - entries[_freeList].Next;
            _freeCount--;
        }
        else
        {
            int count = _count;
            if (count == entries.Length)
            {
                Resize();
                bucket = ref GetBucket(hashCode);
            }
            index = count;
            _count = count + 1;
            entries = _entries.MemorySegment.ToSpan();
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

    internal ref TValue FindValue(TKey key)
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
                var entries = _entries.MemorySegment.ToSpan();
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

                    // The chain of entries forms a loop; which means a concurrent update has happened.
                    // Break out of the loop and throw, rather than looping forever.
                    goto ConcurrentOperation;
                }
            }
            else
            {
                var hashCode = (uint)comparer.GetHashCode(key);
                var i = GetBucket(hashCode);
                var entries = _entries.MemorySegment.ToSpan();
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
    //TODO Exception
        //ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
    ReturnFound:
        ref TValue value = ref entry.KeyValuePair.Value;
    Return:
        return ref value;
    ReturnNotFound:
        value = ref Unsafe.NullRef<TValue>();
        goto Return;
    }

    private void Resize() => Resize(PrimeHelpers.ExpandPrime(_count));

    private void Resize(int newSize)
    {
        // Value types never rehash
        Debug.Assert(_entries.IsDefault == false, "_entries should be allocated");
        Debug.Assert(newSize >= _entries.MemorySegment.Length);

        var entries = _allocator.Allocate<Entry>(newSize);

        int count = _count;
        _entries.MemorySegment.ToSpan().CopyTo(entries.MemorySegment.ToSpan());
        entries.MemorySegment.ToSpan()[count..].Clear();

        _buckets.Dispose();
        _buckets = _allocator.Allocate<int>(newSize);
        _buckets.MemorySegment.ToSpan().Clear();

        var e = entries.MemorySegment.ToSpan();
        for (int i = 0; i < count; i++)
        {
            if (e[i].Next >= -1)
            {
                ref int bucket = ref GetBucket(e[i].HashCode);
                e[i].Next = bucket - 1; // Value in _buckets is 1-based
                bucket = i + 1;
            }
        }

        _entries.Dispose();
        _entries = entries;
    }

    private IMemoryManager _allocator;
    private readonly IEqualityComparer<TKey> _comparer;
    private int _freeList;
    private MemoryBlock<int> _buckets;
    private MemoryBlock<Entry> _entries;
    private int _count;
    private int _freeCount;

    public unsafe struct Enumerator
    {
        private readonly UnmanagedDictionary<TKey, TValue> _dictionary;
        private readonly Entry* _entries;
        private int _index;

        internal Enumerator(UnmanagedDictionary<TKey, TValue> dictionary)
        {
            _dictionary = dictionary;
            _index = 0;
            _entries = _dictionary._entries.MemorySegment.Address;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool MoveNext()
        {
            while ((uint)_index < (uint)_dictionary._count)
            {
                if (_entries[_index++].Next >= -1)
                {
                    return true;
                }
            }

            return false;
        }

        public ref KeyValuePair Current => ref *(KeyValuePair*)&_entries[_index - 1].KeyValuePair;
    }
}

public struct UnmanagedDictionaryMultiValues<TKey, TValue> : IDisposable where TKey : unmanaged where TValue : unmanaged
{
    private readonly IMemoryManager _allocator;
    private UnmanagedDictionary<TKey, UnmanagedList<TValue>> _dictionary;

    public UnmanagedDictionaryMultiValues(IMemoryManager allocator, int capacity = 0, IEqualityComparer<TKey> comparer = null)
    {
        _allocator = allocator;
        _dictionary = new UnmanagedDictionary<TKey, UnmanagedList<TValue>>(allocator, capacity, comparer);
    }

    public int Count => _dictionary.Count;
    public bool IsDisposed => _dictionary.IsDisposed;

    public ref UnmanagedList<TValue> this[TKey key]
    {
        get
        {
            ref var values = ref _dictionary.TryGetValue(key);

            if (Unsafe.IsNullRef(ref values))
            {
                //TODO Exception
                throw new KeyNotFoundException();
            }

            return ref values;
        }
    }

    public void Add(TKey key, TValue value)
    {
        ref var values = ref _dictionary.GetOrAdd(key, out var found);
        if (found == false)
        {
            values = new UnmanagedList<TValue>(_allocator);
        }

        values.Add(value);
    }

    public bool Remove(TKey key)
    {
        var found = _dictionary.Remove(key, out var values);
        if (found)
        {
            values.Dispose();
        }

        return found;
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }
        foreach (ref var kvp in _dictionary)
        {
            kvp.Value.Dispose();
        }
        _dictionary.Dispose();
    }
}
