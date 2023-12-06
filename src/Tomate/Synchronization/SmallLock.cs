using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Tomate;

/// <summary>
/// Exception triggered when an operation can't be performed to due a greater number of concurrent operations than <see cref="SmallLock"/> can handle.
/// </summary>
public class SmallLockConcurrencyExceededException : Exception
{
    #region Constructors

    public SmallLockConcurrencyExceededException(string message) : base(message)
    {
        
    }

    #endregion
}

/// <summary>
/// An interprocess lock allowing control over concurrent accesses for a given (immaterial) resource.
/// </summary>
/// <remarks>
/// <para>
/// As all interprocess compatible types, `SmallLock` is designed to be compatible with storing instances on a <see cref="MemoryManagerOverMMF"/>.
/// One of the resulted constraint is you have to specify at construction the maximum level of concurrency, resizing data on the fly would make the
/// implementation of this type way harder.
/// </para>
/// <para>
/// This type would apparently itself to <see cref="System.Threading.Monitor"/> but is interprocess compatible (and simpler, as more limited).
/// </para>
/// </remarks>
[PublicAPI]
[StructLayout(LayoutKind.Sequential)]
public unsafe struct SmallLock
{
    #region Public APIs

    #region Properties

    /// <summary>
    /// Get the maximum concurrency level the lock can support.
    /// </summary>
    /// <remarks>
    /// This value is determined at construction, base on the size of the data segment that is given.
    /// The lock can't support more concurrent operations (e.g.: simultaneous/concurrent calls to <see cref="Enter"/> for instance) than given at construction.
    /// </remarks>
    public int ConcurrencyCapacity => _header->QueueCapacity;

    /// <summary>
    /// Get the current concurrency level, at the time this property is accessed
    /// </summary>
    public int ConcurrencyCounter => _header->QueueCount;

    /// <summary>
    /// Return <c>true</c> if the lock is taken by _someone_, <c>false</c> otherwise.
    /// </summary>
    public bool IsEntered => _header->LockedBy != 0;

    /// <summary>
    /// Get the id of the processing holding the lock
    /// </summary>
    /// <remarks>
    /// The return id is equivalent to <see cref="IProcessProvider.CurrentProcessId"/> for the calling process.
    /// <c>0</c> means the lock is not held.
    /// </remarks>
    public int LockedByProcess => _header->LockedBy.HighS();

    /// <summary>
    /// Get the lockId (the one given during `Enter()`) that is currently holding the lock
    /// </summary>
    /// <remarks>
    /// <c>0</c> means the lock is not held.
    /// </remarks>
    public int LockId => _header->LockedBy.LowS();

    private int QueueCount => _header->QueueCount;

    #endregion

    #region Methods

    /// <summary>
    /// Helper method determining the size of the <see cref="MemorySegment"/> needed to store a `SmallLock` supporting the given concurrency level
    /// </summary>
    /// <param name="concurrencyLevel">
    /// The required concurrency level, that is, the number of processes/threads that would be able to use the <see cref="SmallLock"/> instance concurrently.
    /// </param>
    /// <returns>The size the `MemorySegment` should be to store one instance.</returns>
    public static int ComputeSegmentSize(ushort concurrencyLevel) => sizeof(Header) + sizeof(long)*concurrencyLevel;

    /// <summary>
    /// Create a new instance, stored in the given memory location
    /// </summary>
    /// <param name="segment">The memory segment used to store the instance, the max concurrency will determined by the size of this segment.</param>
    /// <returns>The created instance.</returns>
    public static SmallLock Create(MemorySegment segment) => new(segment, true);

    /// <summary>
    /// Create a C# instance of `SmallLock` by mapping to an existing (and previously created) one. 
    /// </summary>
    /// <param name="segment">The memory segment that contains the data of the `SmallLock` to map against.</param>
    /// <returns>The instance</returns>
    /// <remarks>See <see cref="MemoryManagerOverMMF"/> for more detail of how this is working</remarks>
    public static SmallLock Map(MemorySegment segment) => new(segment, false);

