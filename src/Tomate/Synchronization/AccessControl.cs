using System.Diagnostics;
using System.Runtime.InteropServices;
using JetBrains.Annotations;

namespace Tomate;

/// <summary>
/// Synchronization type that allows multiple concurrent shared access or one exclusive.
/// Doesn't allow re-entrant calls, burn CPU cycle on wait, using <see cref="SpinWait"/>
/// Costs 8 bytes of data.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
[PublicAPI]
public struct AccessControl
{
    // This type can be used on Memory Mapped File for interprocess synchronization, so the Managed Thread Id is not enough as
    //  a discriminant, we also use the Process Id to store a unique value in _lockedByThreadId
    private static readonly int ProcessId = Process.GetCurrentProcess().Id;

    /// <summary>
    /// Constructor an instance stored in the given memory segment
    /// </summary>
    /// <param name="segment">The memory segment where the instance will be stored.</param>
    /// <returns>The ref to the instance</returns>
    public static ref AccessControl Construct(MemorySegment<AccessControl> segment)
    {
        ref var ac = ref segment.AsRef();
        ac._lockedByThreadId  = 0;
        ac._sharedUsedCounter = 0;
        return ref ac;
    }

    /// <summary>
    /// Reset the usage of the instance to its default state.
    /// </summary>
    public void Reset()
    {
        _lockedByThreadId = 0;
        _sharedUsedCounter = 0;
    }

    // Don't change this data layout as there is a C++ counterpart of AccessControl and we can use this type with IPC
    private volatile int _lockedByThreadId;
    private volatile int _sharedUsedCounter;

    /// <summary>
    /// Returns the Id of the current Process/Thread that own the lock 
    /// </summary>
    /// <remarks>
    /// <c>0</c> is returned if the Access Control is not locked.
    /// The returned value is a xor combination of the Process Id and the threadId and should be used for information purpose only.
    /// </remarks>
    public int LockedById => _lockedByThreadId;
    
    /// <summary>
    /// Returns the count of concurrent accesses.
    /// </summary>
    public int SharedUsedCounter => _sharedUsedCounter;

    /// <summary>
    /// Enter a shared access
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared access is typically used to allow concurrent readers, that is, many processes/threads can concurrently enter a shared access.
    /// If there is already an exclusive access being hold, the calling thread will wait until it can enter the shared access (which is the exclusive access
    /// to be released).
    /// While in shared access, if a process/thread attempts to enter an exclusive access calling <see cref="EnterExclusiveAccess"/>,the calling thread will
    /// wait until there is no shared access at all to satisfy the request.
    /// </para>
    /// <para>
    /// You have the possibility to attempt transforming your shared access to an exclusive one by calling <see cref="TryPromoteToExclusiveAccess"/>. If the
    /// calling thread is the only shared access the promotion will succeed and the thread will then become the exclusive owner. If there is at least another
    /// process/thread holding a shared access, the promotion attempt will fail.
    /// </para>
    /// <para>
    /// Be sure to call <see cref="ExitSharedAccess"/> when you want to release the access. 
    /// </para>
    /// </remarks>
    public void EnterSharedAccess()
    {
        // Currently exclusively locked, wait it's over
        if (_lockedByThreadId != 0)
        {
            var sw = new SpinWait();
            while (_lockedByThreadId != 0)
            {
                sw.SpinOnce();
            }
        }

        // Increment shared usage
        Interlocked.Increment(ref _sharedUsedCounter);

        // Double check on exclusive, in a loop because we need to restore the shared counter to prevent deadlock
        // So we loop until we meet the criteria
        while (_lockedByThreadId != 0)
        {
            Interlocked.Decrement(ref _sharedUsedCounter);
            var sw = new SpinWait();
            while (_lockedByThreadId != 0)
            {
                sw.SpinOnce();
            }
            Interlocked.Increment(ref _sharedUsedCounter);
        }
    }

    /// <summary>
    /// Exit a previously entered shared access
    /// </summary>
    /// <remarks>
    /// A process/thread calling <see cref="EnterSharedAccess"/> must call this method to release the shared access.
    /// </remarks>
    public void ExitSharedAccess() => Interlocked.Decrement(ref _sharedUsedCounter);

    /// <summary>
    /// Enter an exclusive access
    /// </summary>
    /// <remarks>
    /// There can be only one process/thread being in the exclusive access state at a given time.
    /// If the `AccessControl` instance is being in shared mode or exclusive hold by another process/thread, the calling thread will wait until this call can
    /// be satisfied.
    /// Note that this method is not reentrant, if somewhere lower in the calling stack a method successfully entered an exclusive access, this call will
    /// wait indefinitely.
    /// </remarks>
    public void EnterExclusiveAccess()
    {
        var ct = CurrentProcessThreadId;

        // Fast path: exclusive lock works immediately
        if (Interlocked.CompareExchange(ref _lockedByThreadId, ct, 0) == 0)
        {
            // No shared use: we're good to go
            if (_sharedUsedCounter == 0)
            {
                return;
            }

            // Otherwise wait the shared use is over
            var sw = new SpinWait();
            while (_sharedUsedCounter != 0)
            {
                sw.SpinOnce();
            }

            return;
        }

        // Slow path: wait the shared concurrent use is over
        {
            var sw = new SpinWait();
            while (Interlocked.CompareExchange(ref _lockedByThreadId, ct, 0) != 0)
            {
                sw.SpinOnce();
            }

            // Exit if there's no shared access neither
            if (_sharedUsedCounter == 0)
            {
                return;
            }

            // Otherwise wait the shared access to be over
            while (_sharedUsedCounter != 0)
            {
                sw.SpinOnce();
            }
        }
    }

    /// <summary>
    /// Attempt to promote a previously set shared access to exclusive
    /// </summary>
    /// <returns>
    /// <c>true</c> if the calling process/thread could switch to exclusive, <c>false</c> if the attempt failed (other processes/threads are also holding a
    /// shared access).
    /// </returns>
    /// <remarks>
    /// This method will either switch from shared to exclusive or stay shared. Based on the outcome, you have to call the corresponding Exit method.
    /// </remarks>
    public bool TryPromoteToExclusiveAccess()
    {
        var ct = CurrentProcessThreadId;

        // We can enter only if we are the only user (_sharedUsedCounter == 1)
        if (_sharedUsedCounter != 1)
        {
            return false;
        }

        // Try to exclusively lock
        if (Interlocked.CompareExchange(ref _lockedByThreadId, ct, 0) != 0)
        {
            return false;
        }

        // Double check now we're locked that we're still the only shared user
        if (_sharedUsedCounter != 1)
        {
            // Another concurrent user came at the same time, remove exclusive access and quit with failure
            _lockedByThreadId = 0;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Demote from exclusive to shared access
    /// </summary>
    /// <remarks>
    /// The calling process/thread must be in a valid exclusive state for this method to be called.
    /// </remarks>
    public void DemoteFromExclusiveAccess() => _lockedByThreadId = 0;

    /// <summary>
    /// Release the exclusive access on the calling process/thread
    /// </summary>
    public void ExitExclusiveAccess() => _lockedByThreadId = 0;
    
    private static int CurrentProcessThreadId => Environment.CurrentManagedThreadId ^ ProcessId;
}