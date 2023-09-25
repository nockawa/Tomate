using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Tomate;

public class SmallLockConcurrencyExceededException : Exception
{
    public SmallLockConcurrencyExceededException(string message) : base(message)
    {
        
    }
}

[PublicAPI]
[StructLayout(LayoutKind.Sequential)]
public unsafe struct SmallLock
{
    private readonly Header* _header;
    
    // This will act as a cyclic buffer, FIFO.
    private readonly ulong* _items;

    //private static readonly int _processId = Environment.ProcessId;

    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public ulong LockedBy;
        public int ReentrencyCounter;
        public int ProcessProviderId;
        public int QueueAccessControl;
        // The index of the first occupied entry in the items
        public ushort QueueHead;
        // Index of the first free entry
        public ushort QueueTail;
        // The number of entries in _items
        public ushort QueueCapacity;
        // Number of items currently in the queue
        public ushort QueueCount;
    }

    private SmallLock(MemorySegment segment, IProcessProvider processProvider, bool create)
    {
        _header = segment.Cast<Header>().Address;
        _items = (ulong*)(_header + 1);
        if (create)
        {
            Debug.Assert(processProvider != null, "You have to pass a valid ProcessProvider");
            var remainingSize = segment.Length - sizeof(Header);
            var queueCapacity = remainingSize / sizeof(long);
            if (queueCapacity > ushort.MaxValue)
            {
                ThrowHelper.SmallLockConcurrencyTooBig(queueCapacity);
            }
            _header->LockedBy = 0;
            _header->ProcessProviderId = processProvider.ProcessProviderId;
            _header->ReentrencyCounter = 0;
            _header->QueueAccessControl = 0;
            _header->QueueCapacity = (ushort)queueCapacity;
            _header->QueueHead = 0;
            _header->QueueTail = 0;
        }
    }

    public static int ComputeSegmentSize(ushort concurrencyLevel) => sizeof(Header) + sizeof(long)*concurrencyLevel;
    public static SmallLock Create(MemorySegment segment, IProcessProvider processProvider) => new(segment, processProvider, true);
    public static SmallLock Map(MemorySegment segment) => new(segment, null, false);

    public IProcessProvider ProcessProvider => IProcessProvider.GetProvider(_header->ProcessProviderId);
    public bool IsEntered => _header->LockedBy != 0;
    public int LockedByProcess => _header->LockedBy.HighS();
    public int LockId => _header->LockedBy.LowS();
    public int ConcurrencyCapacity => _header->QueueCapacity;
    public int ConcurrencyCounter => _header->QueueCount;
    
    private int QueueCount => _header->QueueCount;

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
        fullLockId.Pack(ProcessProvider.CurrentProcessId, lockId);
        
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
        var processProvider = IProcessProvider.GetProvider(_header->ProcessProviderId);
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
        fullLockId.Pack(ProcessProvider.CurrentProcessId, lockId);

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
}