    /// <summary>
    /// Acquire the lock with an exclusive access
    /// </summary>
    /// <exception cref="SmallLockConcurrencyExceededException">Can't enter because the maximum count of concurrency level is already reached.</exception>
    /// <remarks>
    /// This method will use the process Id and thread Id to identify this access request, if the lock is already hold by someone else, it will wait
    ///  an indeterminate amount of time to get it.
    /// Call <see cref="Exit()"/> to release access.
    /// </remarks>
    public void Enter()
    {
        TryEnter(out _, out _);
    }

    /// <summary>
    /// Release the exclusive access on the lock 
    /// </summary>
    /// <remarks>
    /// This method must be called after a successful <see cref="Enter"/>
    /// </remarks>
    public void Exit() => Exit(0);

    /// <summary>
    /// Release the exclusive access on the lock 
    /// </summary>
    /// <remarks>
    /// This method must be called after a successful <see cref="TryEnter(out bool,int,System.TimeSpan)"/> or <see cref="TryEnter(out bool, out bool,int,System.TimeSpan)"/>
    /// </remarks>
    public void Exit(int lockId)
    {
        // If no lockId is given we use the ManagedThreadId
        if (lockId == 0)
        {
            lockId = Environment.CurrentManagedThreadId;
        }
        
        // Compute the full Lock Id with the process and the given lockId
        ulong fullLockId = default;
        fullLockId.Pack(IProcessProvider.Singleton.CurrentProcessId, lockId);

        if (_header->LockedBy != fullLockId)
        {
            ThrowHelper.SmallLockBadLockId(_header->LockedBy, fullLockId);
        }

        // Nested case
        if (--_header->ReentrencyCounter > 0)
        {
            return;
        }
        
        // We can dequeue, it's the last concerning nesting
        Dequeue();
    }

    /// <summary>
    /// Attempt to acquire the lock with an exclusive access
    /// </summary>
    /// <param name="lockTaken">
    /// If <c>true</c> the user will have to call <see cref="Exit(int)"/> because the lock was successfully taken, <c>false</c> otherwise.
    /// The most likely reason for this arg to be <c>false</c> is we couldn't acquire the lock in due time.
    /// </param>
    /// <param name="lockId">Id to assign to the lock, if <c>0</c> the thread's <c>ManagedThreadId</c> will be used.</param>
    /// <param name="timeOut">
    /// If the lock couldn't be acquired during this time span, we give up.
    /// <c>default(TimeSpan)</c> will be considered as waiting forever.
    /// </param>
    /// <exception cref="SmallLockConcurrencyExceededException">Can't enter because the maximum count of concurrency level is already reached.</exception>
    /// <remarks>
    /// Call <see cref="Exit(int)"/> to release access.
    /// </remarks>
    public void TryEnter(out bool lockTaken, int lockId = 0, TimeSpan timeOut = default)
    {
        TryEnter(out lockTaken, null, lockId, timeOut);
    }

    /// <summary>
    /// Attempt to acquire the lock with an exclusive access
    /// </summary>
    /// <param name="lockTaken">
    /// If <c>true</c> the user will have to call <see cref="Exit(int)"/> because the lock was successfully taken, <c>false</c> otherwise.
    /// The most likely reason for this arg to be <c>false</c> is we couldn't acquire the lock in due time.
    /// </param>
    /// <param name="resumedOnCrashedProcess">If the previous holder of the lock was another process that crashed without releasing it,
    /// we will be set to <c>true</c> to indicate we couldn't wait the lock to be released to switch to the next in line.</param>
    /// <param name="lockId">Id to assign to the lock, if <c>0</c> the thread's <c>ManagedThreadId</c> will be used.</param>
    /// <param name="timeOut">
    /// If the lock couldn't be acquired during this time span, we give up.
    /// <c>default(TimeSpan)</c> will be considered as waiting forever.
    /// </param>
    /// <exception cref="SmallLockConcurrencyExceededException">Can't enter because the maximum count of concurrency level is already reached.</exception>
    /// <remarks>
    /// Call <see cref="Exit(int)"/> to release access.
    /// </remarks>
    public void TryEnter(out bool lockTaken, out bool resumedOnCrashedProcess, int lockId = 0, TimeSpan timeOut = default)
    {
        resumedOnCrashedProcess = false;
        var addr = (bool*)Unsafe.AsPointer(ref resumedOnCrashedProcess);
        TryEnter(out lockTaken, addr, lockId, timeOut);
    }

    #endregion

    #endregion

    #region Fields

    private readonly Header* _header;

    // This will act as a cyclic buffer, FIFO.
    private readonly ulong* _items;

    #endregion

    #region Constructors

    private SmallLock(MemorySegment segment, bool create)
    {
        _header = segment.Cast<Header>().Address;
        _items = (ulong*)(_header + 1);
        if (create)
        {
            var remainingSize = segment.Length - sizeof(Header);
            var queueCapacity = remainingSize / sizeof(long);
            if (queueCapacity > ushort.MaxValue)
            {
                ThrowHelper.SmallLockConcurrencyTooBig(queueCapacity);
            }
            _header->LockedBy = 0;
            _header->ReentrencyCounter = 0;
            _header->QueueAccessControl = 0;
            _header->QueueCapacity = (ushort)queueCapacity;
            _header->QueueHead = 0;
            _header->QueueTail = 0;
        }
    }

    #endregion

    #region Private methods

    private void Dequeue()
    {
        QueueAccessBegin();
        Debug.Assert(_header->QueueCount > 0, "Dequeue of called more than Queue...");
        
        _header->QueueHead++;
        if (_header->QueueHead == _header->QueueCapacity)
        {
            _header->QueueHead = 0;
        }

        // If we dequeue the last item, set 0 in LockedBy to signify the queue is empty and the lock is free
        // Otherwise set the FullLockId of the next in line.
        _header->LockedBy = (_header->QueueTail == _header->QueueHead) ? 0 : _items[_header->QueueHead];

        _header->QueueCount--;
        QueueAccessEnd();
    }

    private int Enqueue(ulong item)
    {
        QueueAccessBegin();
        var capacity = _header->QueueCapacity;
        var res = -1;
        if (QueueCount < capacity)
        {
            res = _header->QueueTail;
            _items[_header->QueueTail++] = item;
            if (_header->QueueTail == capacity)
            {
                _header->QueueTail = 0;
            }

            _header->QueueCount++;
        }

        QueueAccessEnd();
        return res;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void QueueAccessBegin()
    {
        // Get exclusive access
        if (Interlocked.CompareExchange(ref _header->QueueAccessControl, 1, 0) != 0)
        {
            var spinWait = new SpinWait();
            while (Interlocked.CompareExchange(ref _header->QueueAccessControl, 1, 0) != 0)
            {
                spinWait.SpinOnce();
            }
        }
    }

    private void QueueAccessEnd() => Interlocked.CompareExchange(ref _header->QueueAccessControl, 0, 1);

    private void RemoveFromQueue(int entryIndex)
    {
        QueueAccessBegin();

        // Move everything beyond entryIndex one entry before
        var end = _header->QueueTail + (_header->QueueTail > entryIndex ? 0 : _header->QueueCapacity);
        for (var i = entryIndex; i < end - 1; i++)
        {
            _items[i % _header->QueueCapacity] = _items[(i + 1) % _header->QueueCapacity];
        }

        _header->QueueTail = (ushort)((_header->QueueTail > 0) ? _header->QueueTail-1 : (_header->QueueCapacity - 1));
        --_header->QueueCount;

        // If we removed the first entry of the queue, update LockedBy with the new first entry
        if (entryIndex == _header->QueueHead)
        {
            _header->LockedBy = _items[_header->QueueHead];
        }
        
        QueueAccessEnd();
    }

    private void TryEnter(out bool lockTaken, bool* resumeOnCrashedProcess=null, int lockId = 0, TimeSpan timeOut = default)
    {
        // Ensure lockTaken is false at this stage
        lockTaken = false;
        
        // If no lockId is given we use the ManagedThreadId
        if (lockId == 0)
        {
            lockId = Environment.CurrentManagedThreadId;
        }

        if (resumeOnCrashedProcess != null)
        {
            *resumeOnCrashedProcess = false;
        }
        
        // Compute the full Lock Id with the process and the given lockId
        ulong fullLockId = default;
        fullLockId.Pack(IProcessProvider.Singleton.CurrentProcessId, lockId);
        
        // Attempt to get the lock
        var currentLockedBy = Interlocked.CompareExchange(ref _header->LockedBy, fullLockId, 0);
        
        // Fast path, we got the lock because it was free (lockedBy returned 0), enqueue and quit
        if (currentLockedBy == 0)
        {
            _header->ReentrencyCounter = 1;
            Enqueue(fullLockId);

            lockTaken = true;
            return;
        }
        
        // Check for reentrency, if the lockId is the same than lockedBy, it means we already set this lock somewhere in the calling stack
        if (currentLockedBy == fullLockId)
        {
            ++_header->ReentrencyCounter;
            lockTaken = true;
            return;
        }

        // From this point we know the Lock is held by someone else, we need to add ourselves in the queue and wait our turn
        var entryIndex = Enqueue(fullLockId);
        if (entryIndex == -1)
        {
            // false returned means we reach the max concurrency capacity, throw en exception, the user is using more concurrent access than specified
            //  at construction. lockTaken is false, the caller won't have to call Exist
            ThrowHelper.SmallLockConcurrencyCapacityReached(_header->QueueCapacity);
        }

        // We enter a loop
        var waitUntil = (timeOut.Ticks == 0) ? null : new DateTime?(DateTime.UtcNow + timeOut);
        var sw = new SpinWait();
        var processProvider = IProcessProvider.Singleton;
        currentLockedBy = _header->LockedBy;
        while (currentLockedBy != fullLockId)
        {
            // Reach the max time to wait?
            if (waitUntil!=null && DateTime.UtcNow >= waitUntil)
            {
                RemoveFromQueue(entryIndex);
                break;
            }

            // Check if the process that currently holds the lock still exist, otherwise we could wait forever without a specific case
            // If it no longer exists and we are the next in line, we remove this lock from the queue to resume to the next in line (which is us)
            var lockedByProcess = currentLockedBy.HighS();
            if (processProvider.IsProcessAlive(lockedByProcess) == false && (entryIndex == ((_header->QueueHead + 1) % _header->QueueCapacity)))
            {
                RemoveFromQueue(_header->QueueHead);
                currentLockedBy = _header->LockedBy;

                if (resumeOnCrashedProcess != null)
                {
                    *resumeOnCrashedProcess = true;
                }
                continue;
            }

            // Wait a bit before looping
            sw.SpinOnce();
            currentLockedBy = _header->LockedBy;
        }
        
        // Did we successfully got our turn? Set lockTaken accordingly
        lockTaken = currentLockedBy == fullLockId;
    }

    #endregion

    #region Inner types

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        #region Fields

        public ulong LockedBy;

        public int QueueAccessControl;

        // The number of entries in _items
        public ushort QueueCapacity;

        // Number of items currently in the queue
        public ushort QueueCount;

        // The index of the first occupied entry in the items
        public ushort QueueHead;

        // Index of the first free entry
        public ushort QueueTail;
        public int ReentrencyCounter;

        #endregion
    }

    #endregion
}